using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace KY.AI.Serve;

// Supervises a long-running dev server: spawns the child process, tees its output to the
// console (raw, for the human in their IDE) and to a rolling log (ANSI-stripped), tracks the
// build state, watches source files so it knows when a change is still pending, and exposes
// start/stop/restart/status/wait-for-build (consumed by the local REST control API).
internal sealed class DevServer : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _fileName;
    private readonly IReadOnlyList<string> _args;
    private readonly string _workingDir;
    private readonly string _bannerCommand;
    private readonly IReadOnlyList<string> _sourceExt;
    private readonly IReadOnlyList<string> _excludeSegments;
    private readonly int _defaultTimeoutMs;
    private readonly int _defaultQuietMs;
    private readonly RollingLog _log;
    private readonly BuildTracker _tracker;
    private readonly object _ioSync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FileSystemWatcher? _watcher;
    private readonly JobObject _job = new();
    private Process? _child;

    public DevServer(SupervisorOptions opt, SupervisorConfig cfg)
    {
        _fileName = opt.ChildFileName;
        _args = opt.ChildArgs;
        _workingDir = opt.WorkingDir;
        _bannerCommand = opt.BannerCommand;
        _sourceExt = cfg.SourceExtensions;
        _excludeSegments = cfg.WatchExcludeSegments;
        _defaultTimeoutMs = cfg.DefaultTimeoutMs;
        _defaultQuietMs = cfg.DefaultQuietMs;
        Name = opt.Name;
        _log = new RollingLog(opt.LogPath, opt.LogLines);
        _tracker = new BuildTracker(cfg.Matcher);
        _watcher = TryCreateWatcher(cfg.WatchRoot(_workingDir));
    }

    public string Name { get; }
    public string? ControlUrl { get; set; }

    public bool Running { get { lock (_ioSync) return _child is { HasExited: false }; } }

    public void Start()
    {
        _gate.Wait();
        try { StartChildNoGate(); }
        finally { _gate.Release(); }
    }

    public async Task<BuildResult> RestartAsync(int? timeoutMs = null, int? quietMs = null)
    {
        await _gate.WaitAsync();
        try { await StopChildNoGateAsync(); StartChildNoGate(); }
        finally { _gate.Release(); }
        return await _tracker.WaitForSettleAsync(timeoutMs ?? _defaultTimeoutMs, quietMs ?? _defaultQuietMs);
    }

    public async Task<BuildResult> StartIfStoppedAsync(int? timeoutMs = null, int? quietMs = null)
    {
        var started = false;
        await _gate.WaitAsync();
        try { if (!Running) { StartChildNoGate(); started = true; } }
        finally { _gate.Release(); }
        return started
            ? await _tracker.WaitForSettleAsync(timeoutMs ?? _defaultTimeoutMs, quietMs ?? _defaultQuietMs)
            : _tracker.Snapshot();
    }

    public Task<BuildResult> WaitForBuildAsync(int timeoutMs, int quietMs) => _tracker.WaitForSettleAsync(timeoutMs, quietMs);

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try { await StopChildNoGateAsync(); }
        finally { _gate.Release(); }
    }

    public void SetLogCapacity(int count) => _log.SetCapacity(count);

    private void StartChildNoGate()
    {
        if (Running) return;

        var psi = new ProcessStartInfo
        {
            FileName = _fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = _workingDir,
        };
        foreach (var a in _args) psi.ArgumentList.Add(a);

        var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) OnLine(e.Data, false); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) OnLine(e.Data, true); };

        WriteLocal($"↻ {_bannerCommand}  ·  {DateTimeOffset.Now:yyyy-MM-ddTHH:mm:sszzz}");
        _tracker.MarkBuilding();
        proc.Start();
        _job.Assign(proc); // OS kills the whole child tree if the supervisor dies, however it dies
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        lock (_ioSync) _child = proc;
    }

    private async Task StopChildNoGateAsync()
    {
        Process? p;
        lock (_ioSync) { p = _child; _child = null; }
        if (p is null) return;
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                await p.WaitForExitAsync();
            }
        }
        catch { /* already gone */ }
        p.Dispose();
    }

    private void OnLine(string line, bool isErr)
    {
        lock (_ioSync) (isErr ? Console.Error : Console.Out).WriteLine(line); // raw (keeps colour)
        var clean = Ansi.Strip(line);
        _log.Add(clean);
        _tracker.Observe(clean);
    }

    private void WriteLocal(string msg)
    {
        lock (_ioSync) Console.Out.WriteLine(msg);
        _log.Add(msg);
    }

    private int? CurrentPid()
    {
        lock (_ioSync)
        {
            try { return _child is { HasExited: false } ? _child.Id : null; }
            catch { return null; }
        }
    }

    // Watch the project's source files so the tracker knows a change is pending before the dev
    // server reacts. Best-effort.
    private FileSystemWatcher? TryCreateWatcher(string watchDir)
    {
        try
        {
            var w = new FileSystemWatcher(watchDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            void OnFs(object _, FileSystemEventArgs e) { if (IsSource(e.FullPath)) _tracker.NoteSourceChange(); }
            w.Changed += OnFs;
            w.Created += OnFs;
            w.Renamed += (_, e) => { if (IsSource(e.FullPath)) _tracker.NoteSourceChange(); };
            w.EnableRaisingEvents = true;
            return w;
        }
        catch { return null; }
    }

    private bool IsSource(string path)
    {
        foreach (var seg in _excludeSegments)
            if (path.Contains(seg, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var ext in _sourceExt)
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ---- JSON payloads served by the local REST control API (and forwarded by the hub) ----

    public string StatusJson() => JsonSerializer.Serialize(new
    {
        name = Name,
        running = Running,
        pid = CurrentPid(),
        controlUrl = ControlUrl,
        logPath = _log.Path,
        logLines = _log.Capacity,
        build = _tracker.Snapshot(),
    }, Json);

    public async Task<string> RestartJsonAsync()
    {
        var build = await RestartAsync();
        return JsonSerializer.Serialize(new { name = Name, action = "restart", running = Running, build, tail = _log.Tail(15) }, Json);
    }

    public async Task<string> StartJsonAsync()
    {
        var build = await StartIfStoppedAsync();
        return JsonSerializer.Serialize(new { name = Name, action = "start", running = Running, build, tail = _log.Tail(15) }, Json);
    }

    public async Task<string> StopJsonAsync()
    {
        await StopAsync();
        return JsonSerializer.Serialize(new { name = Name, action = "stop", running = Running }, Json);
    }

    public async Task<string> WaitForBuildJsonAsync(int timeoutMs, int quietMs)
    {
        var build = await WaitForBuildAsync(timeoutMs, quietMs);
        return JsonSerializer.Serialize(new { name = Name, action = "wait_for_build", running = Running, build, tail = _log.Tail(15) }, Json);
    }

    public string TailText(int lines)
    {
        var tail = _log.Tail(lines <= 0 ? _log.Capacity : lines);
        return string.Join('\n', tail);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _job.Dispose();
        _log.Dispose();
    }
}
