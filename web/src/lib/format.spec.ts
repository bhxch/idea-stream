import { describe, expect, it } from 'vitest';
import { formatRelativeTime } from './format';

describe('formatRelativeTime', () => {
  it('formatRelativeTime', () => {
    const now = new Date('2026-11-11T12:00:00Z');
    expect(formatRelativeTime('2026-11-11T11:59:30Z', now)).toBe('刚刚');
    expect(formatRelativeTime('2026-11-11T11:30:00Z', now)).toBe('30 分钟前');
    expect(formatRelativeTime('2026-11-11T09:00:00Z', now)).toBe('3 小时前');
    expect(formatRelativeTime('2026-11-10T12:00:00Z', now)).toBe('昨天');
    expect(formatRelativeTime('2026-11-01T12:00:00Z', now)).toBe('11月1日');
  });
});
