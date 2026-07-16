using SherpaOnnx;

namespace VoiceGate.Speech;

/// <summary>
/// Streaming wrapper around sherpa-onnx's Silero voice activity detector.
/// Feed 16 kHz mono float samples; <see cref="IsSpeech"/> reflects the current state.
/// </summary>
public sealed class SileroVad : IDisposable
{
    public const int SampleRate = 16000;
    public const int WindowSize = 512; // samples per Silero inference (32 ms)

    private readonly VoiceActivityDetector _vad;
    private readonly float[] _chunk = new float[WindowSize];
    private int _fill;
    private bool _disposed;

    public bool IsSpeech { get; private set; }

    public SileroVad(string modelPath, float threshold = 0.5f,
                     float minSilenceSeconds = 0.25f, float minSpeechSeconds = 0.1f)
    {
        var config = new VadModelConfig();
        config.SileroVad.Model = modelPath;
        config.SileroVad.Threshold = threshold;
        config.SileroVad.MinSilenceDuration = minSilenceSeconds;
        config.SileroVad.MinSpeechDuration = minSpeechSeconds;
        config.SileroVad.WindowSize = WindowSize;
        // Default is 5 s: silero force-splits long speech, which would blip the gate mid-sentence.
        config.SileroVad.MaxSpeechDuration = 30f;
        config.SampleRate = SampleRate;
        config.NumThreads = 1;
        config.Debug = 0;
        _vad = new VoiceActivityDetector(config, bufferSizeInSeconds: 10f);
    }

    /// <summary>Feeds 16 kHz samples, updating <see cref="IsSpeech"/>.</summary>
    public void Feed(ReadOnlySpan<float> samples)
    {
        int idx = 0;
        while (idx < samples.Length)
        {
            int n = Math.Min(WindowSize - _fill, samples.Length - idx);
            samples.Slice(idx, n).CopyTo(_chunk.AsSpan(_fill));
            _fill += n;
            idx += n;
            if (_fill == WindowSize)
            {
                _vad.AcceptWaveform(_chunk);
                _fill = 0;
                // Drain completed segments; only the live speech flag is used.
                while (!_vad.IsEmpty())
                    _vad.Pop();
                IsSpeech = _vad.IsSpeechDetected();
            }
        }
    }

    /// <summary>
    /// Offline helper: returns the voiced regions of <paramref name="samples16k"/>
    /// concatenated together (used by enrollment).
    /// </summary>
    public static float[] ExtractVoiced(string modelPath, float[] samples16k)
    {
        var config = new VadModelConfig();
        config.SileroVad.Model = modelPath;
        config.SileroVad.Threshold = 0.5f;
        config.SileroVad.MinSilenceDuration = 0.25f;
        config.SileroVad.MinSpeechDuration = 0.15f;
        config.SileroVad.WindowSize = WindowSize;
        config.SileroVad.MaxSpeechDuration = 30f;
        config.SampleRate = SampleRate;
        config.NumThreads = 1;
        config.Debug = 0;

        using var vad = new VoiceActivityDetector(config, bufferSizeInSeconds: 120f);
        var voiced = new List<float>(samples16k.Length);

        var chunk = new float[WindowSize];
        int pos = 0;
        while (pos < samples16k.Length)
        {
            int n = Math.Min(WindowSize, samples16k.Length - pos);
            if (n < WindowSize)
                Array.Clear(chunk);
            Array.Copy(samples16k, pos, chunk, 0, n);
            vad.AcceptWaveform(chunk);
            pos += n;
            while (!vad.IsEmpty())
            {
                voiced.AddRange(vad.Front().Samples);
                vad.Pop();
            }
        }
        vad.Flush();
        while (!vad.IsEmpty())
        {
            voiced.AddRange(vad.Front().Samples);
            vad.Pop();
        }
        return voiced.ToArray();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _vad.Dispose();
    }
}
