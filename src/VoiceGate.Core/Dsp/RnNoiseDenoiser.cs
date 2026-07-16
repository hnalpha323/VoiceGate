using RNNoise.NET;

namespace VoiceGate.Dsp;

/// <summary>
/// Neural noise suppression via RNNoise (48 kHz mono, 480-sample frames).
/// The strength slider becomes a wet/dry mix so suppression depth stays adjustable.
/// Construction throws if the native rnnoise.dll cannot be loaded; callers fall
/// back to <see cref="SpectralDenoiser"/>.
/// </summary>
public sealed class RnNoiseDenoiser : IDenoiser
{
    private readonly Denoiser _denoiser = new();
    private float[] _wetBuf = [];

    public string Name => "RNNoise (neural)";

    public float ReductionDb { get; set; } = 18f;

    // RNNoise estimates noise internally, so the VAD hint is unused here.
    public bool VoiceLikely
    {
        set { }
    }

    /// <summary>RNNoise adds no latency, so bypass is a plain no-op.</summary>
    public bool Bypass { get; set; }

    public void Process(Span<float> frame)
    {
        if (Bypass)
            return;
        if (_wetBuf.Length < frame.Length)
            _wetBuf = new float[frame.Length];
        Span<float> wet = _wetBuf.AsSpan(0, frame.Length);
        frame.CopyTo(wet);
        _denoiser.Denoise(wet, finish: false);

        // Map 6..40 dB to a 0.35..1.0 wet mix (full RNNoise at >= 30 dB).
        float mix = Math.Min(1f, Math.Max(0.35f, ReductionDb / 30f));
        float dry = 1f - mix;
        for (int i = 0; i < frame.Length; i++)
            frame[i] = mix * wet[i] + dry * frame[i];
    }

    public void Dispose() => _denoiser.Dispose();
}
