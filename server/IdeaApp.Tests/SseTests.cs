using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace IdeaApp.Tests;

/// <summary>
/// SSE 端点的集成测试：帧格式、心跳、断线补发与 resync。
///
/// 这些测试从**客户端的角度**看 SSE：连上 <c>GET /api/events</c>，
/// 读帧、断开、带 <c>Last-Event-ID</c> 重连。
///
/// 一条贯穿全篇的前提：事件是**异步广播**的 ——
/// 写接口返回时，事件可能还没进服务端缓冲。
/// 要断言「服务端已经收到某条事件」，得先从流里读到它。
/// </summary>
public sealed class SseTests
{
    /// <summary>等一帧到达的上限。超时即测试失败。</summary>
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 写操作之后，订阅端应当收到 <c>idea.created</c> 帧，载荷里带着这条想法。
    /// </summary>
    [Fact]
    public async Task Sse_stream_emits_created_event_after_write()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();
        using var connection = await ConnectAsync(client);

        var ideaId = await CreateIdeaAsync(client, "第一条想法", "标题");

        var frame = await ReadFrameAsync(connection);

        Assert.Contains("event: idea.created", frame);
        Assert.Contains(ideaId, frame);
    }

    /// <summary>
    /// 一帧的格式：<c>id</c> 行带事件 id、<c>event</c> 行带事件类型、
    /// <c>data</c> 行只有一行且是能解析的 JSON。
    /// </summary>
    [Fact]
    public async Task Sse_frame_carries_event_id_and_single_line_json()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();
        using var connection = await ConnectAsync(client);

        var ideaId = await CreateIdeaAsync(client, "第一条想法", "标题");

        var frame = SseFrame.Parse(await ReadFrameAsync(connection));

        // 事件 id：32 位无连字符的小写 hex。
        Assert.NotNull(frame.Id);
        Assert.Matches("^[0-9a-f]{32}$", frame.Id);

        Assert.Equal("idea.created", frame.Event);

        // data 只有一行（多行会被浏览器当成多段数据拼起来）。
        var dataLine = Assert.Single(frame.DataLines);
        Assert.NotNull(frame.Data);

        using var document = JsonDocument.Parse(frame.Data);
        var root = document.RootElement;

        Assert.Equal(frame.Id, root.GetProperty("id").GetString());
        Assert.Equal("idea.created", root.GetProperty("type").GetString());
        Assert.Equal(ideaId, root.GetProperty("idea").GetProperty("id").GetString());
    }

    /// <summary>
    /// 断线重连时带 <c>Last-Event-ID</c>：服务端要先把漏掉的事件补发出来。
    /// </summary>
    [Fact]
    public async Task Sse_reconnect_with_last_event_id_replays_missed_events()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        string firstEventId;
        string secondIdeaId;

        using (var connection = await ConnectAsync(client))
        {
            await CreateIdeaAsync(client, "第一条想法");
            firstEventId = SseFrame.Parse(await ReadFrameAsync(connection)).Id!;

            // 断线期间写的第二条：先在**旧连接**上读到它，
            // 才能确定服务端已经把它放进了缓冲（事件是异步广播的，
            // 写接口返回不代表广播器已经收到）。
            secondIdeaId = await CreateIdeaAsync(client, "断线期间写的想法");
            var second = SseFrame.Parse(await ReadFrameAsync(connection));

            Assert.Equal("idea.created", second.Event);
            Assert.Contains(secondIdeaId, second.Data!);

            // 现在断开：第二条已经在缓冲里，重连时它就是「漏掉的那条」。
        }

        using var reconnected = await ConnectAsync(client, lastEventId: firstEventId);

        var replayed = SseFrame.Parse(await ReadFrameAsync(reconnected));

        Assert.Equal("idea.created", replayed.Event);
        Assert.NotEqual(firstEventId, replayed.Id);

        // 补发的正是断线期间漏掉的那条（不是第一条重发）。
        Assert.Contains(secondIdeaId, replayed.Data!);
    }

    /// <summary>
    /// <c>Last-Event-ID</c> 在服务端缓冲里找不到（太旧、被挤出去了）：
    /// 服务端补不了，只能发一帧 <c>resync</c> 让客户端重新拉完整列表。
    /// </summary>
    [Fact]
    public async Task Sse_resync_frame_sent_when_last_event_id_too_old()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        // 这个 id 从来没出现过（服务端的缓冲里没有它）。
        using var connection = await ConnectAsync(
            client, lastEventId: "0199a7f2-3c4d-7abc-8def-0123456789ab");

        var frame = SseFrame.Parse(await ReadFrameAsync(connection));

        Assert.Equal("resync", frame.Event);

        // resync 是控制帧，不是业务事件：它没有自己的 id，客户端不能拿它当 Last-Event-ID。
        Assert.Null(frame.Id);
    }

    /// <summary>
    /// 心跳：长时间没有事件时，服务端每隔一段时间发一行注释（<c>: ping</c>），
    /// 免得中间代理把空闲连接掐掉。
    /// </summary>
    [Fact]
    public async Task Sse_heartbeat_is_emitted()
    {
        // 心跳间隔做成可配置的：默认 25 秒太长，测试里改成 1 秒。
        using var factory = new TestAppFactory(sseHeartbeatSeconds: 1);
        using var client = factory.CreateClient();
        using var connection = await ConnectAsync(client);

        // 这个测试里没有任何写操作 → 读到的第一帧只能是心跳。
        var frame = await ReadFrameAsync(connection);

        Assert.Equal(": ping", frame);
    }

    /// <summary>连上事件流。</summary>
    private static async Task<SseConnection> ConnectAsync(HttpClient client, string? lastEventId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/events");
        if (lastEventId is not null)
        {
            request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);
        }

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var stream = await response.Content.ReadAsStreamAsync();

        return new SseConnection(response, new StreamReader(stream, Encoding.UTF8));
    }

    /// <summary>读一帧（帧内若干行，空行结尾）。</summary>
    private static async Task<string> ReadFrameAsync(SseConnection connection)
    {
        var lines = new List<string>();

        while (true)
        {
            var line = await connection.Reader.ReadLineAsync().WaitAsync(FrameTimeout);
            Assert.NotNull(line);

            if (line.Length == 0)
            {
                break;
            }

            lines.Add(line);
        }

        return string.Join("\n", lines);
    }

    /// <summary>建一条想法并返回它的 id。</summary>
    private static async Task<string> CreateIdeaAsync(HttpClient client, string content, string? title = null)
    {
        var response = await client.PostAsJsonAsync("/api/ideas", new { content, title });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>一条 SSE 连接（响应 + 行读取器）。</summary>
    private sealed class SseConnection(HttpResponseMessage response, StreamReader reader) : IDisposable
    {
        public StreamReader Reader { get; } = reader;

        public void Dispose()
        {
            Reader.Dispose();
            response.Dispose();
        }
    }

    /// <summary>一帧 SSE：按行拆开，字段各取各的。</summary>
    private sealed record SseFrame(IReadOnlyList<string> Lines, string? Id, string? Event, string? Data)
    {
        /// <summary>data 行（正常情况下只有一行）。</summary>
        public IReadOnlyList<string> DataLines =>
            Lines.Where(line => line.StartsWith("data:", StringComparison.Ordinal)).ToArray();

        /// <summary>解析一帧的文本。</summary>
        public static SseFrame Parse(string frame)
        {
            var lines = frame.Split('\n');

            string? id = null;
            string? type = null;
            string? data = null;

            foreach (var line in lines)
            {
                if (line.StartsWith("id:", StringComparison.Ordinal))
                {
                    id = line["id:".Length..].Trim();
                }
                else if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    type = line["event:".Length..].Trim();
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    data = line["data:".Length..].Trim();
                }
            }

            return new SseFrame(lines, id, type, data);
        }
    }
}
