using System.Text.RegularExpressions;
using KY.AI.Serve;

namespace KY.AI.Ng;

// Angular/esbuild build-output detection for the shared BuildTracker.
//   start    — "Changes detected" / "Rebuilding"
//   success  — "bundle generation complete"
//   failed   — "bundle generation failed"
//   errors   — lines containing "[ERROR]"
//   warnings — lines containing "[WARNING]" (covers deprecations like allowSignalWrites)
// The latest settle line wins (ng emits one terminal "bundle generation …" per cycle).
//
// Structured diagnostics come in esbuild's two-line shape, which the BuildTracker stitches
// together: a header line carrying the message, then a standalone location line, e.g.
//   ✘ [ERROR] TS2304: Cannot find name 'foo'. [plugin angular-compiler]
//
//       src/app/app.component.ts:12:34:
public sealed class NgBuildMatcher : IBuildMatcher
{
    public bool FirstSettleWins => false;

    // "[ERROR] <message>" / "[WARNING] <message>" — the diagnostic header (location is separate).
    private static readonly Regex Header =
        new(@"\[(?<sev>ERROR|WARNING)\]\s*(?<msg>.*)$", RegexOptions.Compiled);

    // An indented "path/file.ext:line:col:" location line (esbuild prints it after the header).
    private static readonly Regex Location =
        new(@"^\s*(?<file>.+?\.[A-Za-z0-9]+):(?<line>\d+):(?<col>\d+):?\s*$", RegexOptions.Compiled);

    public LineKind Classify(string line, bool building)
    {
        if (line.Contains("Changes detected", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Rebuilding", StringComparison.OrdinalIgnoreCase))
            return LineKind.BuildStart;
        if (line.Contains("[ERROR]", StringComparison.Ordinal))
            return LineKind.Error;
        if (line.Contains("[WARNING]", StringComparison.Ordinal))
            return LineKind.Warning;
        if (line.Contains("bundle generation complete", StringComparison.OrdinalIgnoreCase))
            return LineKind.SettledSuccess;
        if (line.Contains("bundle generation failed", StringComparison.OrdinalIgnoreCase))
            return LineKind.SettledFailed;
        return LineKind.None;
    }

    public BuildDiagnostic? TryParseDiagnostic(string line)
    {
        var m = Header.Match(line);
        if (!m.Success) return null;
        var severity = m.Groups["sev"].Value.Equals("ERROR", StringComparison.Ordinal) ? "error" : "warning";
        return new BuildDiagnostic(severity, null, null, null, m.Groups["msg"].Value.Trim(), line.Trim());
    }

    public (string File, int Line, int Col)? TryParseLocation(string line)
    {
        var m = Location.Match(line);
        if (!m.Success) return null;
        return (m.Groups["file"].Value, int.Parse(m.Groups["line"].Value), int.Parse(m.Groups["col"].Value));
    }
}
