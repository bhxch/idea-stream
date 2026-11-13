/**
 * webhook 的事件类型清单。
 *
 * **写死这一个清单**（与后端的 `EventTypes.All` 一致）：
 * 后端只认这 8 种，填错的名字会在保存时被拒（400 `invalid_event_type`）。
 */

/** 可以订阅的事件类型。 */
export const WEBHOOK_EVENT_TYPES = [
  'idea.created',
  'idea.updated',
  'idea.deleted',
  'idea.restored',
  'idea.merged',
  'project.created',
  'project.updated',
  'project.deleted',
] as const;

/** 事件类型的中文说明（页面上显示用，订阅事件那一栏）。 */
export const WEBHOOK_EVENT_LABELS: Record<string, string> = {
  'idea.created': '想法新建',
  'idea.updated': '想法修改',
  'idea.deleted': '想法进回收站',
  'idea.restored': '想法从回收站恢复',
  'idea.merged': '想法合并',
  'project.created': '项目新建',
  'project.updated': '项目修改',
  'project.deleted': '项目进回收站',
};
