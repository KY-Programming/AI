namespace KY.AI.Serve;

// How a single line of dev-server output is classified by a tool-specific matcher.
public enum LineKind
{
    None,            // nothing of interest
    BuildStart,      // a (re)build / restart has begun
    Error,           // a compiler diagnostic to count
    SettledSuccess,  // the build finished successfully
    SettledFailed,   // the build finished with a failure
}

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
}
