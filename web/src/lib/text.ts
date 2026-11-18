/**
 * 取一段文本的**第一行**（去掉首尾空白），超长时截断并加省略号。
 *
 * 用途：想法没有标题了，但界面仍然需要一个能认出这条想法的短文本 ——
 * 勾选框的可访问名称、合并对话框的预览行都用它。
 *
 * @param text 原文。
 * @param maxLength 最多保留多少个字符；超出的部分用「…」收尾。
 */
export function firstLine(text: string, maxLength = 40): string {
  const line = text.split('\n')[0].trim();
  if (line.length <= maxLength) {
    return line;
  }
  // 「…」占一个字符，所以总长仍然是 maxLength。
  return `${line.slice(0, maxLength - 1)}…`;
}
