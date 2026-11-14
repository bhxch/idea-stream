import { defineConfig, devices } from '@playwright/test';

/**
 * Playwright E2E 配置。
 *
 * 约定见 web/e2e/README.md。
 *
 * 端口：5299 是 E2E 专用端口。5080 被沙箱里的别的进程占着，
 * 5199 是 server/scripts/aot-smoke.sh 的端口 —— 两个都不能抢。
 */
export const E2E_PORT = 5299;
export const E2E_BASE_URL = `http://127.0.0.1:${E2E_PORT}`;

export default defineConfig({
  // 用例放在 web/e2e/ 下（fixtures.ts、smoke.spec.ts 与各分区的用例）。
  testDir: './e2e',

  // ⚠️ workers 固定为 1：所有用例共用**同一个数据库**（E2E 服务只有一个实例），
  // 而每个用例前都会复位一次库。并行跑的话，A 的复位会把 B 刚造的数据清掉 ——
  // 表现为「用例之间互相干扰」，而且是偶发的，极难定位。
  workers: 1,
  fullyParallel: false,

  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  timeout: 30_000,
  expect: { timeout: 5_000 },

  reporter: [
    ['list'],
    // 报告输出到 web/playwright-report/（相对配置文件所在目录）。
    ['html', { outputFolder: 'playwright-report', open: 'never' }],
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
    env: { E2E_WEB_SERVER: '1' },
  },
});
