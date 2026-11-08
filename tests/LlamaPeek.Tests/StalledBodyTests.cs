using System.Net;
using System.Text;
using LlamaPeek.Core;

namespace LlamaPeek.Tests;

/// <summary>
/// Regression: HttpClient.Timeout only covers the response HEADERS
/// (HttpCompletionOption.ResponseHeadersRead). A server that sends headers then
/// stalls the body (engine restart, mid-transfer network drop) hung the poll
/// loop forever: no failure, no backoff, no red dot — icons frozen until the
/// socket died on its own or the user hit Restart.
/// </summary>
public class StalledBodyTests
{
    private sealed class NeverBodyStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => Task.Run(() => ct.WaitHandle.WaitOne(-1) ? 0 : 0, CancellationToken.None); // ignores ct like a wedged socket read
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => new(ReadAsync(buffer.ToArray(), 0, buffer.Length, ct));
    }

    private sealed class StallAfterFirstHandler : HttpMessageHandler
    {
        private int _calls;
        public StallAfterFirstHandler() => StallCount = 0;
        public int StallCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"live":{"state":"idle","queued":0,"slots":[]}}""", Encoding.UTF8, "application/json"),
                });
            Interlocked.Increment(ref StallCount);
            // Headers arrive instantly; the body never does.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new NeverBodyStream()),
            });
        }
    }

    [Fact]
    public async Task StalledBody_PollTimesOut_LoopSurvives_AndMarksUnreachable()
    {
        var handler = new StallAfterFirstHandler();
        var settings = new AppSettings
        {
            Servers = { new ServerConfig("s", "http://stub", ServerKind.Strata, false, false) },
            PollActiveMs = 300, PollIdleMs = 300, GraceMs = 1000, RefreshMs = 300,
        };
        await using var m = new StatsMonitor(settings, () => null, () => handler);
        m.Start();

        // First poll succeeds, second poll stalls on the body.
        var stallDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < stallDeadline && Volatile.Read(ref handler.StallCount) == 0)
            await Task.Delay(100);
        Assert.True(Volatile.Read(ref handler.StallCount) > 0); // stalled poll happened

        // The stall must NOT hang the loop: the 4 s overall timeout must fire,
        // the poll must be marked failed, and the loop must keep polling
        // through backoff (every later response stalls too, so it stays down).
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && Volatile.Read(ref handler.StallCount) < 2)
            await Task.Delay(250);
        Assert.True(Volatile.Read(ref handler.StallCount) >= 2,
            "poll loop stopped after the stalled response");
        var rt2 = StallRuntime(m)!;
        Assert.False(rt2.Reachable);
        Assert.True(rt2.BackoffMs > 0);
    }

    private static ServerRuntime? StallRuntime(StatsMonitor m)
    {
        // Reachable/BackoffMs live on the runtime; surface them via reflection
        // (the monitor owns the list).
        var runtimes = typeof(StatsMonitor).GetField("_runtimes",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(m)
            as System.Collections.IEnumerable;
        foreach (var o in runtimes!) return (ServerRuntime)o;
        return null;
    }
}
