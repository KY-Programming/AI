using System.Text.Json;

namespace KY.AI.Browser;

// Instance-side dispatch for an EvalRequest the hub forwarded to /eval. This is where the supervised-
// interaction gate is enforced (the flags live on the capture instance's channel, not the hub): an
// `overlay` request toggles InteractionActive; a manipulation kind — or a batch that contains one — is
// refused until it is open. Two human overrides sit on top of that gate, checked in order:
//   Killed  — the human clicked a Stop icon (badge or paused pill): EVERY kind is refused, reads
//             included (evaluate_js/query_dom/wait_for/get_styles/read_component, not just
//             manipulation) — EXCEPT the overlay verb itself. There is no page-side "revive": resuming
//             means the human tells the agent in chat, and the agent's own start_interaction is what
//             starts a clean new session and clears Killed (see EvalChannel.SetInteraction) — that is
//             why overlay is exempted here rather than refused outright like it is for Paused.
//   Paused  — the human clicked the badge's Pause icon: manipulation/batch-with-manipulation/a fresh
//             start_interaction are refused (reads still work), telling the agent to wait or call
//             wait_for_resume. Only the paused pill's "resume" clears it — the agent can't reopen the
//             gate itself.
// Everything else is queued on the channel and awaited. Kept as a pure static so the /eval route and
// the unit tests drive the exact same logic.
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

        var isOverlayKind = string.Equals(req.Kind, "overlay", StringComparison.Ordinal);
        var opensOverlay = isOverlayKind && req.Show == true;
        var isManipulationBatch = string.Equals(req.Kind, "batch", StringComparison.Ordinal)
            && req.Actions is { } acts && acts.Any(a => a.IsManipulation);

        // Killed refuses everything except the overlay verb — see the type header for why.
        if (ch.Killed && !isOverlayKind) return Killed();

        if (ch.Paused && (opensOverlay || IsManipulationKind(req.Kind) || isManipulationBatch))
            return Paused();

        if (isOverlayKind)
            ch.SetInteraction(req.Show == true);
        else if (IsManipulationKind(req.Kind) && !ch.InteractionActive)
            return NeedsInteraction();
        else if (isManipulationBatch && !ch.InteractionActive)
            return NeedsInteraction();

        return await ch.RequestAsync(id => req with { Id = id }, waitMs, CancellationToken.None);
    }

    public static string NeedsInteraction() => JsonSerializer.Serialize(new
    {
        ok = false,
        needsInteraction = true,
        error = "call start_interaction first — it shows the user a visible overlay while you drive the page; call stop_interaction when done",
    }, Json);

    // The human clicked Pause on the badge — a brief, resumable "hands off, I'm testing this myself".
    public static string Paused() => JsonSerializer.Serialize(new
    {
        ok = false,
        paused = true,
        error = "the user paused the supervision overlay — they are using this tab themselves right now. " +
                "Stop testing and either call wait_for_resume or wait for them to tell you to continue; " +
                "do not call start_interaction again until they click \"resume\" on the paused pill.",
    }, Json);

    // The human clicked a Stop icon — this is not a pause, don't treat it like one.
    public static string Killed() => JsonSerializer.Serialize(new
    {
        ok = false,
        killed = true,
        error = "the user stopped the session completely (not just paused it) — stop testing now. Do not " +
                "retry, do not call wait_for_resume. There is no button for the user to click to bring you " +
                "back — wait for them to tell you to continue in chat, and only then call start_interaction " +
                "to begin a clean new session.",
    }, Json);

    public static string NotRunning() =>
        JsonSerializer.Serialize(new { enabled = false, error = "ky-ai-browser capture not running" }, Json);
}
