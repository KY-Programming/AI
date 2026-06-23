using KY.AI.Serve;

namespace KY.AI.Net;

// `dotnet watch` / ASP.NET host build-output detection for the shared BuildTracker.
//   start   — "File changed" / "Restarting" / "Building..."
//   success — "Application started" / "Now listening on" / "Hot reload of changes succeeded"
//   failed  — "Build FAILED" / "Waiting for a file to change before restarting"
//   errors  — lines containing ": error " (counted only while a build is in flight, so runtime
//             log lines that happen to contain "error" once the app is serving don't count)
// The first settle line per cycle wins (e.g. "Now listening on" before "Application started").
internal sealed class DotnetBuildMatcher : IBuildMatcher
{
    public bool FirstSettleWins => true;

    public LineKind Classify(string line, bool building)
    {
        if (line.Contains("File changed", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Restarting", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Building...", StringComparison.OrdinalIgnoreCase))
            return LineKind.BuildStart;

        if (building && line.Contains(": error ", StringComparison.Ordinal))
            return LineKind.Error;

        if (line.Contains("Application started", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Now listening on", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Hot reload of changes succeeded", StringComparison.OrdinalIgnoreCase))
            return LineKind.SettledSuccess;

        if (line.Contains("Build FAILED", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Waiting for a file to change before restarting", StringComparison.OrdinalIgnoreCase))
            return LineKind.SettledFailed;

        return LineKind.None;
    }
}
