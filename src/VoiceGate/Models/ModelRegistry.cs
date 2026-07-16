using System.IO;

namespace VoiceGate.Models;

public sealed record ModelInfo(string Key, string FileName, string Url, long SizeBytes, string Description);

/// <summary>
/// The ONNX models VoiceGate needs at runtime. They are NOT bundled with the app;
/// the user downloads them once (in-app button or setup-models.ps1) from the
/// official sherpa-onnx GitHub releases.
/// </summary>
public static class ModelRegistry
{
    public static string ModelsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VoiceGate", "models");

    public static ModelInfo Vad { get; } = new(
        Key: "vad",
        FileName: "silero_vad.onnx",
        Url: "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx",
        SizeBytes: 643_854,
        Description: "Silero voice activity detection (~0.6 MB)");

    public static ModelInfo Speaker { get; } = new(
        Key: "speaker",
        FileName: "3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx",
        Url: "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx",
        SizeBytes: 28_281_164,
        Description: "CAM++ speaker verification, language-robust (~28 MB)");

    public static IReadOnlyList<ModelInfo> All { get; } = [Vad, Speaker];

    public static string PathOf(ModelInfo model) => Path.Combine(ModelsDir, model.FileName);

    public static bool IsPresent(ModelInfo model)
    {
        // Exact size: the URLs are immutable GitHub release assets, and anything
        // truncated would crash the native ONNX loader with a cryptic error.
        var fi = new FileInfo(PathOf(model));
        return fi.Exists && fi.Length == model.SizeBytes;
    }

    public static bool AllPresent => All.All(IsPresent);
}
