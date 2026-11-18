import { describe, expect, it } from 'vitest';
import { firstLine } from './text';

describe('firstLine', () => {
  it('firstLine_takes_the_text_before_the_first_newline', () => {
    expect(firstLine('第一行\n第二行')).toBe('第一行');
  });

  it('firstLine_trims_the_whitespace', () => {
    expect(firstLine('  第一行  \n第二行')).toBe('第一行');
  });

  it('firstLine_truncates_long_lines', () => {
    const result = firstLine('x'.repeat(60));

    expect(result.length).toBeLessThanOrEqual(40);
    expect(result.endsWith('…')).toBe(true);
  });

  it('firstLine_of_empty_text_is_empty', () => {
    expect(firstLine('')).toBe('');
    expect(firstLine('\n\n')).toBe('');
  });
});
