namespace KY.AI.Terminal;

// The permission mode plus the heuristics that decide when it is safe for the agent to inject:
// the shell should be idle at a prompt (no recent output) and the human should not be mid-line.
// Without OSC 133 shell-integration markers this is necessarily approximate; the timers are the
// v1 fallback and OSC 133 (M7) can later make "at the prompt" exact. Also holds the single
// pending proposal used by suggest mode (M5).
internal sealed class ModeState
{
    private readonly object _sync = new();
    private TerminalMode _mode;

    private long _lastOutputMs = Environment.TickCount64;
    private long _lastHumanInputMs;
    private bool _humanLineDirty;     // human has typed since the last Enter

    private string? _pending;         // staged agent command awaiting approval (suggest)
    private long _proposalId;

    // Quiet windows. Output must be silent this long to look "at a prompt"; human input must be
    // silent this long (and the current line empty) before the agent may take the keyboard.
    public int IdleQuietMs { get; init; } = 300;
    public int HumanIdleMs { get; init; } = 750;

    public ModeState(TerminalMode mode) => _mode = mode;

    public TerminalMode Mode
    {
        get { lock (_sync) return _mode; }
        set { lock (_sync) _mode = value; }
    }

    public void NoteOutput() { lock (_sync) _lastOutputMs = Environment.TickCount64; }

    public void NoteHumanInput(ReadOnlySpan<byte> bytes)
    {
        lock (_sync)
        {
            _lastHumanInputMs = Environment.TickCount64;
            foreach (var b in bytes)
            {
                if (b is (byte)'\r' or (byte)'\n') _humanLineDirty = false;   // line submitted
                else if (b >= 0x20) _humanLineDirty = true;                   // printable typed
            }
        }
    }

    public bool IsIdle()
    {
        lock (_sync)
        {
            var now = Environment.TickCount64;
            return now - _lastOutputMs >= IdleQuietMs
                && now - _lastHumanInputMs >= HumanIdleMs
                && !_humanLineDirty;
        }
    }

    // ── proposal queue (suggest mode, M5) ──────────────────────────────────────
    public long Propose(string text)
    {
        lock (_sync) { _pending = text; return ++_proposalId; }
    }

    public string? Pending { get { lock (_sync) return _pending; } }

    public bool HasPending { get { lock (_sync) return _pending is not null; } }

    // Atomically remove and return the pending proposal (on approve); null if none.
    public string? TakePending()
    {
        lock (_sync) { var p = _pending; _pending = null; return p; }
    }

    public void ClearPending() { lock (_sync) _pending = null; }
}
