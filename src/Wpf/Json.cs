using System.Text.Json;
using System.Text.Json.Serialization;

namespace KY.AI.Wpf;

// One serializer for every tool reply, matching the rest of the suite's camelCase payloads.
// WhenWritingNull is doing real work here rather than being a style choice: most fields of most
// automation nodes are empty, and a full WPF tree written with explicit nulls is roughly twice the
// tokens for no added meaning.
internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Write(object value) => JsonSerializer.Serialize(value, Options);

    // The shape every failure takes, so the agent can branch on `error` without knowing which tool
    // produced it — the same soft-error convention the Serve hub uses.
    public static string Error(string error, string? hint = null, object? extra = null) =>
        Write(new { error, hint, extra });
}
