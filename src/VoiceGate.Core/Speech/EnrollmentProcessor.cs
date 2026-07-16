namespace VoiceGate.Speech;

public sealed record EnrollmentResult(
    VoiceProfile Profile,
    double VoicedSeconds,
    float SelfSimilarity,
    float SuggestedThreshold,
    string? Warning);

/// <summary>
/// Turns a raw 16 kHz enrollment recording into a voice profile:
/// VAD-trims silence, averages embeddings over chunks, and measures
/// self-consistency so a sensible gate threshold can be suggested.
/// </summary>
public static class EnrollmentProcessor
{
    public const double MinVoicedSeconds = 6.0;
    private const int ChunkCount = 4;

    public static EnrollmentResult Process(
        float[] recording16k, string vadModelPath, SpeakerVerifier verifier, string speakerModelFileName)
    {
        float[] voiced = SileroVad.ExtractVoiced(vadModelPath, recording16k);
        double voicedSeconds = voiced.Length / (double)SileroVad.SampleRate;
        if (voicedSeconds < MinVoicedSeconds)
            throw new InvalidOperationException(
                $"Only {voicedSeconds:0.0}s of clear speech detected (need at least {MinVoicedSeconds:0}s). " +
                "Please record again, speaking continuously and closer to the microphone.");

        // Overall embedding from the full voiced audio.
        float[] overall = verifier.ComputeEmbedding(voiced);
        if (overall.Length == 0)
            throw new InvalidOperationException("The speaker model could not process the recording.");

        // Per-chunk embeddings to measure how consistent the voiceprint is.
        int chunkLen = voiced.Length / ChunkCount;
        var sims = new List<float>();
        for (int i = 0; i < ChunkCount; i++)
        {
            var chunk = new float[chunkLen];
            Array.Copy(voiced, i * chunkLen, chunk, 0, chunkLen);
            float[] emb = verifier.ComputeEmbedding(chunk);
            if (emb.Length == 0)
                continue;
            float sim = SpeakerVerifier.Cosine(emb, overall);
            if (!float.IsNaN(sim))
                sims.Add(sim);
        }

        float selfSim = sims.Count > 0 ? sims.Average() : 0f;
        // Gate threshold sits well below typical self-similarity but above impostor range.
        float suggested = Math.Min(0.55f, Math.Max(0.22f, selfSim - 0.18f));

        string? warning = null;
        if (selfSim < 0.45f)
            warning = "Your voiceprint is less consistent than ideal (noisy room or varying distance?). " +
                      "The gate will still work, but consider re-enrolling in a quieter environment.";

        var profile = new VoiceProfile
        {
            ModelFileName = speakerModelFileName,
            Embedding = overall,
            SelfSimilarity = selfSim,
            VoicedSeconds = voicedSeconds,
            CreatedUtc = DateTime.UtcNow,
        };
        return new EnrollmentResult(profile, voicedSeconds, selfSim, suggested, warning);
    }
}
