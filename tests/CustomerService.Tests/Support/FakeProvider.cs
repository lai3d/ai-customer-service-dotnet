using System.Net;
using System.Text;

namespace CustomerService.Tests.Support;

/// <summary>
/// A scripted HTTP provider for driving the real clients. A stub implementing IChatModel can
/// return whatever it likes on an error path, and a suite built on one will encode a
/// contract no real client satisfies -- the test passes, its subject is the fixture, and the
/// production code is never executed. These tests put the assertion below that seam.
/// </summary>
public sealed class FakeProvider : HttpMessageHandler
{
    readonly Func<HttpRequestMessage, Stream> respond;
    public List<string> RequestBodies { get; } = new();
    public List<HttpRequestMessage> Requests { get; } = new();

    public FakeProvider(Func<HttpRequestMessage, Stream> respond) => this.respond = respond;

    /// <summary>Serves the given SSE frames, then ends the stream cleanly.</summary>
    public static FakeProvider Sse(params string[] frames) =>
        new(_ => new MemoryStream(Encoding.UTF8.GetBytes(string.Concat(frames))));

    /// <summary>Serves the given SSE frames, then fails the connection.</summary>
    public static FakeProvider SseThenCutOff(params string[] frames) =>
        new(_ => new CutOffStream(Encoding.UTF8.GetBytes(string.Concat(frames))));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(respond(request)) };
        response.Content.Headers.ContentType = new("text/event-stream");
        return response;
    }

    public HttpClient Client() => new(this) { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Yields its bytes, then behaves like a connection that dropped.</summary>
    sealed class CutOffStream(byte[] data) : Stream
    {
        int pos;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (pos >= data.Length) throw new IOException("connection reset by the fake provider");
            int n = Math.Min(buffer.Length, data.Length - pos);
            data.AsSpan(pos, n).CopyTo(buffer);
            pos += n;
            return n;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => ValueTask.FromResult(Read(buffer.Span));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Task.FromResult(Read(buffer, offset, count));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public static class Anthropic
    {
        public const long InputTokensOnTheWire = 1842;

        public static string MessageStart(long input = InputTokensOnTheWire, string model = "claude-opus-5") =>
            $"event: message_start\ndata: {{\"type\":\"message_start\",\"message\":{{\"id\":\"msg_01\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"{model}\",\"content\":[],\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{{\"input_tokens\":{input},\"output_tokens\":1}}}}}}\n\n";
        public static string TextBlockStart(int index = 0) =>
            $"event: content_block_start\ndata: {{\"type\":\"content_block_start\",\"index\":{index},\"content_block\":{{\"type\":\"text\",\"text\":\"\"}}}}\n\n";
        public static string TextDelta(string text, int index = 0) =>
            $"event: content_block_delta\ndata: {{\"type\":\"content_block_delta\",\"index\":{index},\"delta\":{{\"type\":\"text_delta\",\"text\":{System.Text.Json.JsonSerializer.Serialize(text)}}}}}\n\n";
        public static string ToolUseStart(string id, string name, int index = 1) =>
            $"event: content_block_start\ndata: {{\"type\":\"content_block_start\",\"index\":{index},\"content_block\":{{\"type\":\"tool_use\",\"id\":\"{id}\",\"name\":\"{name}\",\"input\":{{}}}}}}\n\n";
        public static string InputJsonDelta(string partial, int index = 1) =>
            $"event: content_block_delta\ndata: {{\"type\":\"content_block_delta\",\"index\":{index},\"delta\":{{\"type\":\"input_json_delta\",\"partial_json\":{System.Text.Json.JsonSerializer.Serialize(partial)}}}}}\n\n";
        public static string BlockStop(int index) =>
            $"event: content_block_stop\ndata: {{\"type\":\"content_block_stop\",\"index\":{index}}}\n\n";
        public static string MessageDelta(string stopReason, long output, long input = InputTokensOnTheWire) =>
            $"event: message_delta\ndata: {{\"type\":\"message_delta\",\"delta\":{{\"stop_reason\":\"{stopReason}\",\"stop_sequence\":null}},\"usage\":{{\"input_tokens\":{input},\"output_tokens\":{output}}}}}\n\n";
        public const string MessageStop = "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
    }

    public static class OpenAI
    {
        public static string Chunk(string content, string model = "gpt-5-2025-08-07") =>
            $"data: {{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"{model}\",\"choices\":[{{\"index\":0,\"delta\":{{\"content\":{System.Text.Json.JsonSerializer.Serialize(content)}}},\"finish_reason\":null}}]}}\n\n";
        public static string RoleChunk(string model = "gpt-5-2025-08-07") =>
            $"data: {{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"{model}\",\"choices\":[{{\"index\":0,\"delta\":{{\"role\":\"assistant\",\"content\":\"\"}},\"finish_reason\":null}}]}}\n\n";
        public static string ToolCallChunk(int index, string? id, string? name, string args, string model = "gpt-5-2025-08-07")
        {
            var idPart = id is null ? "" : $"\"id\":\"{id}\",\"type\":\"function\",";
            var namePart = name is null ? "" : $"\"name\":\"{name}\",";
            return $"data: {{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"{model}\",\"choices\":[{{\"index\":0,\"delta\":{{\"tool_calls\":[{{\"index\":{index},{idPart}\"function\":{{{namePart}\"arguments\":{System.Text.Json.JsonSerializer.Serialize(args)}}}}}]}},\"finish_reason\":null}}]}}\n\n";
        }
        public static string Finish(string reason, string model = "gpt-5-2025-08-07") =>
            $"data: {{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"{model}\",\"choices\":[{{\"index\":0,\"delta\":{{}},\"finish_reason\":\"{reason}\"}}]}}\n\n";
        public static string Usage(long prompt, long completion, string model = "gpt-5-2025-08-07") =>
            $"data: {{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"{model}\",\"choices\":[],\"usage\":{{\"prompt_tokens\":{prompt},\"completion_tokens\":{completion},\"total_tokens\":{prompt + completion}}}}}\n\n";
        public const string Done = "data: [DONE]\n\n";
    }
}
