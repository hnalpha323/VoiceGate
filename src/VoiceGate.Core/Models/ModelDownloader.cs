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
            // The stream/array overloads below are the ones netstandard2.0 also has.
            using (Stream source = await response.Content.ReadAsStreamAsync())
            using (FileStream dest = File.Create(temp))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    await dest.WriteAsync(buffer, 0, read, ct);
                    done += read;
                    if (total > 0)
                        progress.Report(Math.Min(1.0, (double)done / total));
                }
            }
            if (File.Exists(target))
                File.Delete(target);
            File.Move(temp, target);
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
