using SherpaOnnx;

namespace VoiceGate.Speech;

/// <summary>
/// Computes speaker embeddings with a sherpa-onnx speaker-verification model
/// and compares them by cosine similarity. All inputs are 16 kHz mono float.
/// </summary>
public sealed class SpeakerVerifier : IDisposable
{
    public const int SampleRate = 16000;

    private readonly SpeakerEmbeddingExtractor _extractor;
    private readonly object _lock = new();
    private bool _disposed;

    public int Dim => _extractor.Dim;

    public SpeakerVerifier(string modelPath, int numThreads = 2)
    {
        var config = new SpeakerEmbeddingExtractorConfig
        {
            Model = modelPath,
            NumThreads = numThreads,
            Debug = 0,
            Provider = "cpu",
        };
        _extractor = new SpeakerEmbeddingExtractor(config);
    }

    /// <summary>Returns an L2-normalized embedding, or an empty array if the clip is too short.</summary>
    public float[] ComputeEmbedding(float[] samples16k)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using OnlineStream stream = _extractor.CreateStream();
            stream.AcceptWaveform(SampleRate, samples16k);
            stream.InputFinished();
            if (!_extractor.IsReady(stream))
                return [];
            float[] embedding = _extractor.Compute(stream);
            Normalize(embedding);
            return embedding;
        }
    }

    public static float Cosine(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length)
            return float.NaN;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        double denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom < 1e-12 ? float.NaN : (float)(dot / denom);
    }

    public static void Normalize(float[] v)
    {
        double norm = 0;
        for (int i = 0; i < v.Length; i++)
            norm += (double)v[i] * v[i];
        norm = Math.Sqrt(norm);
        if (norm < 1e-12)
            return;
        float inv = (float)(1.0 / norm);
        for (int i = 0; i < v.Length; i++)
            v[i] *= inv;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _extractor.Dispose();
        }
    }
}
