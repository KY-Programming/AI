using System.Text.RegularExpressions;
using KY.AI.Serve;

namespace KY.AI.Ng;

// Angular build-output detection for the shared BuildTracker. Two output formats are recognized:
//
// esbuild (`:application`, `:browser-esbuild`)
//   start    — "Changes detected" / "Rebuilding"
//   success  — "bundle generation complete"
//   failed   — "bundle generation failed"
//   errors   — lines containing "[ERROR]"
//   warnings — lines containing "[WARNING]" (covers deprecations like allowSignalWrites)
// The latest settle line wins (ng emits one terminal "bundle generation …" per cycle).
// Structured diagnostics come in esbuild's two-line shape, which the BuildTracker stitches
// together: a header line carrying the message, then a standalone location line, e.g.
//   ✘ [ERROR] TS2304: Cannot find name 'foo'. [plugin angular-compiler]
//
//       src/app/app.component.ts:12:34:
//
// webpack (`:browser` via `:dev-server`) — a cycle prints, in this order:
//   ✔ Browser application bundle generation complete.      ← stderr, after compiling, even on failure
//   Error: src/x.ts:3:19 - error TS2307: …                  ← stderr, location on the same line
//   ./src/styles.css - Error: Module build failed (…):     ← stderr, webpack module diagnostic
//   Error: Can't resolve 'x.css' in '…'                    ← continuation of the one above
//   ✖ Failed to compile.  /  ✔ Compiled successfully.       ← stdout, the actual verdict
// A webpack REBUILD prints no start line at all, so the "<Browser|Server> application bundle
// generation complete" marker opens the cycle (BuildStart when idle; ignored while a cold start is
// already building) and must never settle it. Diagnostics are separated by blank lines, so a bare
// "Error:"/"Warning:" line only counts when it follows a blank line — otherwise it is the
// continuation of a multi-line module error and would double-count. That is the one piece of
// per-line state this matcher keeps (the tracker feeds it every line, in order, under its lock).
public sealed class NgBuildMatcher : IBuildMatcher
{
    public bool FirstSettleWins => false;

    // "[ERROR] <message>" / "[WARNING] <message>" — the diagnostic header (location is separate).
    private static readonly Regex Header =
        new(@"\[(?<sev>ERROR|WARNING)\]\s*(?<msg>.*)$", RegexOptions.Compiled);

    // An indented "path/file.ext:line:col:" location line (esbuild prints it after the header).
    private static readonly Regex Location =
        new(@"^\s*(?<file>.+?\.[A-Za-z0-9]+):(?<line>\d+):(?<col>\d+):?\s*$", RegexOptions.Compiled);

    // webpack's cycle marker; esbuild's equivalent line starts with plain "Application bundle".
    private static readonly Regex WebpackBundleComplete =
        new(@"\b(?:Browser|Server) application bundle generation complete", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "Error: src/x.ts:3:19 - error TS2307: <message>" (TS/NG diagnostic, location inline).
    private static readonly Regex WebpackCompilerDiag =
        new(@"^(?<sev>Error|Warning):\s+(?<file>.+?):(?<line>\d+):(?<col>\d+)\s+-\s+(?:error|warning)\s+(?<msg>.+)$", RegexOptions.Compiled);

    // "./node_modules/x.mjs:527:50-54 - Error: <message>" / "./src/styles.css - Error: <message>".
    private static readonly Regex WebpackModuleDiag =
        new(@"^(?<file>\S.*?)(?::(?<line>\d+):(?<col>\d+)(?:-\d+)?)?\s+-\s+(?<sev>Error|Warning):\s*(?<msg>.*)$", RegexOptions.Compiled);

    // A bare "Error: <message>" / "Warning: <message>" (budgets, CommonJS bailouts, …).
    private static readonly Regex WebpackPlainDiag =
        new(@"^(?<sev>Error|Warning):\s*(?<msg>.*)$", RegexOptions.Compiled);

    private bool _prevBlank = true;

    public LineKind Classify(string line, bool building)
    {
        var afterBlank = _prevBlank;
        _prevBlank = string.IsNullOrWhiteSpace(line);

        if (line.Contains("Changes detected", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Rebuilding", StringComparison.OrdinalIgnoreCase))
            return LineKind.BuildStart;
        if (WebpackBundleComplete.IsMatch(line))
            return building ? LineKind.None : LineKind.BuildStart;
        if (line.Contains("[ERROR]", StringComparison.Ordinal))
            return LineKind.Error;
        if (line.Contains("[WARNING]", StringComparison.Ordinal))
            return LineKind.Warning;
        if (WebpackSeverity(line, afterBlank) is { } sev)
            return sev;
        if (line.Contains("bundle generation complete", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Compiled successfully", StringComparison.OrdinalIgnoreCase))
            return LineKind.SettledSuccess;
        if (line.Contains("bundle generation failed", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Failed to compile", StringComparison.OrdinalIgnoreCase))
            return LineKind.SettledFailed;
        return LineKind.None;
    }

    private static LineKind? WebpackSeverity(string line, bool afterBlank)
    {
        var m = WebpackCompilerDiag.Match(line);
        if (!m.Success) m = WebpackModuleDiag.Match(line);
        if (!m.Success && afterBlank) m = WebpackPlainDiag.Match(line);
        if (!m.Success) return null;
        return m.Groups["sev"].Value == "Error" ? LineKind.Error : LineKind.Warning;
    }

    public BuildDiagnostic? TryParseDiagnostic(string line)
    {
        var m = Header.Match(line);
        if (m.Success)
        {
            var severity = m.Groups["sev"].Value.Equals("ERROR", StringComparison.Ordinal) ? "error" : "warning";
            return new BuildDiagnostic(severity, null, null, null, m.Groups["msg"].Value.Trim(), line.Trim());
        }

        m = WebpackCompilerDiag.Match(line);
        if (!m.Success) m = WebpackModuleDiag.Match(line);
        if (!m.Success) m = WebpackPlainDiag.Match(line);
        if (!m.Success) return null;
        return new BuildDiagnostic(
            m.Groups["sev"].Value == "Error" ? "error" : "warning",
            m.Groups["file"].Success ? m.Groups["file"].Value : null,
            m.Groups["line"].Success ? int.Parse(m.Groups["line"].Value) : null,
            m.Groups["col"].Success ? int.Parse(m.Groups["col"].Value) : null,
            m.Groups["msg"].Value.Trim(),
            line.Trim());
    }

    public (string File, int Line, int Col)? TryParseLocation(string line)
    {
        var m = Location.Match(line);
        if (!m.Success) return null;
        return (m.Groups["file"].Value, int.Parse(m.Groups["line"].Value), int.Parse(m.Groups["col"].Value));
    }
}
