namespace KY.AI.Serve;

// How a single line of dev-server output is classified by a tool-specific matcher.
public enum LineKind
{
    None,            // nothing of interest
    BuildStart,      // a (re)build / restart has begun
    Error,           // a compiler diagnostic to count
    Warning,         // a non-fatal diagnostic worth surfacing (deprecations, etc.)
    SettledSuccess,  // the build finished successfully
    SettledFailed,   // the build finished with a failure
}

// A structured compiler diagnostic parsed from build output. Location fields are null when the
// CLI didn't print them (or they couldn't be parsed); `Raw` is always the original output line so
// nothing is lost when parsing is partial.
public sealed record BuildDiagnostic(
    string Severity,   // "error" | "warning"
    string? File,
    int? Line,
    int? Column,
    string Message,
    string Raw);

// Tool-specific build-output detection injected into the shared BuildTracker. The Angular
// and .NET supervisors key off different phrasing (and slightly different settle policies),
// so each tool ships its own matcher while the tracking/debounce logic stays shared.
public interface IBuildMatcher
{
    // Classify a single (ANSI-stripped) output line. `building` is true when a build is
    // currently in flight — some tools only count error lines while a build is running.
    LineKind Classify(string line, bool building);

    // true  → the first settle line per build cycle wins (later settle lines are ignored,
    //         e.g. dotnet emitting "Now listening on" before "Application started").
    // false → the latest settle line wins (e.g. Angular's last "bundle generation …").
    bool FirstSettleWins { get; }

    // ── Structured-diagnostic parsing (opt-in per tool; default null) ──
    // A matcher that recognizes its CLI's diagnostic format overrides these. The BuildTracker
    // correlates them for the multi-line case: a header line carrying severity + message, then a
    // standalone location line (e.g. esbuild prints "✘ [ERROR] …" then "src/x.ts:12:34:").

    // A diagnostic *header* line. Location fields may be null when the CLI prints the location
    // separately (the tracker backfills via TryParseLocation). `Raw` must echo the original line.
    BuildDiagnostic? TryParseDiagnostic(string line) => null;

    // A standalone "file:line:col" location line, used to backfill the most recent header that
    // still lacks a location.
    (string File, int Line, int Col)? TryParseLocation(string line) => null;
}
