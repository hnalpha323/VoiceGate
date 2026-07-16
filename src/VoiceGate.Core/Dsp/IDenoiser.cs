namespace VoiceGate.Dsp;

/// <summary>A streaming denoiser processing mono 48 kHz float frames in place.</summary>
public interface IDenoiser : IDisposable
{
    string Name { get; }

    /// <summary>Suppression strength in dB (interpretation is implementation-specific).</summary>
    float ReductionDb { get; set; }

    /// <summary>Hint from the VAD; implementations may use it to steer noise estimation.</summary>
    bool VoiceLikely { set; }

    /// <summary>
    /// When true, Process becomes an identity pass-through WITHOUT changing the
    /// implementation's latency, so toggling never glitches the stream.
    /// </summary>
    bool Bypass { get; set; }

    void Process(Span<float> frame);
}
