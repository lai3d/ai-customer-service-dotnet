namespace CustomerService.HttpApi;

/// <summary>
/// The single-page demo, embedded in the assembly. It is deliberately not a chat widget. A
/// widget's job is to make the model feel seamless and invisible; the substance here is the
/// invisible part -- which passages retrieval found and how they scored, which tools ran and
/// what they decided, and how many model calls the turn actually billed for.
/// </summary>
public static class DemoPage
{
    public static byte[] Html { get; } = Read();

    static byte[] Read()
    {
        using var s = typeof(DemoPage).Assembly.GetManifestResourceStream("web/index.html")
            ?? throw new InvalidOperationException("web/index.html is not embedded");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public static void MapDemoPage(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", (HttpContext http) =>
        {
            http.Response.ContentType = "text/html; charset=utf-8";
            return http.Response.Body.WriteAsync(Html).AsTask();
        });
    }
}
