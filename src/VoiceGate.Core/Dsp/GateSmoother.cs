namespace VoiceGate.Dsp;

/// <summary>
/// Per-sample exponential gain smoothing between 0 and 1 with independent
/// attack (opening) and release (closing) times, so the gate never clicks.
/// </summary>
public sealed class GateSmoother
{
    private float _gain;
    private float _attackCoef;
    private float _releaseCoef;

    public float CurrentGain => _gain;

    public GateSmoother(int sampleRate, float attackMs, float releaseMs)
        => Configure(sampleRate, attackMs, releaseMs);

    public void Configure(int sampleRate, float attackMs, float releaseMs)
    {
        _attackCoef = Coef(sampleRate, attackMs);
        _releaseCoef = Coef(sampleRate, releaseMs);
    }

    private static float Coef(int sampleRate, float ms)
    {
        if (ms <= 0f)
            return 0f;
        return (float)Math.Exp(-1.0 / (sampleRate * (ms / 1000.0)));
    }

    /// <summary>Applies smoothed gain toward <paramref name="target"/> (0 or 1) in place.</summary>
    public void Process(Span<float> data, float target)
    {
        float gain = _gain;
        float coef = target > gain ? _attackCoef : _releaseCoef;
        for (int i = 0; i < data.Length; i++)
        {
            // Recheck direction as gain crosses the target.
            coef = target > gain ? _attackCoef : _releaseCoef;
            gain = target + coef * (gain - target);
            data[i] *= gain;
        }
        _gain = gain;
    }

}
