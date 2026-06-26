using System.Diagnostics;

namespace KY.AI.Serve;

internal enum BuildStatus { Unknown, Building, Success, Failed }

// The settled (or in-progress) result of a dev-server build, as observed from the output stream.
//   building          — a (re)build is currently running
//   pending           — a source file changed that the latest build hasn't incorporated yet
//   seq               — increments per build, so callers can tell builds apart
//   startedBy         — the output line that triggered this build (null on a cold start/restart)
//   settledBy         — the verbatim output line that produced the success/failed verdict (null
//                       while building/unknown); makes a mis-matched detector obvious. NOTE its
//                       timestamp, if any, is the dev server's own — not one we emit.
//   errorLines/       — raw matched diagnostic lines (capped), kept for back-compat
//     warningLines
//   diagnostics       — structured {severity,file,line,col,message,raw} parsed from those lines
//   lastChangeAt      — when a source file last changed (ISO-8601 with offset)
//   filesInLastBuild  — the source files this build incorporated (those changed since the prior
//                       build began, plus any whose change event lagged into the build's first
//                       second), so an agent can confirm its edit is reflected
//   timedOut          — wait_for_build gave up before the build settled
//   mayHaveStaleInstances — this incremental rebuild changed code (a file outside the tool's
//                       hot-swappable set, e.g. a .ts), so a hot reload may keep already-created
//                       objects on the previous version; reload the page to be sure. Only set for
//                       tools that opt in via HotReloadSafeExtensions (ng), never on a cold start.
//   staleHint         — human-readable companion to mayHaveStaleInstances (null when it is false)
internal sealed record BuildResult(
    string Status,
    bool Building,
    bool Pending,
    int Errors,
    int Warnings,
    long? DurationMs,
    string? StartedAt,
    string? FinishedAt,
    string? LastChangeAt,
    long Seq,
    IReadOnlyList<string> ErrorLines,
    IReadOnlyList<string> WarningLines,
    IReadOnlyList<BuildDiagnostic> Diagnostics,
    IReadOnlyList<string> FilesInLastBuild,
    string? StartedBy = null,
    string? SettledBy = null,
    bool TimedOut = false,
    bool MayHaveStaleInstances = false,
    string? StaleHint = null);

// Watches the dev-server output (via a tool-specific IBuildMatcher) and a source-file
// watcher to derive the current build state. `WaitForSettleAsync` blocks until the build
// that incorporates the latest change has settled and stayed quiet — making verification
// deterministic.
internal sealed class BuildTracker
{
    internal const string IsoFormat = "yyyy-MM-ddTHH:mm:ss.fffzzz";

    // How long a detected source change stays "pending" without a rebuild starting. If the
    // dev server doesn't react within this window the change wasn't rebuild-worthy (a no-op
    // touch, or the build's own output written into the source tree — e.g. Angular i18n
    // localize), so pending self-clears instead of sticking forever.
    private static readonly TimeSpan PendingGrace = TimeSpan.FromSeconds(4);

    // A change event delivered within this window after a build starts is treated as the lagging
    // trigger for THAT build (the dev server printed "Rebuilding…" before our watcher event arrived)
    // rather than a change pending the next build — so filesInLastBuild isn't left empty by the race.
    private static readonly TimeSpan AttributionGrace = TimeSpan.FromSeconds(1);

    private const int LineCap = 20;       // raw error/warning lines kept per build
    private const int DiagCap = 40;       // structured diagnostics kept per build
    private const int ChangedFilesCap = 100;

    private readonly IBuildMatcher _matcher;
    private readonly IReadOnlyList<string> _hotReloadSafeExt;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _sync = new();
    private readonly Stopwatch _sw = new();
    private readonly List<string> _errorLines = new();
    private readonly List<string> _warningLines = new();
    private readonly List<BuildDiagnostic> _diagnostics = new();
    // Two buckets: files attributed to the current/just-finished build, and changes awaiting the
    // next build. NoteSourceChange routes into one or the other by timing (see AttributionGrace).
    private readonly HashSet<string> _attributed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _incoming = new(StringComparer.OrdinalIgnoreCase);

    private BuildStatus _status = BuildStatus.Unknown;
    private int _errors;
    private int _warnings;
    private long? _durationMs;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _finishedAt;
    private DateTimeOffset? _lastSourceChange;
    private DateTimeOffset _buildStartedAt = DateTimeOffset.MinValue;
    private long _seq;
    private string? _startedBy;
    private string? _settledBy;
    private int _openDiagIndex = -1;                 // diagnostic awaiting its location line (-1 = none)

    // hotReloadSafeExt: extensions a hot reload swaps cleanly (templates/styles); a build touching
    // anything else is flagged mayHaveStaleInstances. Empty ⇒ no stale hint. clock is injectable so
    // the change→build attribution window is testable; defaults to wall-clock.
    public BuildTracker(IBuildMatcher matcher, IReadOnlyList<string>? hotReloadSafeExt = null, Func<DateTimeOffset>? clock = null)
    {
        _matcher = matcher;
        _hotReloadSafeExt = hotReloadSafeExt ?? Array.Empty<string>();
        _now = clock ?? (() => DateTimeOffset.Now);
    }

    public void MarkBuilding() { lock (_sync) BeginNoLock(null); }

    // Called by the file watcher the moment a source file changes — before the dev server reacts.
    // `path` is recorded so the next build can report which files it incorporated. A change arriving
    // within the attribution grace of an in-flight build is folded into THAT build (the watcher event
    // lagged the dev server's own detection); otherwise it queues for the next build.
    public void NoteSourceChange(string path)
    {
        lock (_sync)
        {
            var now = _now();
            _lastSourceChange = now;
            if (string.IsNullOrEmpty(path)) return;
            var withinGrace = _status == BuildStatus.Building && now - _buildStartedAt <= AttributionGrace;
            var target = withinGrace ? _attributed : _incoming;
            if (target.Count < ChangedFilesCap) target.Add(path);
        }
    }

    // Classify one (ANSI-stripped) line, fold it into the build state, and return its kind plus
    // the build seq it belongs to — so the caller can tag the log entry with the same cycle.
    public (LineKind Kind, long Seq) Observe(string line)
    {
        lock (_sync)
        {
            var kind = _matcher.Classify(line, _status == BuildStatus.Building);
            switch (kind)
            {
                case LineKind.BuildStart:
                    BeginNoLock(line);
                    break;
                case LineKind.Error:
                    _errors++;
                    if (_errorLines.Count < LineCap) _errorLines.Add(line.Trim());
                    RecordDiagnostic(line, "error");
                    break;
                case LineKind.Warning:
                    _warnings++;
                    if (_warningLines.Count < LineCap) _warningLines.Add(line.Trim());
                    RecordDiagnostic(line, "warning");
                    break;
                case LineKind.SettledSuccess:
                    Settle(BuildStatus.Success, line);
                    break;
                case LineKind.SettledFailed:
                    Settle(BuildStatus.Failed, line);
                    break;
                default:
                    // A non-diagnostic line may still be the standalone location for the open
                    // diagnostic (esbuild prints "src/x.ts:12:34:" on the line after the message).
                    TryBackfillLocation(line);
                    break;
            }
            return (kind, _seq);
        }
    }

    private void RecordDiagnostic(string line, string severity)
    {
        var raw = line.Trim();
        var d = _matcher.TryParseDiagnostic(line) ?? new BuildDiagnostic(severity, null, null, null, raw, raw);
        if (_diagnostics.Count < DiagCap)
        {
            _diagnostics.Add(d);
            _openDiagIndex = d.File is null ? _diagnostics.Count - 1 : -1;
        }
        else
        {
            _openDiagIndex = -1;
        }
    }

    private void TryBackfillLocation(string line)
    {
        if (_openDiagIndex < 0) return;
        var loc = _matcher.TryParseLocation(line);
        if (loc is null) return;
        var d = _diagnostics[_openDiagIndex];
        _diagnostics[_openDiagIndex] = d with { File = loc.Value.File, Line = loc.Value.Line, Column = loc.Value.Col };
        _openDiagIndex = -1;
    }

    private void BeginNoLock(string? startLine)
    {
        _status = BuildStatus.Building;
        _errors = 0;
        _warnings = 0;
        _errorLines.Clear();
        _warningLines.Clear();
        _diagnostics.Clear();
        _openDiagIndex = -1;
        _startedBy = startLine?.Trim();
        _startedAt = _now();
        _buildStartedAt = _startedAt.Value;
        // The changes that arrived before this build began are the ones it incorporates; lagging
        // trigger events (delivered just after the dev server's own detection) are folded into this
        // same set by NoteSourceChange during the attribution grace.
        _attributed.Clear();
        foreach (var f in _incoming) _attributed.Add(f);
        _incoming.Clear();
        _seq++;
        _settledBy = null;
        _sw.Restart();
    }

    private void Settle(BuildStatus status, string line)
    {
        // For matchers where the first settle wins, ignore later settle lines this cycle
        // (e.g. "Now listening on" arriving before "Application started").
        if (_matcher.FirstSettleWins && _status is BuildStatus.Success or BuildStatus.Failed) return;
        _sw.Stop();
        _status = status;
        _durationMs = _sw.ElapsedMilliseconds;
        _finishedAt = _now();
        _settledBy = line.Trim();
    }

    // Blocks until the build has settled AND incorporates the latest source change AND has
    // stayed quiet for quietMs (so rapid multi-file saves debounce into one final result).
    public async Task<BuildResult> WaitForSettleAsync(int timeoutMs, int quietMs)
    {
        var overall = Stopwatch.StartNew();
        DateTimeOffset? stableSince = null;
        long stableSeq = -1;

        while (overall.ElapsedMilliseconds < timeoutMs)
        {
            BuildResult snap;
            bool ready;
            long seq;
            lock (_sync)
            {
                snap = SnapshotNoLock();
                ready = _status is BuildStatus.Success or BuildStatus.Failed && !PendingNoLock();
                seq = _seq;
            }

            if (!ready)
            {
                stableSince = null;
            }
            else
            {
                if (stableSince is null || seq != stableSeq) { stableSince = DateTimeOffset.UtcNow; stableSeq = seq; }
                if ((DateTimeOffset.UtcNow - stableSince.Value).TotalMilliseconds >= quietMs)
                    return snap;
            }

            await Task.Delay(80);
        }

        lock (_sync) return SnapshotNoLock() with { TimedOut = true };
    }

    public BuildResult Snapshot() { lock (_sync) return SnapshotNoLock(); }

    // Current build seq (read-only) — lets the inject heartbeat report the build cycle so
    // ky-ai-browser can tag console events with it (console↔build correlation).
    public long CurrentBuildSeq { get { lock (_sync) return _seq; } }

    private bool PendingNoLock()
    {
        if (_status == BuildStatus.Building) return true;
        if (_lastSourceChange is null || _finishedAt is null) return false;
        // Files the build writes during compilation (i18n localize rewriting src, etc.) land
        // before the build finishes — those must not count. Only a change after the last build
        // finished is pending, and only until the grace expires (else it never clears).
        if (_lastSourceChange <= _finishedAt) return false;
        return _now() - _lastSourceChange.Value < PendingGrace;
    }

    private BuildResult SnapshotNoLock()
    {
        var files = _attributed.Count > LineCap ? _attributed.Take(LineCap).ToList() : _attributed.ToList();
        var (stale, hint) = StaleNoLock(files);
        return new(
            _status.ToString().ToLowerInvariant(),
            _status == BuildStatus.Building,
            PendingNoLock(),
            _errors,
            _warnings,
            _durationMs,
            _startedAt?.ToString(IsoFormat),
            _finishedAt?.ToString(IsoFormat),
            _lastSourceChange?.ToString(IsoFormat),
            _seq,
            _errorLines.ToList(),
            _warningLines.ToList(),
            _diagnostics.ToList(),
            files,
            _startedBy,
            _settledBy,
            MayHaveStaleInstances: stale,
            StaleHint: hint);
    }

    // True when this INCREMENTAL rebuild incorporated a file outside the hot-swappable set (e.g. a
    // .ts), so a hot reload may have kept already-created objects on the old code. Never fires on a
    // cold start/restart (the page fully reloads anyway) or for tools that don't opt in via
    // HotReloadSafeExtensions.
    private (bool Stale, string? Hint) StaleNoLock(IReadOnlyList<string> files)
    {
        if (_hotReloadSafeExt.Count == 0) return (false, null);   // tool didn't opt in
        if (_startedBy is null) return (false, null);             // cold start/restart → full reload
        if (files.Count == 0) return (false, null);

        var changedCode = files.Any(f =>
            !_hotReloadSafeExt.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));
        if (!changedCode) return (false, null);                   // only templates/styles → hot-swaps cleanly

        return (true,
            "this rebuild changed code (not just templates/styles); a hot reload may keep " +
            "already-created objects (services, singletons, model instances) on the previous version — " +
            "call reload_page if runtime state looks stale.");
    }
}
