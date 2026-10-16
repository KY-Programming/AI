using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KY.AI.Browser;
using Xunit;

namespace KY.AI.Browser.Tests;

// The heartbeat that keeps ky-ai-ng from reverting our index.html inject. A request that times out
// (ng busy, e.g. during an npm install) surfaces as an OperationCanceledException — it must NOT end the
// loop, or ng's watchdog reverts the tag 15s later and the page silently loses its connection.
public class HeartbeatLoopTests
{
    [Fact]
    public async Task A_timed_out_heartbeat_does_not_stop_the_loop()
    {
        var handler = new SlowFirstHandler();
        using var cts = new CancellationTokenSource();
        var loop = Program.HeartbeatLoopAsync("http://127.0.0.1:1", "<script></script>", cts.Token,
            handler, timeout: TimeSpan.FromMilliseconds(100), interval: TimeSpan.FromMilliseconds(20));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (handler.Calls < 3 && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(handler.Calls >= 3, $"loop stopped after the timed-out beat ({handler.Calls} call(s))");

        cts.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));   // our own cancellation still ends it
    }

    // First request hangs past the client timeout; every later one answers like ng does.
    private sealed class SlowFirstHandler : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1) await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true,\"active\":true,\"buildSeq\":1}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
