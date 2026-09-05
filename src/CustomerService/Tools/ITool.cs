using System.Text.Json;
using System.Text.Json.Serialization;

namespace CustomerService.Tools;

/// <summary>
/// What the model gets back, plus a label for metrics and the stream. Outcome is not sent
/// to the model. It exists because a tool call is otherwise invisible to everything outside
/// the model call -- no metric, no span, nothing for a client to display until the assistant
/// happens to mention what it did.
/// </summary>
public sealed record ToolOutcome(string Content, string Outcome);

/// <summary>
/// One callable action. The conversation id is a parameter rather than something fished
/// out of an ambient context. In Spring AI it travelled through a ToolContext, which
/// created a contract with teeth: a code path that reached the model without populating it
/// broke ticket creation, and broke it only once a conversation had escalated far enough
/// for the model to try. Here a caller that forgets it does not compile.
/// </summary>
public interface ITool
{
    Llm.ToolDefinition Definition { get; }
    Task<ToolOutcome> InvokeAsync(string conversationId, JsonElement arguments, CancellationToken ct);
}

public static class ToolJson
{
    // Enums as their names. The first live turn against Claude reported that "the status
    // field came back in a coded form I can't reliably translate": System.Text.Json writes
    // enums as integers unless told otherwise, so the model received {"status":1} and,
    // correctly, refused to guess what it meant. No test noticed, because every test read
    // the JSON back through the same serializer. A tool result is prompt; it has to be
    // written for a reader that has never seen the type.
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>
    /// Builds a JSON-schema object from a property table. Descriptions are prompt, not
    /// documentation: they are the entire basis on which the model decides whether to call
    /// a tool instead of answering from retrieved text.
    /// </summary>
    public static JsonElement Schema(IReadOnlyDictionary<string, (string type, string description)> properties, params string[] required)
    {
        var props = properties.ToDictionary(kv => kv.Key, kv => new { type = kv.Value.type, description = kv.Value.description });
        return JsonSerializer.SerializeToElement(new { type = "object", properties = props, required }, Options);
    }

    public static string? OptionalString(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
