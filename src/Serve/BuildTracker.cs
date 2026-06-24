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
//                       build began), so an agent can confirm its edit is reflected
//   timedOut          — wait_for_build gave up before the build settled
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
    bool TimedOut = false);

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

    private const int LineCap = 20;       // raw error/warning lines kept per build
    private const int DiagCap = 40;       // structured diagnostics kept per build
    private const int ChangedFilesCap = 100;

    private readonly IBuildMatcher _matcher;
    private readonly object _sync = new();
    private readonly Stopwatch _sw = new();
    private readonly List<string> _errorLines = new();
    private readonly List<string> _warningLines = new();
    private readonly List<BuildDiagnostic> _diagnostics = new();
    private readonly HashSet<string> _changedSinceBuild = new(StringComparer.OrdinalIgnoreCase);

    private BuildStatus _status = BuildStatus.Unknown;
    private int _errors;
    private int _warnings;
    private long? _durationMs;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _finishedAt;
    private DateTimeOffset? _lastSourceChange;
    private long _seq;
    private string? _startedBy;
    private string? _settledBy;
    private int _openDiagIndex = -1;                 // diagnostic awaiting its location line (-1 = none)
    private List<string> _filesInLastBuild = new();

    public BuildTracker(IBuildMatcher matcher) => _matcher = matcher;

    public void MarkBuilding() { lock (_sync) BeginNoLock(null); }

    // Called by the file watcher the moment a source file changes — before the dev server reacts.
    // `path` is recorded so the next build can report which files it incorporated.
    public void NoteSourceChange(string path)
    {
        lock (_sync)
        {
            _lastSourceChange = DateTimeOffset.Now;
            if (!string.IsNullOrEmpty(path) && _changedSinceBuild.Count < ChangedFilesCap)
                _changedSinceBuild.Add(path);
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
        _startedAt = DateTimeOffset.Now;
        // The files changed since the previous build began are the ones this build incorporates.
        _filesInLastBuild = _changedSinceBuild.ToList();
        _changedSinceBuild.Clear();
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
        _finishedAt = DateTimeOffset.Now;
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

    private bool PendingNoLock()
    {
        if (_status == BuildStatus.Building) return true;
        if (_lastSourceChange is null || _finishedAt is null) return false;
        // Files the build writes during compilation (i18n localize rewriting src, etc.) land
        // before the build finishes — those must not count. Only a change after the last build
        // finished is pending, and only until the grace expires (else it never clears).
        if (_lastSourceChange <= _finishedAt) return false;
        return DateTimeOffset.Now - _lastSourceChange.Value < PendingGrace;
    }

    private BuildResult SnapshotNoLock() => new(
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
        _filesInLastBuild.Count > LineCap ? _filesInLastBuild.Take(LineCap).ToList() : _filesInLastBuild.ToList(),
        _startedBy,
        _settledBy);
}
