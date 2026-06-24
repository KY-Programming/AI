using System.Text.RegularExpressions;
using KY.AI.Serve;

namespace KY.AI.Net;

// `dotnet watch` / ASP.NET host build-output detection for the shared BuildTracker.
//   start    — "File changed" / "Restarting" / "Building..."
//   success  — "Application started" / "Now listening on" / "Hot reload of changes succeeded"
//   failed   — "Build FAILED" / "Waiting for a file to change before restarting"
//   errors   — lines containing ": error " (counted only while a build is in flight, so runtime
//              log lines that happen to contain "error" once the app is serving don't count)
//   warnings — lines containing ": warning " (same build-in-flight guard as errors)
// The first settle line per cycle wins (e.g. "Now listening on" before "Application started").
//
// Roslyn/MSBuild diagnostics are single-line and self-contained, e.g.
//   C:\path\File.cs(12,34): error CS0103: The name 'foo' does not exist [C:\path\proj.csproj]
public sealed class DotnetBuildMatcher : IBuildMatcher
{
    public bool FirstSettleWins => true;

    // "<file>(line,col): error|warning CODE: message [project]" — the whole diagnostic on one line.
    private static readonly Regex Diagnostic = new(
        @"^(?<file>.+?)\((?<line>\d+),(?<col>\d+)\):\s*(?<sev>error|warning)\s+[A-Za-z0-9]+:\s*(?<msg>.*?)(?:\s*\[[^\]]*\])?$",
        RegexOptions.Compiled);

    public LineKind Classify(string line, bool building)
    {
        if (line.Contains("File changed", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Restarting", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Building...", StringComparison.OrdinalIgnoreCase))
            return LineKind.BuildStart;

        if (building && line.Contains(": error ", StringComparison.Ordinal))
            return LineKind.Error;
        if (building && line.Contains(": warning ", StringComparison.Ordinal))
            return LineKind.Warning;

        if (line.Contains("Application started", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Now listening on", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Hot reload of changes succeeded", StringComparison.OrdinalIgnoreCase))
            return LineKind.SettledSuccess;

        if (line.Contains("Build FAILED", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Waiting for a file to change before restarting", StringComparison.OrdinalIgnoreCase))
            return LineKind.SettledFailed;

        return LineKind.None;
    }

    public BuildDiagnostic? TryParseDiagnostic(string line)
    {
        var m = Diagnostic.Match(line);
        if (!m.Success) return null;
        return new BuildDiagnostic(
            m.Groups["sev"].Value,
            m.Groups["file"].Value.Trim(),
            int.Parse(m.Groups["line"].Value),
            int.Parse(m.Groups["col"].Value),
            m.Groups["msg"].Value.Trim(),
            line.Trim());
    }
}
