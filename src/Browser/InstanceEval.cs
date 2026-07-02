using System.Text.Json;

namespace KY.AI.Browser;

// Instance-side dispatch for an EvalRequest the hub forwarded to /eval. This is where the supervised-
// interaction gate is enforced (the flag lives on the capture instance's channel, not the hub): an
// `overlay` request toggles it; a manipulation kind — or a batch that contains one — is refused until
// it is open. Everything else is queued on the channel and awaited. Kept as a pure static so the
// /eval route and the unit tests drive the exact same logic.
internal static class InstanceEval
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static bool IsManipulationKind(string? kind) =>
        kind is "click" or "move" or "key" or "type" or "scroll" or "focus" or "navigate";

    // Gate, then enqueue + await. `ch` is null only if capture isn't running (defensive — an instance
    // always has its channel); waitMs is how long to park the call waiting on the page.
    public static async Task<string> DispatchAsync(EvalChannel? ch, EvalRequest req, int waitMs)
    {
        if (ch is null) return NotRunning();

        if (string.Equals(req.Kind, "overlay", StringComparison.Ordinal))
            ch.SetInteraction(req.Show == true);
        else if (IsManipulationKind(req.Kind) && !ch.InteractionActive)
            return NeedsInteraction();
        else if (string.Equals(req.Kind, "batch", StringComparison.Ordinal)
                 && req.Actions is { } acts && acts.Any(a => a.IsManipulation) && !ch.InteractionActive)
            return NeedsInteraction();

        return await ch.RequestAsync(id => req with { Id = id }, waitMs, CancellationToken.None);
    }

    public static string NeedsInteraction() => JsonSerializer.Serialize(new
    {
        ok = false,
        needsInteraction = true,
        error = "call start_interaction first — it shows the user a visible overlay while you drive the page; call stop_interaction when done",
    }, Json);

    public static string NotRunning() =>
        JsonSerializer.Serialize(new { enabled = false, error = "ky-ai-browser capture not running" }, Json);
}
