using System.IO;
using System.Text.Json;

namespace VoiceGate.Config;

public sealed class AppSettings
{
    public string? MicDeviceId { get; set; }
    public string? OutputDeviceId { get; set; }

    /// <summary>0 = VAD only, 1 = Balanced, 2 = Strict (matches GateMode).</summary>
    public int GateMode { get; set; } = 1;

    public float AcceptThreshold { get; set; } = 0.40f;
    public bool DenoiseEnabled { get; set; } = true;
    public float NoiseReductionDb { get; set; } = 18f;
    public int LookaheadMs { get; set; } = 120;
    public int ReleaseMs { get; set; } = 120;
    public int HoldMs { get; set; } = 350;
    public bool ExclusiveMic { get; set; }
    public bool MonitorOutput { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VoiceGate", "settings.json");

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DefaultPath)!);
        File.WriteAllText(DefaultPath, JsonSerializer.Serialize(this, JsonOptions));
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(DefaultPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(DefaultPath)) ?? new AppSettings();
        }
        catch
        {
            // Corrupt settings fall back to defaults.
        }
        return new AppSettings();
    }
}
