using System.Text;

namespace IdeaApp.Tests;

/// <summary>
/// 扫源码用的文本工具。
///
/// <para>
/// 守卫测试要看的是「代码里真的写了什么」，不是「注释里说了什么」——
/// 注释里常写着示例代码（<c>/// catch (...) =&gt; ApiException(400, "x", …)</c>），
/// 不去掉的话扫描器会把它当成真的代码；反过来，被注释掉的代码也不该算数。
/// </para>
///
/// <para>
/// 放在这里而不是某个测试类的私有方法里：不止一个守卫要扫源码
/// （错误码清单的守卫、415 声明与机制的配对守卫），规则得是同一份 ——
/// 两边各写一份，迟早有一边认不全某种写法。
/// </para>
/// </remarks>
internal static class SourceText
{
    /// <summary>
    /// 去掉源码里的注释（字符串/字符字面量的内容原样保留）。
    /// </summary>
    /// <remarks>
    /// 字面量里的 <c>//</c> 不是注释，所以不能简单按行首砍。
    /// </remarks>
    public static string StripComments(string text)
    {
        var builder = new StringBuilder(text.Length);
        var index = 0;

        while (index < text.Length)
        {
            var current = text[index];

            if (current == '"')
            {
                var end = SkipStringLiteral(text, index);
                builder.Append(text, index, end - index);
                index = end;
                continue;
            }

            if (current == '\'')
            {
                var end = SkipCharLiteral(text, index);
                builder.Append(text, index, end - index);
                index = end;
                continue;
            }

            if (current == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                while (index < text.Length && text[index] != '\n')
                {
                    builder.Append(' ');
                    index++;
                }

                continue;
            }

            if (current == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                builder.Append("  ");
                index += 2;

                while (index < text.Length
                    && !(text[index] == '*' && index + 1 < text.Length && text[index + 1] == '/'))
                {
                    builder.Append(text[index] == '\n' ? '\n' : ' ');
                    index++;
                }

                if (index < text.Length)
                {
                    builder.Append("  ");
                    index += 2;
                }

                continue;
            }

            builder.Append(current);
            index++;
        }

        return builder.ToString();
    }

    /// <summary>跳过一个字符串字面量，返回它结束后的下标。</summary>
    /// <remarks>
    /// 三种写法都要认：普通 <c>"…"</c>（<c>\</c> 转义）、逐字 <c>@"…"</c>（<c>""</c> 转义）、
    /// 原始 <c>"""…"""</c>。
    /// </remarks>
    public static int SkipStringLiteral(string text, int index)
    {
        if (index + 2 < text.Length && text[index + 1] == '"' && text[index + 2] == '"')
        {
            var closing = text.IndexOf("\"\"\"", index + 3, StringComparison.Ordinal);
            return closing < 0 ? text.Length : closing + 3;
        }

        var verbatim = index > 0 && text[index - 1] == '@';
        var cursor = index + 1;

        while (cursor < text.Length)
        {
            var current = text[cursor];

            if (!verbatim && current == '\\')
            {
                cursor += 2;
                continue;
            }

            if (current == '"')
            {
                if (verbatim && cursor + 1 < text.Length && text[cursor + 1] == '"')
                {
                    cursor += 2;
                    continue;
                }

                return cursor + 1;
            }

            cursor++;
        }

        return text.Length;
    }

    /// <summary>跳过一个字符字面量，返回它结束后的下标。</summary>
    public static int SkipCharLiteral(string text, int index)
    {
        var cursor = index + 1;

        while (cursor < text.Length)
        {
            var current = text[cursor];

            if (current == '\\')
            {
                cursor += 2;
                continue;
            }

            if (current == '\'')
            {
                return cursor + 1;
            }

            cursor++;
        }

        return text.Length;
    }

    /// <summary>下标所在行（从 1 开始）。</summary>
    public static int LineNumber(string text, int index)
    {
        var line = 1;

        for (var cursor = 0; cursor < index && cursor < text.Length; cursor++)
        {
            if (text[cursor] == '\n')
            {
                line++;
            }
        }

        return line;
    }
}
