import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { dirname, join } from 'node:path';

import { defineConfig, devices } from '@playwright/test';

/**
 * Playwright E2E 配置。
 *
 * 约定见 web/e2e/README.md。
 *
 * 端口与库：**一轮 = 一个端口 + 一个库**。
 *
 *   端口从 5300 起找空闲的（E2E_PORT 可以指定），库落在
 *   `web/e2e/.tmp/run-<端口>/e2e.db` —— 每轮一个目录。
 *   于是两轮并发跑时，两边端口不同、库不同、产物目录也不同，
 *   一轮的复位不会踩到另一轮。
 *
 *   端口怎么传下去的：没设 `E2E_PORT` 时，这里自己挑一个空闲的并写进
 *   `process.env.E2E_PORT`。`use.baseURL` 与 `webServer.url` 都用它；
 *   `webServer.env` 默认继承 `process.env`，于是 webServer 拉起的
 *   `start-server.sh` 读到的也是**同一个**端口 —— 服务与 baseURL 不会错位。
 */

// ── 端口 ─────────────────────────────────────────────────────────────────

/** e2e 目录（web/e2e）。从当前目录往上找，兼容从仓库根或 web/ 里起。 */
function findE2EDir(): string {
  let dir = process.cwd();
  for (let i = 0; i < 8; i++) {
    for (const candidate of [join(dir, 'e2e'), join(dir, 'web', 'e2e')]) {
      if (existsSync(join(candidate, 'lib', 'common.sh'))) {
        return candidate;
      }
    }
    const parent = dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  throw new Error('找不到 web/e2e 目录（lib/common.sh 不在预期位置）');
}

/**
 * 本轮的端口。
 *
 * - `E2E_PORT` 已经设了 → 就用它（谁指定谁负责，端口被抢了也是它的事）；
 * - 没设 → 让 `e2e/lib/common.sh` 挑一个空闲的（从 5300 起）并预约。
 *
 * 挑端口的逻辑只有一份，在 common.sh 里 —— bash 脚本与这份配置用的是同一套，
 * 两边不会挑出不一样的端口（挑不一样的话，服务与 baseURL 就对不上了）。
 */
function resolvePort(): number {
  const fromEnv = process.env.E2E_PORT;
  if (fromEnv && /^\d+$/.test(fromEnv)) {
    return Number(fromEnv);
  }
  const e2eDir = findE2EDir();
  const result = spawnSync('bash', [join(e2eDir, 'lib', 'common.sh'), '--reserve-port'], {
    cwd: e2eDir,
    encoding: 'utf8',
  });
  if (result.status !== 0) {
    throw new Error(`挑不到空闲端口：${(result.stderr || result.stdout || '').trim()}`);
  }
  const port = Number(result.stdout.trim());
  if (!Number.isInteger(port) || port <= 0) {
    throw new Error(`挑端口的脚本没给出端口：${result.stdout}`);
  }
  return port;
}

export const E2E_PORT = resolvePort();
// 写回环境变量：webServer.env 默认继承 process.env，
// 于是 start-server.sh 起的服务用的就是同一个端口。
process.env.E2E_PORT = String(E2E_PORT);

export const E2E_BASE_URL = `http://127.0.0.1:${E2E_PORT}`;

/** 本轮的产物目录名（按端口分）：两轮并发跑不会互相删产物。 */
const E2E_RUN_TAG = `run-${E2E_PORT}`;

// 跑完把服务停掉（跟 run-e2e.sh 的默认行为一致）——
// 不然 `cd web && npm run e2e` 会在机器上留一个占着端口的服务。
//
// 两种情况不做：
//   - **worker 进程**里（TEST_WORKER_INDEX 有值）：worker 退出不代表这一轮结束；
//   - E2E_NO_AUTOSTOP=1（run-e2e.sh 设的）：服务归那个脚本管
//     （它默认跑完就停，加 --keep 时还要留着 —— 这里停了就白跑）。
if (process.env.TEST_WORKER_INDEX === undefined && process.env.E2E_NO_AUTOSTOP !== '1') {
  const e2eDir = findE2EDir();
  process.on('exit', () => {
    try {
      execFileSync('bash', [join(e2eDir, 'start-server.sh'), '--stop'], {
        cwd: e2eDir,
        env: { ...process.env, E2E_PORT: String(E2E_PORT) },
        stdio: 'ignore',
      });
    } catch {
      // 收摊失败不该影响测试的退出码。
    }
  });
}

export default defineConfig({
  // 用例放在 web/e2e/ 下（fixtures.ts、smoke.spec.ts 与各分区的用例）。
  testDir: './e2e',

  // ⚠️ workers 固定为 1：一轮里所有用例共用**同一个数据库**（E2E 服务只有一个实例），
  // 而每个用例前都会复位一次库。并行跑的话，A 的复位会把 B 刚造的数据清掉 ——
  // 表现为「用例之间互相干扰」，而且是偶发的，极难定位。
  //
  // （两轮**之间**已经隔离了：端口不同、库不同。见文件头。）
  workers: 1,
  fullyParallel: false,

  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  timeout: 30_000,
  expect: { timeout: 5_000 },

  // 产物目录按轮次分：web/test-results/run-<端口>/。
  // 两轮并发跑时各自清各自的目录 —— 以前两轮写同一个 test-results/，
  // 互相删产物，失败时留下的 trace 会变成 ENOENT ... traces/*.network。
  outputDir: `test-results/${E2E_RUN_TAG}`,

  reporter: [
    ['list'],
    // 报告输出到 web/playwright-report/run-<端口>/（相对配置文件所在目录）。
    ['html', { outputFolder: `playwright-report/${E2E_RUN_TAG}`, open: 'never' }],
  ],

  use: {
    baseURL: E2E_BASE_URL,
    // 时间与语言钉死：界面上的时间显示与排序断言才稳定。
    locale: 'zh-CN',
    timezoneId: 'Asia/Shanghai',
    // 失败时保留 trace 与截图（重跑时能看清现场）。
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'off',
    actionTimeout: 10_000,
    navigationTimeout: 15_000,
  },

  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],

  webServer: {
    // 起服务（幂等）：端口上已有健康服务就直接复用。
    command: 'bash e2e/start-server.sh',
    url: `${E2E_BASE_URL}/api/health`,
    timeout: 300_000,
    reuseExistingServer: true,
    cwd: '.',
    // 告诉脚本「你是被 Playwright 的 webServer 拉起来的」：
    // 那种情况下脚本不能立刻退出（Playwright 一看到 webServer 进程退出就报
    // "exited early"），得驻留着等 Playwright 收工。
    //
    // ⚠️ 这里只**加**变量，不替换：webServer.env 默认继承 process.env，
    // E2E_PORT（上面写进去的那个）会一起传下去。
    env: { E2E_WEB_SERVER: '1' },
  },
});
