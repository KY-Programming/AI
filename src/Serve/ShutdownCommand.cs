namespace KY.AI.Serve;

// `<tool> shutdown` — tear down the whole stack for this tool: the hub plus every dev server it
// supervises. A terminal `shutdown` is meant as "stop everything"; to stop just one app, stop its
// process in your IDE instead. Just POSTs the hub's /shutdown (which cascades to the supervisors).
public static class ShutdownCommand
{
    public static async Task<int> RunAsync(string toolName, int defaultHubPort, string[] rest)
    {
        var hubUrl = $"http://127.0.0.1:{defaultHubPort}";
        for (var i = 0; i < rest.Length; i++)
        {
            if (rest[i] == "--hub-port" && ++i < rest.Length && int.TryParse(rest[i], out var p))
                hubUrl = $"http://127.0.0.1:{p}";
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            using var resp = await http.PostAsync(hubUrl.TrimEnd('/') + "/shutdown", null);
            Console.WriteLine($"{toolName} · {await resp.Content.ReadAsStringAsync()}");
            return 0;
        }
        catch
        {
            Console.WriteLine($"{toolName} · no hub running at {hubUrl} — nothing to shut down.");
            return 0;
        }
    }
}
