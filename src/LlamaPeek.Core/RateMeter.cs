namespace LlamaPeek.Core;

/// <summary>
/// Measures a counter's rate across the gap between counter CHANGES, not between
/// polls: llama.cpp advances n_prompt_tokens_processed in --batch-size jumps, so
/// dividing a jump by one poll interval overstates the rate. Zero-delta samples
/// keep the last reading (a batch boundary is not zero throughput).
/// </summary>
public sealed class RateMeter(double smoothing)
{
    long _lastCount;
    DateTime? _lastChangeUtc;
    double? _rate;

    public double? Rate => _rate;

    /// <param name="replace">True on the first reading of a new phase: adopt the
    /// value outright instead of blending a prefill rate into a decode rate.</param>
    public void Update(long count, DateTime nowUtc, bool replace = false)
    {
        if (count == _lastCount) return;
        if (_lastChangeUtc is { } last && count > _lastCount)
        {
            double dt = (nowUtc - last).TotalSeconds;
            if (dt > 0.05)
            {
                double inst = (count - _lastCount) / dt;
                _rate = replace || _rate is null ? inst : _rate + smoothing * (inst - _rate);
            }
        }
        _lastCount = count;
        _lastChangeUtc = nowUtc;
    }

    /// <summary>Adopt a server-computed rate (strata prefill_tok_s_mean).</summary>
    public void SetDirect(double value) => _rate = value;

    public void Reset()
    {
        _lastCount = 0;
        _lastChangeUtc = null;
        _rate = null;
    }
}
