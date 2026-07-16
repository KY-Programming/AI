using System.Diagnostics;

namespace KY.AI.Serve;

// Shared "is a hub up, and if not, launch one" logic — was duplicated near-identically in
// SupervisorHost and TerminalHost; StdioBridge needs the same checks (with a persistent, not
// idle-exiting, launch) so it's pulled out here for all three to share.
internal static class HubLifecycle
{
    public static async Task<bool> HubReachableAsync(string hubUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var resp = await http.GetAsync(hubUrl.TrimEnd('/') + "/health");
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // Launch `<self> hub` detached so it outlives the launching process. Process.Start (esp.
    // UseShellExecute=true) can block for seconds if AV scans a freshly written exe; run it on a
    // dedicated thread, not the ThreadPool, so a slow spawn can never starve the pool an ASP.NET
    // Core host's own connection-accept continuations depend on.
    public static void TryLaunchHub(string toolName, int port, bool exitWhenIdle)
    {
        new Thread(() =>
        {
            try
            {
                var self = Environment.ProcessPath;
                if (self is null) return;
                var psi = new ProcessStartInfo { FileName = self };
                if (OperatingSystem.IsWindows())
                {
                    // Detached with no console window — survives the launching process's Ctrl+C.
                    psi.UseShellExecute = true;
                    psi.WindowStyle = ProcessWindowStyle.Hidden;
                }
                else
                {
                    // On POSIX, exec our own binary directly (UseShellExecute=true would route
                    // through the desktop file handler instead).
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                }
                psi.ArgumentList.Add("hub");
                psi.ArgumentList.Add("--port");
                psi.ArgumentList.Add(port.ToString());
                if (exitWhenIdle) psi.ArgumentList.Add("--exit-when-idle");
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{toolName}: could not auto-start hub: {ex.Message}");
            }
        }) { IsBackground = true, Name = $"{toolName}-hub-launch" }.Start();
    }

    // Ensure a hub is reachable at hubUrl, launching one (persistently — no --exit-when-idle) if
    // it isn't, and polling /health until it answers or the timeout elapses. Used by StdioBridge,
    // which — unlike SupervisorHost's fire-and-forget launch — needs the hub actually up before it
    // can proxy a call, and shouldn't race a not-yet-listening port with its first request.
    public static async Task<bool> EnsureRunningAsync(string toolName, int port, TimeSpan timeout)
    {
        var hubUrl = $"http://127.0.0.1:{port}";
        if (await HubReachableAsync(hubUrl)) return true;

        TryLaunchHub(toolName, port, exitWhenIdle: false);

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(200);
            if (await HubReachableAsync(hubUrl)) return true;
        }
        return false;
    }
}
