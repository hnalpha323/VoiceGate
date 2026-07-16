using System.IO;
using System.Net.Http;

namespace VoiceGate.Models;

/// <summary>Downloads model files with progress reporting; writes to a temp file then moves into place.</summary>
public static class ModelDownloader
{
    public static async Task DownloadAsync(ModelInfo model, IProgress<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ModelRegistry.ModelsDir);
        string target = ModelRegistry.PathOf(model);
        string temp = target + ".download";
        try
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
        catch { /* stale temp from an old run */ }

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(15);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("VoiceGate/1.0");

        using HttpResponseMessage response =
            await http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? model.SizeBytes;
        try
        {
            await using (Stream source = await response.Content.ReadAsStreamAsync(ct))
            await using (FileStream dest = File.Create(temp))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await dest.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0)
                        progress.Report(Math.Min(1.0, (double)done / total));
                }
            }
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch { }
            throw;
        }
    }
}
