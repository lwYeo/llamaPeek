using System.Net.Http.Json;
using System.Text.Json;

namespace LlamaPeek.Core;

/// <summary>
/// One poll loop per server. Kind auto-detect via /health ("service": "strata"),
/// re-asked after the server has been unreachable. Exponential backoff with
/// jitter on failure (1s -> 10s cap), fast cadence while working, idle cadence
/// once quiet, graceMs keeps the fast rate briefly after work stops.
/// </summary>
public sealed class ServerRuntime : IAsyncDisposable
{
    private const long MaxResponseBytes = 2_000_000;

    private readonly ServerConfig _cfg;
    private readonly AppSettings _settings;
    private readonly Aggregator _agg;
    private readonly Func<string?> _apiKey;
    private readonly HttpClient _http;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private DateTime _lastActiveUtc = DateTime.MinValue;
    private DateTime _lastModelDiscoveryUtc = DateTime.MinValue;
    private string? _modelId;
    private int _consecutiveFailures;

    public ServerRuntime(ServerConfig cfg, AppSettings settings, Aggregator agg,
                         Func<string?> apiKey, HttpMessageHandler? handler = null)
    {
        _cfg = cfg;
        _settings = settings;
        _agg = agg;
        _apiKey = apiKey;
        _http = new HttpClient(handler ?? new SocketsHttpHandler())
        { Timeout = TimeSpan.FromSeconds(4) };
    }

    public string Name => _cfg.Name;
    public bool Required => _cfg.Required;
    public bool Reachable { get; private set; }
    public ServerKind? ResolvedKind { get; private set; }
    public double BackoffMs { get; private set; }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            int delayMs;
            try
            {
                await PollOnceAsync(ct);
                _consecutiveFailures = 0;
                BackoffMs = 0;
                Reachable = true;
                bool active = _lastActiveUtc > DateTime.MinValue &&
                         DateTime.UtcNow - _lastActiveUtc <= TimeSpan.FromMilliseconds(_settings.GraceMs);
                delayMs = active ? _settings.PollActiveMs : _settings.PollIdleMs;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch
            {
                // Includes HttpClient timeouts (TaskCanceledException is an
                // OperationCanceledException): a network outage must back off
                // and RETRY, not kill the loop — that latched the red dot on
                // every icon after a brief Tailscale drop.
                Reachable = false;
                ResolvedKind = null;   // re-ask /health once back
                _agg.Clear(_cfg.Name); // slots dying mid-task must not linger in snapshots
                _consecutiveFailures++;
                double baseMs = Math.Min(10_000, 1000 * Math.Pow(2, Math.Min(_consecutiveFailures, 4)));
                BackoffMs = baseMs * (0.8 + Random.Shared.NextDouble() * 0.4);
                delayMs = (int)BackoffMs;
            }
            try { await Task.Delay(delayMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        ResolvedKind ??= await DetectKindAsync(ct);

        List<SlotReading> readings = ResolvedKind == ServerKind.Strata
            ? StrataSource.Parse(await GetJsonAsync($"{_cfg.Url.TrimEnd('/')}/metrics", ct))
            : LlamaCppSource.Parse(await GetJsonAsync(await SlotsUrlAsync(ct), ct));

        var now = DateTime.UtcNow;
        _agg.FeedBatch(_cfg.Name, readings, now);
        if (readings.Any(r => r.IsProcessing)) _lastActiveUtc = now;
    }

    private async Task<ServerKind> DetectKindAsync(CancellationToken ct)
    {
        if (_cfg.Kind != ServerKind.Auto) return _cfg.Kind;
        try
        {
            using var doc = JsonDocument.Parse(
                await GetJsonAsync($"{_cfg.Url.TrimEnd('/')}/health", ct));
            if (doc.RootElement.TryGetProperty("service", out var s) &&
                s.ValueKind == JsonValueKind.String && s.GetString() == "strata")
                return ServerKind.Strata;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        return ServerKind.LlamaCpp;
    }

    private async Task<string> SlotsUrlAsync(CancellationToken ct)
    {
        string url = $"{_cfg.Url.TrimEnd('/')}/slots";
        if (!_cfg.RequiresModel) return url;
        // Router builds need ?model=<id>; discover the loaded model every 5 s.
        if (_modelId is null || DateTime.UtcNow - _lastModelDiscoveryUtc > TimeSpan.FromSeconds(5))
        {
            try
            {
                using var doc = JsonDocument.Parse(
                    await GetJsonAsync($"{_cfg.Url.TrimEnd('/')}/v1/models", ct));
                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 &&
                    data[0].TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    _modelId = id.GetString();
                _lastModelDiscoveryUtc = DateTime.UtcNow;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        return _modelId is null ? url : $"{url}?model={Uri.EscapeDataString(_modelId)}";
    }

    private async Task<string> GetJsonAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var key = _apiKey();
        if (!string.IsNullOrEmpty(key))
            req.Headers.Authorization = new("Bearer", key);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        int total = 0, n;
        while ((n = await stream.ReadAsync(buf, ct)) > 0)
        {
            total += n;
            if (total > MaxResponseBytes)
                throw new InvalidOperationException($"response from {url} exceeds size cap");
            ms.Write(buf, 0, n);
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_loop is not null) { try { await _loop; } catch { } }
        _http.Dispose();
        _cts?.Dispose();
    }
}
