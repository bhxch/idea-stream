import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useTagMode } from './useTagMode';

/**
 * 内存版 localStorage。
 *
 * jsdom 自带的 localStorage 是**跨测试文件共享**的同一个实例，
 * 每个测试装一个干净的替身，读写互不干扰。
 */
function createMemoryStorage(): Storage {
  const map = new Map<string, string>();
  return {
    get length(): number {
      return map.size;
    },
    clear(): void {
      map.clear();
    },
    getItem(key: string): string | null {
      return map.get(key) ?? null;
    },
    key(index: number): string | null {
      return [...map.keys()][index] ?? null;
    },
    removeItem(key: string): void {
      map.delete(key);
    },
    setItem(key: string, value: string): void {
      map.set(key, value);
    },
  };
}

describe('useTagMode', () => {
  beforeEach(() => {
    vi.stubGlobal('localStorage', createMemoryStorage());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('useTagMode 默认 or 并写入 localStorage', () => {
    const { tagMode, setTagMode } = useTagMode();

    expect(tagMode.value).toBe('or');

    setTagMode('and');

    expect(globalThis.localStorage.getItem('idea.tagMode')).toBe('and');
  });

  it('useTagMode 读取已存的 and', () => {
    globalThis.localStorage.setItem('idea.tagMode', 'and');

    const { tagMode } = useTagMode();

    expect(tagMode.value).toBe('and');
  });

  it('useTagMode 非法值回落 or', () => {
    globalThis.localStorage.setItem('idea.tagMode', 'xxx');

    const { tagMode } = useTagMode();

    expect(tagMode.value).toBe('or');
  });
});
