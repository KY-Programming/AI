using System.Diagnostics;

namespace KY.AI.Serve;

internal enum BuildStatus { Unknown, Building, Success, Failed }

// The settled (or in-progress) result of a dev-server build, as observed from the output
// stream.
//   building  — a (re)build is currently running
//   pending   — a source file changed that the latest build hasn't incorporated yet
//   seq       — increments per build, so callers can tell builds apart
//   settledBy — the exact output line that produced the success/failed verdict
//               (null while building/unknown); makes a mis-matched detector obvious
//   timedOut  — wait_for_build gave up before the build settled
internal sealed record BuildResult(
    string Status,
    bool Building,
    bool Pending,
    int Errors,
    long? DurationMs,
    string? StartedAt,
    string? FinishedAt,
    long Seq,
    IReadOnlyList<string> ErrorLines,
    string? SettledBy = null,
    bool TimedOut = false);

// Watches the dev-server output (via a tool-specific IBuildMatcher) and a source-file
// watcher to derive the current build state. `WaitForSettleAsync` blocks until the build
// that incorporates the latest change has settled and stayed quiet — making verification
// deterministic.
internal sealed class BuildTracker
{
    private const string IsoFormat = "yyyy-MM-ddTHH:mm:ss.fffzzz";

    // How long a detected source change stays "pending" without a rebuild starting. If the
    // dev server doesn't react within this window the change wasn't rebuild-worthy (a no-op
    // touch, or the build's own output written into the source tree — e.g. Angular i18n
    // localize), so pending self-clears instead of sticking forever.
    private static readonly TimeSpan PendingGrace = TimeSpan.FromSeconds(4);

    private readonly IBuildMatcher _matcher;
    private readonly object _sync = new();
    private readonly Stopwatch _sw = new();
    private readonly List<string> _errorLines = new();

    private BuildStatus _status = BuildStatus.Unknown;
    private int _errors;
    private long? _durationMs;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _finishedAt;
    private DateTimeOffset? _lastSourceChange;
    private long _seq;
    private string? _settledBy;

    public BuildTracker(IBuildMatcher matcher) => _matcher = matcher;

    public void MarkBuilding() { lock (_sync) BeginNoLock(); }

    // Called by the file watcher the moment a source file changes — before the dev server reacts.
    public void NoteSourceChange() { lock (_sync) _lastSourceChange = DateTimeOffset.Now; }

    public void Observe(string line)
    {
        lock (_sync)
        {
            switch (_matcher.Classify(line, _status == BuildStatus.Building))
            {
                case LineKind.BuildStart:
                    BeginNoLock();
                    break;
                case LineKind.Error:
                    _errors++;
                    if (_errorLines.Count < 20) _errorLines.Add(line.Trim());
                    break;
                case LineKind.SettledSuccess:
                    Settle(BuildStatus.Success, line);
                    break;
                case LineKind.SettledFailed:
                    Settle(BuildStatus.Failed, line);
                    break;
            }
        }
    }

    private void BeginNoLock()
    {
        _status = BuildStatus.Building;
        _errors = 0;
        _errorLines.Clear();
        _startedAt = DateTimeOffset.Now;
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
        _durationMs,
        _startedAt?.ToString(IsoFormat),
        _finishedAt?.ToString(IsoFormat),
        _seq,
        _errorLines.ToList(),
        _settledBy);
}
