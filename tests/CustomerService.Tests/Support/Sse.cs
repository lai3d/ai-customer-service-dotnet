namespace CustomerService.Tests.Support;

public sealed record SseFrame(string? Event, string? Data, bool Comment);

public static class Sse
{
    /// <summary>Splits a text/event-stream body into frames, keeping comment-only frames.</summary>
    public static List<SseFrame> Parse(string body)
    {
        var frames = new List<SseFrame>();
        foreach (var raw in body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string? ev = null, data = null; bool comment = false;
            foreach (var line in raw.Split('\n'))
            {
                if (line.StartsWith("event: ")) ev = line[7..].Trim();
                else if (line.StartsWith("data: ")) data = line[6..];
                else if (line.StartsWith(':')) comment = true;
            }
            frames.Add(new SseFrame(ev, data, comment && ev is null));
        }
        return frames;
    }
}
