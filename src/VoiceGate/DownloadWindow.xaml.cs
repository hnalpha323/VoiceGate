using System.Windows;
using VoiceGate.Models;

namespace VoiceGate;

public partial class DownloadWindow : Window
{
    private readonly List<ModelInfo> _models;
    private CancellationTokenSource? _cts;

    public DownloadWindow(List<ModelInfo> models)
    {
        InitializeComponent();
        _models = models;
        TxtFileList.Text = string.Join("\n", models.Select(m =>
            $"• {m.FileName}  (~{m.SizeBytes / 1_000_000.0:0.#} MB)  -  {m.Description}"));
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        BtnDownload.IsEnabled = false;
        _cts = new CancellationTokenSource();
        try
        {
            for (int i = 0; i < _models.Count; i++)
            {
                ModelInfo model = _models[i];
                TxtDlStatus.Text = $"Downloading {model.FileName} ({i + 1}/{_models.Count})...";
                int index = i;
                var progress = new Progress<double>(p =>
                    PrgDownload.Value = (index + p) / _models.Count);
                await ModelDownloader.DownloadAsync(model, progress, _cts.Token);
            }
            PrgDownload.Value = 1;
            TxtDlStatus.Text = "All models downloaded successfully.";
            BtnClose.Content = "Done";
        }
        catch (OperationCanceledException)
        {
            TxtDlStatus.Text = "Download cancelled.";
            BtnDownload.IsEnabled = true;
        }
        catch (Exception ex)
        {
            TxtDlStatus.Text = "Download failed: " + ex.Message +
                "\nCheck your internet connection and try again (or run setup-models.ps1).";
            BtnDownload.IsEnabled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Covers the title-bar X and Alt+F4 too: an orphaned download would keep the
        // .download temp file locked and make every retry fail with a sharing violation.
        _cts?.Cancel();
        base.OnClosing(e);
    }
}
