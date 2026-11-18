/**
 * 夹具自检：把「复位」的契约钉住。
 *
 * 这不是业务用例 —— 它守的是**底座自己的约定**：
 * 每个用例开始前库会被复位，所以用例之间互不干扰、与顺序无关。
 * 改坏了复位（或者应用改了删除语义），这里会先红。
 */
import { test, expect, RESET_ARTIFACT } from './fixtures';

test('复位：造脏数据 → 复位 → 活动状态干净', async ({ api, seed }) => {
  const project = await seed.project('自检项目');
  await seed.idea('自检想法 A');
  await seed.idea('自检想法 B', { projectId: project.id });
  await seed.tag('自检标签');
  await seed.webhook({ name: '自检 webhook' });

  expect((await api.listIdeas()).length).toBe(3);
  expect((await api.listProjects()).length).toBe(2);
  expect((await api.listWebhooks()).length).toBe(1);

  await api.reset();

  // 活动状态：想法 0、项目只剩收件箱、webhook 0。
  expect(await api.listIdeas()).toEqual([]);
  expect((await api.listProjects()).map((p) => p.name)).toEqual(['收件箱']);
  expect(await api.listWebhooks()).toEqual([]);
  // 设置回到默认值。
  expect((await api.getSettings())['project.sort_mode']).toBe('recent');
});

test('复位：回收站里只剩形态统一的残渣', async ({ api, seed }) => {
  const project = await seed.project('残渣项目');
  await seed.idea('会被清掉的想法', { projectId: project.id });

  await api.reset();

  const trash = await api.listTrash();
  // 残渣形态：有想法的项目留一条正文为 e2e-reset 的想法
  //（项目被删时它挂在容器条目下）。
  const artifacts = trash.flatMap((entry) =>
    entry.kind === 'project' ? entry.ideas : entry.idea ? [entry.idea] : [],
  );
  expect(artifacts.length).toBeGreaterThan(0);
  for (const artifact of artifacts) {
    expect(artifact.content).toBe(RESET_ARTIFACT);
  }
  // 业务数据不留残渣。
  expect(JSON.stringify(trash)).not.toContain('会被清掉的想法');
});

test('复位：用例之间互不干扰（上一个用例造的数据看不见了）', async ({ api, seed }) => {
  // 这个用例之前的那条用例造过「自检想法 A/B」与「自检项目」。
  // beforeEach 的复位应该把它们清干净了。
  expect(await api.listIdeas()).toEqual([]);
  expect((await api.listProjects()).map((p) => p.name)).toEqual(['收件箱']);
});

test('复位：项目删掉之后，它的项目级 webhook 不会变成孤儿', async ({ api, seed }) => {
  const project = await seed.project('会被删掉的项目');
  const webhook = await seed.webhook({
    name: '项目级 webhook',
    scope: 'project',
    projectId: project.id,
  });
  // 项目级的 webhook 创建时就是**启用**的。
  expect(webhook.enabled).toBe(true);

  // 项目一软删就从 GET /api/projects 里消失了 —— 但 webhook 还在
  // （GET /api/webhooks?projectId=<已删项目id> 仍然列得出来）。
  await api.deleteProject(project.id);
  expect((await api.listProjects()).map((p) => p.id)).not.toContain(project.id);
  expect(await api.listWebhooks(project.id)).toHaveLength(1);

  await api.reset();

  // 复位之后那个 webhook 不在了：不然它会一直启用着，
  // 每次复位都给死地址投递，把投递队列塞满。
  expect(await api.listWebhooks(project.id)).toEqual([]);
});
