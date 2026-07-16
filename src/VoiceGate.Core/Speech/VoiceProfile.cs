using System.Text.Json;

namespace VoiceGate.Speech;

/// <summary>The enrolled voiceprint: an averaged speaker embedding plus quality metadata.</summary>
public sealed class VoiceProfile
{
    public string ModelFileName { get; set; } = "";
    public float[] Embedding { get; set; } = [];
    public float SelfSimilarity { get; set; }
    public double VoicedSeconds { get; set; }
    public DateTime CreatedUtc { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VoiceGate", "profile.json");

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public static VoiceProfile? Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path))
                return null;
            var profile = JsonSerializer.Deserialize<VoiceProfile>(File.ReadAllText(path));
            return profile is { Embedding.Length: > 0 } ? profile : null;
        }
        catch
        {
            return null;
        }
    }
}
