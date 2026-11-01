namespace LlamaPeek.Core;

/// <summary>
/// Owns the pollers and the aggregator; exposes snapshots on a refreshMs tick.
/// Restart() = rebuild pollers from new settings + reset aggregator (cached
/// maxima included) — the pipeline behind the Restart menu item AND settings save.
/// </summary>
public sealed class StatsMonitor : IAsyncDisposable
{
    private readonly Func<string?> _apiKey;
    private readonly Func<HttpMessageHandler?> _handlerFactory;   // tests inject stubs
    private Aggregator _agg = null!;
    private AppSettings _settings = null!;
    private List<ServerRuntime> _runtimes = [];
    private CancellationTokenSource? _refreshCts;
    private Task? _refreshLoop;

    public event Action<Snapshot>? SnapshotUpdated;

    public StatsMonitor(AppSettings settings, Func<string?> apiKey,
                   Func<HttpMessageHandler?>? handlerFactory = null)
    {
        _apiKey = apiKey;
        _handlerFactory = handlerFactory ?? (() => null);
        Build(settings);
    }

    private void Build(AppSettings settings)
    {
        _settings = settings.Clone();
        _agg = new Aggregator(_settings.Smoothing);
        _runtimes = _settings.Servers
            .Select(s => new ServerRuntime(s, _settings, _agg, _apiKey, _handlerFactory()))
            .ToList();
    }

    public void Start()
    {
        foreach (var rt in _runtimes) rt.Start();
        _refreshCts = new CancellationTokenSource();
        _refreshLoop = Task.Run(() => RefreshLoopAsync(_refreshCts.Token));
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(_settings.RefreshMs);
        while (!ct.IsCancellationRequested)
        {
            var snap = Current();
            SnapshotUpdated?.Invoke(snap);
            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public Snapshot Current()
    {
        var snap = _agg.Read();
        snap.AnyUnreachable = _runtimes.Any(r => !r.Reachable);
        snap.RequiredUnreachable = _runtimes.Any(r => !r.Reachable && r.Required);
        return snap;
    }

    /// <summary>Full restart: reload settings, reconnect servers, reset maxima.</summary>
    public async Task RestartAsync(AppSettings? newSettings = null)
    {
        await StopRuntimesAsync();
        Build(newSettings ?? _settings);
        Start();
    }

    private async Task StopRuntimesAsync()
    {
        _refreshCts?.Cancel();
        if (_refreshLoop is not null) { try { await _refreshLoop; } catch { } }
        _refreshLoop = null;
        foreach (var rt in _runtimes) await rt.DisposeAsync();
        _runtimes = [];
    }

    public async ValueTask DisposeAsync() => await StopRuntimesAsync();
}
