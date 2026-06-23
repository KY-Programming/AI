using KY.AI.Serve;

namespace KY.AI.Ng;

// Angular/esbuild build-output detection for the shared BuildTracker.
//   start   — "Changes detected" / "Rebuilding"
//   success — "bundle generation complete"
//   failed  — "bundle generation failed"
//   errors  — lines containing "[ERROR]"
// The latest settle line wins (ng emits one terminal "bundle generation …" per cycle).
internal sealed class NgBuildMatcher : IBuildMatcher
{
    public bool FirstSettleWins => false;

    public LineKind Classify(string line, bool building)
    {
        if (line.Contains("Changes detected", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Rebuilding", StringComparison.OrdinalIgnoreCase))
            return LineKind.BuildStart;
        if (line.Contains("[ERROR]", StringComparison.Ordinal))
            return LineKind.Error;
        if (line.Contains("bundle generation complete", StringComparison.OrdinalIgnoreCase))
            return LineKind.SettledSuccess;
        if (line.Contains("bundle generation failed", StringComparison.OrdinalIgnoreCase))
            return LineKind.SettledFailed;
        return LineKind.None;
    }
}
