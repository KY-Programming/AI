using KY.AI.Serve;
using KY.AI.Terminal.ConPty;

namespace KY.AI.Terminal;

// Owns one ConPTY-hosted shell and shuttles bytes between it and the host console:
//   PTY output → host stdout (the human sees it)   [+ tee hook for the agent-readable model]
//   host stdin → PTY input    (the human types)
// The shell process is placed under a JobObject so the whole tree dies with this process,
// however it is stopped. Later milestones layer the VtScreen tee, the agent input router, and
// the mode state machine onto the OnOutput / input seams left here.
internal sealed class TerminalSession : IDisposable
{
    private readonly TerminalSessionOptions _opt;
    private readonly JobObject _job = new();

    private HostConsole? _host;
    private PseudoConsolePipe? _input;
    private PseudoConsolePipe? _output;
    private PseudoConsole? _pty;
    private ConPtyProcess? _proc;
    private FileStream? _ptyWrite;
    private FileStream? _ptyRead;
    private VtScreen? _screen;
    private Stream? _stdout;
    private readonly object _stdoutLock = new();
    private InputRouter? _router;
    private readonly ManualResetEventSlim _outputDone = new(false);

    private volatile bool _stopping;
    private bool _shutdown;
    private readonly ModeState _modeState;
    private readonly PromptGuard _promptGuard = new();
    private readonly RollingLog _audit;

    // v2 TUI (Claude-CLI-style): top region passthrough + reserved bottom chrome (composer/status/
    // popout). Null when --no-tui (falls back to the plain transparent passthrough).
    private readonly bool _useTui;
    private Tui? _tui;
    private Composer? _composer;
    private readonly Win32InputDecoder _win32 = new();
    private short _curW, _curH;
    private volatile bool _raw;   // a full-screen app (alt-screen) or password prompt owns the screen
    private bool _welcomeActive = true;   // show the welcome in the popout until the first command
    private volatile bool _agentSeen;     // an agent has called an agent-only tool (read/inject/propose)

    private static readonly System.Text.Json.JsonSerializerOptions Json =
        new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };

    public TerminalSession(TerminalSessionOptions opt)
    {
        _opt = opt;
        _useTui = opt.Tui;
        _modeState = new ModeState(opt.InitialMode);
        _audit = new RollingLog(opt.AuditPath, 500);
    }

    // ── read surface for the REST/MCP layer ───────────────────────────────────
    public string Name => _opt.Name;
    public string ShellDisplay => _opt.ShellDisplay;
    public int Pid => _proc?.Pid ?? 0;
    public bool Running => _proc is { HasExited: false };
    public TerminalMode Mode { get => _modeState.Mode; set => _modeState.Mode = value; }
    public (int Cols, int Rows) ScreenSize => _screen?.Size ?? (0, 0);
    public (int Row, int Col) Cursor => _screen?.Cursor ?? (0, 0);
    public IReadOnlyList<string> ScreenLines() => _screen?.ScreenLines() ?? Array.Empty<string>();
    public IReadOnlyList<string> ScrollbackTail(int count) => _screen?.ScrollbackTail(count) ?? Array.Empty<string>();

    public string StatusJson()
    {
        var (cols, rows) = ScreenSize;
        var (cr, cc) = Cursor;
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            name = Name,
            shell = ShellDisplay,
            pid = Pid,
            running = Running,
            mode = _modeState.Mode.Wire(),
            idle = _modeState.IsIdle(),
            pendingProposal = _modeState.Pending,
            passwordPrompt = _promptGuard.PasswordActive,
            rawPassthrough = _raw,
            auditEntries = _audit.Count,
            cols,
            rows,
            cursor = new { row = cr, col = cc },
            scrollbackLines = _screen?.ScrollbackTail(0).Count ?? 0,
        }, Json);
    }

    public string ScreenJson()
    {
        NoteAgent();
        var (cols, rows) = ScreenSize;
        var (cr, cc) = Cursor;
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            cols,
            rows,
            cursor = new { row = cr, col = cc },
            lines = ScreenLines(),
        }, Json);
    }

    public string ScrollbackJson(int lines)
    {
        NoteAgent();
        var tail = ScrollbackTail(lines);
        return System.Text.Json.JsonSerializer.Serialize(new { count = tail.Count, lines = tail }, Json);
    }

    public string ModeJson() => J(new
    {
        ok = true,
        mode = _modeState.Mode.Wire(),
        idle = _modeState.IsIdle(),
        pendingProposal = _modeState.Pending,
    });

    // Spawn the shell, enter raw passthrough, and start the relay threads. Non-blocking: the
    // caller drives lifetime via WaitForExit. Throws (after cleaning up) if setup fails.
    public void Start()
    {
        try
        {
            _host = new HostConsole();
            var (w, h) = HostConsole.Size();
            _curW = w; _curH = h;
            // The shell only owns the top region; the bottom rows are our chrome.
            var top = _useTui ? (short)Math.Max(1, h - Tui.Bottom) : h;
            _screen = new VtScreen(w, top, _opt.Scrollback);
            _stdout = Console.OpenStandardOutput();
            if (_useTui) { _composer = new Composer(); _tui = new Tui(_stdout, w, h); }
            _router = new InputRouter(toComposer: HostInput, approve: Approve, dismiss: Dismiss);

            _input = new PseudoConsolePipe();
            _output = new PseudoConsolePipe();
            _pty = PseudoConsole.Create(_input, _output, w, top);

            // Set up the screen (clear + scroll region + chrome) BEFORE the shell starts so its
            // first prompt lands inside the top region.
            if (_useTui) lock (_stdoutLock) { _tui!.Enter(); RedrawLocked(); }

            _proc = ConPtyProcess.Start(_opt.CommandLine, _opt.WorkingDir, _pty);
            _job.Assign(_proc.ProcessHandle);

            // Release the parent's copies of the ends handed to ConPTY so EOF can propagate when
            // the shell and ConPTY close theirs (otherwise the output reader never sees EOF).
            _input.ReadSide.Dispose();
            _output.WriteSide.Dispose();

            _ptyWrite = new FileStream(_input.WriteSide, FileAccess.Write);
            _ptyRead = new FileStream(_output.ReadSide, FileAccess.Read);

            new Thread(PumpOutput) { IsBackground = true, Name = "pty-output" }.Start();
            new Thread(PumpInput) { IsBackground = true, Name = "host-input" }.Start();
            new Thread(() => PumpResize(w, h)) { IsBackground = true, Name = "resize" }.Start();
        }
        catch
        {
            Shutdown();
            throw;
        }
    }

    // Block until the shell exits; returns its exit code.
    public int WaitForExit() => _proc?.WaitForExit() ?? -1;

    // Stop the session on demand (the hub calls this when tearing the whole stack down). Tears
    // down the shell tree and restores the console; WaitForExit then returns and the host's
    // finally runs the usual deregister/cleanup. Idempotent.
    public void RequestStop() => Shutdown();

    // Convenience for the standalone (--no-hub) path: run to completion on the calling thread.
    public int Run()
    {
        try { Start(); return WaitForExit(); }
        finally { Shutdown(); }
    }

    private void PumpOutput()
    {
        try
        {
            var buf = new byte[8192];
            int n;
            while (!_stopping && (n = _ptyRead!.Read(buf, 0, buf.Length)) > 0)
            {
                // One render critical section: passthrough write + tee + chrome redraw are atomic,
                // so the chrome (which reads the shell's cursor snapshot) can't tear against output.
                lock (_stdoutLock)
                {
                    _stdout!.Write(buf, 0, n); _stdout.Flush();
                    OnOutput(buf, n);
                    if (_useTui)
                    {
                        UpdateRawMode();              // enter/exit raw on alt-screen / password
                        if (!_raw) RedrawLocked();
                    }
                }
            }
        }
        catch { /* pipe closed on shutdown */ }
        finally { _outputDone.Set(); }
    }

    private void PumpInput()
    {
        try
        {
            var buf = new byte[1024];
            var stdin = Console.OpenStandardInput();
            int n;
            while (!_stopping && (n = stdin.Read(buf, 0, buf.Length)) > 0)
            {
                // Translate win32-input-mode key records (ConPTY asks the terminal for them) back to
                // plain VT bytes; no-op when the terminal sends plain input.
                var keys = _win32.Decode(buf.AsSpan(0, n));
                if (keys.Length == 0) continue;
                if (_raw)
                {
                    // Full-screen app / password prompt: stream keys straight to the shell (vim needs
                    // Ctrl-B too), bypassing the composer and the prefix chords.
                    _modeState.NoteHumanInput(keys);
                    WriteToPty(keys);
                }
                else
                {
                    // The router intercepts prefix chords and routes the rest to the composer.
                    _router!.Feed(keys);
                }
            }
        }
        catch { /* pipe closed on shutdown */ }
    }

    private void PumpResize(short w, short h)
    {
        while (!_stopping)
        {
            try
            {
                Thread.Sleep(200);
                var (nw, nh) = HostConsole.Size();
                if (nw != w || nh != h)
                {
                    w = nw; h = nh; _curW = nw; _curH = nh;
                    _tui?.Resize(nw, nh);   // dims only
                    if (!_useTui || _raw)
                    {
                        _pty?.Resize(nw, nh);
                        _screen?.Resize(nw, nh);
                    }
                    else
                    {
                        var top = (short)Math.Max(1, nh - Tui.Bottom);
                        _pty?.Resize(nw, top);
                        _screen?.Resize(nw, top);
                        lock (_stdoutLock) { _tui!.SetScrollRegion(); RedrawLocked(); }
                    }
                }
            }
            catch { }
        }
    }

    // Write bytes to the shell's input. Centralised so agent input and arbitration share one path.
    private void WriteToPty(ReadOnlySpan<byte> bytes)
    {
        var fs = _ptyWrite;
        if (fs is null) return;
        lock (fs) { fs.Write(bytes); fs.Flush(); }
    }

    // Host keystrokes that are NOT multiplexer chords. In TUI mode they edit the composer (the line
    // is sent to the shell on Enter); without the TUI they stream straight to the shell.
    private void HostInput(ReadOnlySpan<byte> bytes)
    {
        _modeState.NoteHumanInput(bytes);

        // Decode + redraw under the render lock, but defer the PTY write until AFTER releasing it so
        // a full ConPTY input pipe can't deadlock against the output pump (which needs the same lock).
        string? submit = null;
        var interrupt = false;
        lock (_stdoutLock)
        {
            var changed = _composer!.Feed(bytes, line => submit = line, () => interrupt = true, CycleMode);
            if (submit is not null) _welcomeActive = false;   // first command dismisses the welcome
            if (changed) RedrawLocked();
        }
        if (interrupt) WriteToPty(new byte[] { 0x03 });
        if (submit is not null) WriteToPty(System.Text.Encoding.UTF8.GetBytes(submit + "\r"));
    }

    // Tee the shell's output into the agent-readable screen model + the password-prompt guard.
    private void OnOutput(byte[] buf, int n)
    {
        _modeState.NoteOutput();
        _screen?.Feed(buf.AsSpan(0, n));
        if (_screen is not null) _promptGuard.Update(_screen.CursorLineText());
    }

    // Redraw the chrome. Caller MUST hold _stdoutLock (one render critical section).
    private void RedrawLocked()
    {
        if (_tui is null) return;
        var (cr, cc) = _screen?.Cursor ?? (0, 0);
        var w = _curW;
        var mode = _modeState.Mode.Wire();
        var (display, caretCol) = _composer!.RenderWindow(w, "❯ ");
        var popout = Chrome.Popout(_modeState.Pending, _welcomeActive, _agentSeen, ShellDisplay, mode, w);
        var input = Chrome.InputRow(display, caretCol, _composer.IsEmpty, w);
        var hint = Chrome.Hint(mode, ShellDisplay, _modeState.Pending is not null, w);
        _tui.Redraw(cr, cc, popout, Chrome.BoxTop(w), input, Chrome.BoxBottom(w), hint);
    }

    private void RedrawChrome()
    {
        if (_tui is null) return;
        lock (_stdoutLock) RedrawLocked();
    }

    // An agent touched an agent-only endpoint → flip the welcome popout to "agent connected".
    private void NoteAgent()
    {
        if (_agentSeen) return;
        _agentSeen = true;
        if (_welcomeActive) RedrawChrome();
    }

    // Switch between composer chrome and full raw passthrough based on the shell's state. Caller
    // holds _stdoutLock. Raw while a full-screen app (alt-screen) or a password prompt is active.
    private void UpdateRawMode()
    {
        var want = (_screen?.AltScreenActive ?? false) || _promptGuard.PasswordActive;
        if (want == _raw) return;
        if (want) EnterRaw(); else ExitRaw();
    }

    private void EnterRaw()
    {
        _raw = true;
        _tui!.EnterRaw();                       // show cursor, drop the scroll region
        _pty?.Resize(_curW, _curH);             // give the app the whole screen
        _screen?.Resize(_curW, _curH);
    }

    private void ExitRaw()
    {
        _raw = false;
        var top = (short)Math.Max(1, _curH - Tui.Bottom);
        _pty?.Resize(_curW, top);               // reclaim the bottom rows for chrome
        _screen?.Resize(_curW, top);
        _tui!.ExitRaw();                        // hide cursor, re-assert the scroll region
        RedrawLocked();
    }

    // In TUI mode the chrome reflects state (mode/proposal), so just redraw; without the TUI fall
    // back to the scrolling banner notice.
    private void Notify(byte[] banner)
    {
        if (_tui is not null) RedrawChrome();
        else WriteHostNotice(banner);
    }

    // ── agent input surface (mode-gated, server-side) ─────────────────────────
    // Type a command line at the shell prompt. Allowed only in auto mode and only when the shell
    // is idle and the human isn't mid-line; waits briefly for an idle window before giving up.
    public async Task<string> InjectTextAsync(string text, int waitMs = 4000)
    {
        NoteAgent();
        var mode = _modeState.Mode;
        if (mode == TerminalMode.ReadOnly) return Refuse("read-only", "session is read-only");
        if (mode == TerminalMode.Suggest) return Refuse("suggest", "in suggest mode use propose_command (the human approves)");
        if (_promptGuard.PasswordActive) return Refuse("password-prompt", "a password prompt is active; agent input is blocked");

        if (!await WaitIdleAsync(waitMs))
            return Refuse("busy", "the shell is busy or the human is typing; try again");

        WriteToPty(System.Text.Encoding.UTF8.GetBytes(text));
        Audit("send_text", text);
        return J(new { ok = true, injected = text });
    }

    // Send named keys (Enter, Ctrl-C, Up, …). Not idle-gated — keys are also how you interact with
    // a running program (e.g. Ctrl-C to interrupt) — but still mode-gated.
    public string InjectKeys(string keys)
    {
        NoteAgent();
        var mode = _modeState.Mode;
        if (mode == TerminalMode.ReadOnly) return Refuse("read-only", "session is read-only");
        if (mode == TerminalMode.Suggest) return Refuse("suggest", "in suggest mode use propose_command (the human approves)");
        if (_promptGuard.PasswordActive) return Refuse("password-prompt", "a password prompt is active; agent input is blocked");

        var bytes = Keys.Translate(keys);
        if (bytes is null) return Refuse("bad-keys", $"could not parse keys: '{keys}'");
        WriteToPty(bytes);
        Audit("send_keys", keys);
        return J(new { ok = true, sent = keys });
    }

    public string SetMode(string mode)
    {
        if (!TerminalModeExtensions.TryParse(mode, out var m))
            return Refuse("bad-mode", $"unknown mode '{mode}' (use read|suggest|auto)");
        _modeState.Mode = m;
        return J(new { ok = true, mode = m.Wire() });
    }

    public string ResizeRequest(int cols, int rows)
    {
        _pty?.Resize((short)Math.Clamp(cols, 1, short.MaxValue), (short)Math.Clamp(rows, 1, short.MaxValue));
        _screen?.Resize(cols, rows);
        var (c, r) = ScreenSize;
        return J(new { ok = true, cols = c, rows = r });
    }

    // ── suggest mode: stage a command for the human to approve ────────────────
    public string ProposeJson(string text)
    {
        NoteAgent();
        if (_modeState.Mode == TerminalMode.ReadOnly)
            return Refuse("read-only", "session is read-only; switch to suggest to propose");
        var id = _modeState.Propose(text);
        Audit("propose", text);
        Notify(Banner.Proposal(text, _opt.PrefixName));
        return J(new { ok = true, proposalId = id, pending = text });
    }

    // Human-driven approve/dismiss (chord or local REST — deliberately NOT exposed as MCP tools,
    // so the agent can never approve its own proposal).
    public string ApproveJson()
    {
        var cmd = _modeState.TakePending();
        if (cmd is null) return J(new { ok = false, reason = "none", message = "no pending proposal" });
        InjectApproved(cmd);
        return J(new { ok = true, approved = cmd });
    }

    public string DismissJson()
    {
        var had = _modeState.HasPending;
        _modeState.ClearPending();
        if (had) Notify(Banner.Dismissed());
        return J(new { ok = true, dismissed = had });
    }

    private void Approve()
    {
        var cmd = _modeState.TakePending();
        if (cmd is not null) InjectApproved(cmd);
    }

    private void Dismiss()
    {
        if (_modeState.HasPending) { _modeState.ClearPending(); Notify(Banner.Dismissed()); }
    }

    private void CycleMode()
    {
        var next = _modeState.Mode switch
        {
            TerminalMode.ReadOnly => TerminalMode.Suggest,
            TerminalMode.Suggest => TerminalMode.Auto,
            _ => TerminalMode.ReadOnly,
        };
        _modeState.Mode = next;
        Notify(Banner.ModeChanged(next.Wire()));
    }

    // Inject an approved command (with Enter so it runs) once the shell is idle.
    private void InjectApproved(string cmd)
    {
        if (_promptGuard.PasswordActive)
        {
            Notify(Banner.Note("blocked: password prompt active — proposal not injected"));
            return;
        }
        Notify(Banner.Approved(cmd));
        Audit("approve", cmd);
        _ = Task.Run(async () =>
        {
            await WaitIdleAsync(4000);
            WriteToPty(System.Text.Encoding.UTF8.GetBytes(cmd + "\r"));
        });
    }

    private void Audit(string kind, string payload) =>
        _audit.Add($"{DateTime.Now:HH:mm:ss} [{_modeState.Mode.Wire()}] {kind}: {payload}");

    public string AuditJson(int lines)
    {
        var tail = _audit.Tail(lines);
        return J(new { count = tail.Count, lines = tail });
    }

    // Write a styled multiplexer notice to the human's console (not fed to the agent's screen).
    private void WriteHostNotice(byte[] bytes)
    {
        var s = _stdout;
        if (s is null) return;
        lock (_stdoutLock) { s.Write(bytes, 0, bytes.Length); s.Flush(); }
    }

    private async Task<bool> WaitIdleAsync(int waitMs)
    {
        var deadline = Environment.TickCount64 + waitMs;
        while (true)
        {
            if (_modeState.IsIdle()) return true;
            if (Environment.TickCount64 >= deadline) return false;
            await Task.Delay(50);
        }
    }

    private static string J(object o) => System.Text.Json.JsonSerializer.Serialize(o, Json);

    private static string Refuse(string reason, string message) =>
        J(new { ok = false, reason, message });

    private void Shutdown()
    {
        if (_shutdown) return;
        _shutdown = true;
        _stopping = true;

        try { _pty?.Dispose(); } catch { }          // ClosePseudoConsole → output reader hits EOF
        _outputDone.Wait(TimeSpan.FromMilliseconds(500));
        try { _ptyWrite?.Dispose(); } catch { }
        try { _ptyRead?.Dispose(); } catch { }
        try { _proc?.Dispose(); } catch { }
        try { _input?.Dispose(); } catch { }
        try { _output?.Dispose(); } catch { }
        try { if (_useTui) lock (_stdoutLock) _tui?.Leave(); } catch { }   // reset region + show cursor
        try { _host?.Restore(); } catch { }
        try { _audit.Dispose(); } catch { }
        try { _job.Dispose(); } catch { }            // reap the shell tree
    }

    public void Dispose() => Shutdown();
}
