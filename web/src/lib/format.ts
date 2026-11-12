/**
 * 相对时间格式化。
 *
 * 消息流里每条想法都带一个时间戳，显示成「刚刚 / 30 分钟前 / 昨天 / 11月1日」
 * 比 ISO 串友好得多。时间戳由后端给出（UTC，带时区偏移），
 * 这里按**浏览器本地时区**翻译成人话。
 */

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

/**
 * 超过这个天数就不再显示「N 天前」，改显示具体日期。
 *
 * 10 天前说「10 天前」没什么意义，「11月1日」才有信息量。
 */
const DAYS_BEFORE_DATE = 7;

/**
 * 把 ISO 时间戳转成相对时间描述。
 *
 * @param iso 后端给的时间戳（`2026-11-11T12:00:00+00:00` 或 `...Z` 都能解析）
 * @param now 「现在」，默认取当前时间；测试里传固定值
 * @returns `刚刚` / `N 分钟前` / `N 小时前` / `昨天` / `N 天前` / `M月D日`；
 *          时间戳解析不出来时返回空串（不显示时间，而不是显示「NaN 分钟前」）
 */
export function formatRelativeTime(iso: string, now: Date = new Date()): string {
  const timestamp = new Date(iso).getTime();
  if (Number.isNaN(timestamp)) {
    return '';
  }

  const elapsed = now.getTime() - timestamp;

  if (elapsed < MINUTE) {
    return '刚刚';
  }
  if (elapsed < HOUR) {
    return `${Math.floor(elapsed / MINUTE)} 分钟前`;
  }
  if (elapsed < DAY) {
    return `${Math.floor(elapsed / HOUR)} 小时前`;
  }
  if (elapsed < 2 * DAY) {
    return '昨天';
  }
  if (elapsed < DAYS_BEFORE_DATE * DAY) {
    return `${Math.floor(elapsed / DAY)} 天前`;
  }

  const date = new Date(timestamp);
  return `${date.getMonth() + 1}月${date.getDate()}日`;
}
