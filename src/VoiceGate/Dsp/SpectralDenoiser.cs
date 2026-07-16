namespace VoiceGate.Dsp;

/// <summary>
/// Streaming spectral noise suppressor using a decision-directed Wiener gain
/// (Ephraim-Malah style a-priori SNR estimate) over a 1024-point STFT with 50%
/// overlap and sqrt-Hann analysis/synthesis windows (COLA-compliant).
///
/// Feed arbitrary-length blocks via <see cref="Process"/>; the same number of
/// samples comes back with a constant latency of <see cref="LatencySamples"/>.
/// The noise spectrum adapts whenever <see cref="VoiceLikely"/> is false
/// (driven externally by the VAD), and tracks downward at all times.
/// </summary>
public sealed class SpectralDenoiser : IDenoiser
{
    public const int FftSize = 1024;
    public const int Hop = FftSize / 2;

    /// <summary>
    /// Total pipeline delay: Hop samples of output priming (keeps in/out counts 1:1
    /// for arbitrary block sizes) + one analysis frame before a sample is fully
    /// reconstructed by both overlapping windows. Verified by the bypass identity test.
    /// </summary>
    public const int LatencySamples = FftSize;

    private const float Eps = 1e-12f;
    private const float DdAlpha = 0.96f;
    private const int WarmupFrames = 20;

    private readonly float[] _window = new float[FftSize];
    private readonly float[] _analysis = new float[FftSize];
    private readonly float[] _ola = new float[FftSize];
    private readonly float[] _re = new float[FftSize];
    private readonly float[] _im = new float[FftSize];
    private readonly float[] _noise = new float[FftSize / 2 + 1];
    private readonly float[] _prevSnr = new float[FftSize / 2 + 1];
    private readonly float[] _prevGain2 = new float[FftSize / 2 + 1];
    private readonly float[] _gain = new float[FftSize / 2 + 1];

    private readonly FloatRingBuffer _inFifo = new(FftSize * 16);
    private readonly FloatRingBuffer _outFifo = new(FftSize * 16);
    private readonly float[] _hopBuf = new float[Hop];

    private int _framesProcessed;
    private volatile bool _voiceLikely;

    public string Name => "Spectral (built-in)";

    /// <summary>Set by the engine from the VAD: when true, the noise estimate is frozen (slow creep only).</summary>
    public bool VoiceLikely
    {
        get => _voiceLikely;
        set => _voiceLikely = value;
    }

    /// <summary>Maximum suppression depth in dB (gain floor). Typical range 6-40.</summary>
    public float ReductionDb { get; set; } = 18f;

    /// <summary>
    /// Identity pass-through that keeps the STFT pipeline (and its fixed latency)
    /// running, so enabling/disabling never shifts the stream in time.
    /// Noise estimation continues while bypassed for an instant re-enable.
    /// </summary>
    public bool Bypass { get; set; }

    public SpectralDenoiser()
    {
        for (int i = 0; i < FftSize; i++)
        {
            // Periodic Hann; sqrt so analysis*synthesis windows satisfy COLA at 50% overlap.
            double hann = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / FftSize);
            _window[i] = (float)Math.Sqrt(hann);
        }
        Reset();
    }

    public void Reset()
    {
        _inFifo.Clear();
        _outFifo.Clear();
        Array.Clear(_analysis);
        Array.Clear(_ola);
        Array.Clear(_noise);
        Array.Clear(_prevSnr);
        Array.Clear(_prevGain2);
        _framesProcessed = 0;
        // Prime the output so sample counts stay 1:1 with a fixed latency.
        _outFifo.Write(new float[FftSize - Hop]);
    }

    public void Process(Span<float> frame)
    {
        _inFifo.Write(frame);
        while (_inFifo.Available >= Hop)
        {
            Array.Copy(_analysis, Hop, _analysis, 0, FftSize - Hop);
            _inFifo.Read(_hopBuf);
            _hopBuf.CopyTo(_analysis.AsSpan(FftSize - Hop));
            ProcessFrame();
        }
        int got = _outFifo.Read(frame);
        // By construction the FIFO always holds enough; zero-fill defensively if not.
        if (got < frame.Length)
            frame[got..].Clear();
    }

    private void ProcessFrame()
    {
        for (int i = 0; i < FftSize; i++)
        {
            _re[i] = _analysis[i] * _window[i];
            _im[i] = 0f;
        }
        Fft.Transform(_re, _im, forward: true);

        int half = FftSize / 2;
        bool warmup = _framesProcessed < WarmupFrames;
        bool updateNoise = warmup || !_voiceLikely;

        for (int k = 0; k <= half; k++)
        {
            float p = _re[k] * _re[k] + _im[k] * _im[k];
            float n = _noise[k];

            if (_framesProcessed == 0)
                n = p;
            else if (warmup)
                n = 0.8f * n + 0.2f * p;
            else if (p < n)
                n = 0.85f * n + 0.15f * p;      // always track downward quickly
            else if (updateNoise)
                n = 0.95f * n + 0.05f * p; // rise only during non-speech
            else
                n *= 1.0002f;                               // slow creep so it never locks low
            _noise[k] = n;

            float snrPost = p / (n + Eps);
            float prio = DdAlpha * _prevGain2[k] * _prevSnr[k]
                       + (1f - DdAlpha) * MathF.Max(snrPost - 1f, 0f);
            _gain[k] = prio / (1f + prio);
            _prevSnr[k] = snrPost;
        }

        float gMin = MathF.Pow(10f, -ReductionDb / 20f);
        bool bypass = Bypass;
        for (int k = 0; k <= half; k++)
        {
            float g;
            if (bypass)
            {
                g = 1f;
            }
            else
            {
                g = _gain[k];
                if (k > 0 && k < half)
                    g = 0.25f * _gain[k - 1] + 0.5f * _gain[k] + 0.25f * _gain[k + 1];
                g = MathF.Max(g, gMin);
            }
            _prevGain2[k] = g * g;

            _re[k] *= g;
            _im[k] *= g;
            if (k > 0 && k < half)
            {
                _re[FftSize - k] *= g;
                _im[FftSize - k] *= g;
            }
        }
        _framesProcessed++;

        Fft.Transform(_re, _im, forward: false);

        for (int i = 0; i < FftSize; i++)
            _ola[i] += _re[i] * _window[i];

        _outFifo.Write(_ola.AsSpan(0, Hop));
        Array.Copy(_ola, Hop, _ola, 0, FftSize - Hop);
        Array.Clear(_ola, FftSize - Hop, Hop);
    }

    public void Dispose()
    {
        // No unmanaged resources.
    }
}
