namespace KY.AI.Browser;

// One browser/runtime console event, enriched server-side. Mirrors the build verdict's
// timestamp/seq conventions so the agent reads consoles the same way it reads builds:
//   seq         — server-assigned, monotonic per supervisor (use with console_tail sinceSeq)
//   level       — log | info | warn | error | debug | exception | unhandledrejection
//   args        — each console argument rendered to a string (objects/Errors/DOM safely stringified)
//   text        — args joined with a space (the one-line human/grep view); may be null for `hello`
//   source/line/col — best-effort source location parsed from the stack (or window.onerror fields)
//   stack       — the raw stack string, kept verbatim (parsing is best-effort, this is not)
//   timestamp   — client wall-clock when the event fired (ISO-8601 w/ offset), falls back to ReceivedAt
//   buildSeq    — the build seq in effect when this event was ingested (build correlation;
//                 pass a wait_for_build verdict's `seq` to console_tail sinceBuildSeq)
//   pageLoadId  — random id minted by the snippet per page load; segments reloads / HMR boundaries
//   tabId       — stable per-tab id (sessionStorage); which browser tab logged this, for per-tab tails
//   receivedAt  — server wall-clock when the supervisor stored it (ISO-8601 w/ offset)
public sealed record ConsoleEvent(
    long Seq,
    string Level,
    IReadOnlyList<string> Args,
    string? Text,
    string? Source,
    int? Line,
    int? Col,
    string? Stack,
    string Timestamp,
    long BuildSeq,
    string PageLoadId,
    string ReceivedAt,
    string? TabId = null);

// The raw per-event shape posted by the injected snippet (camelCase on the wire; matched
// case-insensitively). Everything is nullable/optional so a malformed event degrades gracefully
// rather than failing the whole batch.
public sealed record RawConsoleEvent(
    string? Level,
    string[]? Args,
    string? Text,
    string? Source,
    int? Line,
    int? Col,
    string? Stack,
    string? Timestamp);

// The ingest POST body: a batch of events plus the misroute token and the page-load id they
// belong to (see ConsoleCollector for why the token is a routing tag, not a security control).
// `DroppedClient` reports events the snippet dropped before sending (its own queue overflowed),
// so a client-side flood still shows up in the server's `dropped` count.
public sealed record ConsoleIngestBatch(
    string? Token,
    string? PageLoadId,
    RawConsoleEvent[]? Events,
    long? DroppedClient = null,
    string? TabId = null);

// Severity ordering for console_tail's `level` (min-severity) filter. Unknown event levels rank
// highest so they are never hidden; an unknown filter value disables the filter (rank 0).
internal static class ConsoleLevels
{
    private static readonly Dictionary<string, int> Rank = new(StringComparer.OrdinalIgnoreCase)
    {
        ["debug"] = 0,
        ["log"] = 1,
        ["info"] = 2,
        ["warn"] = 3,
        ["warning"] = 3,
        ["error"] = 4,
        ["exception"] = 5,
        ["unhandledrejection"] = 5,
    };

    // Rank of an event's level — unknown levels sort to the top so a filter never hides them.
    public static int RankOf(string? level) =>
        level is not null && Rank.TryGetValue(level, out var r) ? r : int.MaxValue;

    // Rank of a filter threshold — an unrecognized threshold means "no filter" (everything passes).
    public static int FilterRank(string? minLevel) =>
        minLevel is not null && Rank.TryGetValue(minLevel, out var r) ? r : 0;
}
