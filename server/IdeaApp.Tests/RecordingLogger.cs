using Microsoft.Extensions.Logging;

namespace IdeaApp.Tests;

/// <summary>
/// 把日志收集起来的测试替身。
///
/// <para>
/// 为什么不用 <c>Microsoft.Extensions.Logging.Testing</c> 里的 <c>TestLogger</c>：
/// 那是另一个 NuGet 包，而这个仓库不引新的依赖（测试也一样）。
/// 自己写一个更薄：一个 <see cref="ILogger{TCategoryName}"/> 实现，把每条日志留下来。
/// </para>
///
/// <para>
/// 消息用 <c>formatter</c> 渲染 —— 那是「结构化日志模板 + 参数」展开成文本的地方，
/// 与真实日志落到控制台/文件上的内容一致（所以断言消息里能看到 idea id 与操作名）。
/// </para>
/// </summary>
/// <typeparam name="TCategoryName">日志分类名（泛型参数只是为了满足 <see cref="ILogger{TCategoryName}"/>）。</typeparam>
public sealed class RecordingLogger<TCategoryName> : ILogger<TCategoryName>
{
    private readonly List<LogEntry> _entries = new();

    /// <summary>收集到的全部日志（按记录顺序）。</summary>
    public IReadOnlyList<LogEntry> Entries => _entries;

    /// <summary>其中的 warning 级日志。</summary>
    public IReadOnlyList<LogEntry> Warnings =>
        _entries.Where(entry => entry.Level == LogLevel.Warning).ToList();

    /// <summary>记一条日志。</summary>
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
    }

    /// <summary>开始一个作用域：这里用不上，返回 null 即可（接口允许）。</summary>
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <summary>全部级别都收。</summary>
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <summary>一条日志。</summary>
    /// <param name="Level">级别。</param>
    /// <param name="Message">渲染后的消息文本。</param>
    /// <param name="Exception">附带的异常（可为 null）。</param>
    public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
}
