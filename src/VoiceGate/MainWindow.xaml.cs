using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using VoiceGate.Audio;
using VoiceGate.Config;
using VoiceGate.Devices;
using VoiceGate.Models;
using VoiceGate.Speech;

namespace VoiceGate;

public partial class MainWindow : Window
{
    private readonly AudioEngine _engine = new();
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _uiTimer;
    private VoiceProfile? _profile;

    /// <summary>False until the constructor finishes, so control events don't fire into a half-built window.</summary>
    private readonly bool _ready;

    private static readonly Brush LightOff = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x50));
    private static readonly Brush LightGreen = new SolidColorBrush(Color.FromRgb(0x37, 0xD6, 0x7A));
    private static readonly Brush LightRed = new SolidColorBrush(Color.FromRgb(0xFF, 0x54, 0x70));

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        _profile = VoiceProfile.Load();

        SldThreshold.Value = _settings.AcceptThreshold;
        SldReduction.Value = _settings.NoiseReductionDb;
        SldRelease.Value = _settings.ReleaseMs;
        SldLookahead.Value = _settings.LookaheadMs;
        CmbMode.SelectedIndex = Math.Clamp(_settings.GateMode, 0, 2);
        ChkDenoise.IsChecked = _settings.DenoiseEnabled;
        ChkExclusive.IsChecked = _settings.ExclusiveMic;
        ChkMonitor.IsChecked = _settings.MonitorOutput;

        _engine.ErrorOccurred += msg => Dispatcher.BeginInvoke(() => OnEngineError(msg));
        _engine.StatusMessage += msg => Dispatcher.BeginInvoke(() => TxtStatusMsg.Text = msg);

        LoadDevices();
        UpdateModelStatus();
        UpdateProfileStatus();

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(66) };
        _uiTimer.Tick += UiTimer_Tick;
        _uiTimer.Start();

        _ready = true;
        UpdateSliderLabels();
    }

    // ---------- devices ----------

    private void LoadDevices()
    {
        // Snapshot the wanted IDs first: replacing ItemsSource clears the selection and
        // fires SelectionChanged, which would overwrite the saved IDs with null.
        string? wantMic = (CmbMic.SelectedItem as AudioDeviceInfo)?.Id ?? _settings.MicDeviceId;
        string? wantOutput = (CmbOutput.SelectedItem as AudioDeviceInfo)?.Id ?? _settings.OutputDeviceId;

        var mics = DeviceService.GetDevices(DataFlow.Capture)
            .Where(d => !d.IsVbCable) // never list VoiceGate's own cable as an input
            .ToList();
        var outputs = DeviceService.GetDevices(DataFlow.Render);

        CmbMic.ItemsSource = mics;
        CmbOutput.ItemsSource = outputs;

        CmbMic.SelectedItem =
            mics.FirstOrDefault(d => d.Id == wantMic)
            ?? mics.FirstOrDefault(d => d.Id == DeviceService.GetDefaultCaptureId())
            ?? mics.FirstOrDefault();

        CmbOutput.SelectedItem =
            outputs.FirstOrDefault(d => d.Id == wantOutput)
            ?? outputs.FirstOrDefault(d => d.IsVbCable)
            ?? outputs.FirstOrDefault();

        CableBanner.Visibility = DeviceService.IsVbCableInstalled ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => LoadDevices();

    private void Device_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
            return;
        _settings.MicDeviceId = (CmbMic.SelectedItem as AudioDeviceInfo)?.Id;
        _settings.OutputDeviceId = (CmbOutput.SelectedItem as AudioDeviceInfo)?.Id;
        if (_engine.IsRunning)
            TxtStatusMsg.Text = "Device change takes effect after Stop / Start.";
    }

    // ---------- status panels ----------

    private void UpdateModelStatus()
    {
        var lines = ModelRegistry.All
            .Select(m => $"{(ModelRegistry.IsPresent(m) ? "✓" : "✗")} {m.Description}");
        TxtModelsStatus.Text = string.Join("   ", lines);
        BtnDownloadModels.Visibility = ModelRegistry.AllPresent ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateProfileStatus()
    {
        if (_profile == null)
        {
            TxtProfileStatus.Text =
                "No voice profile enrolled yet. Enroll your voice so the gate can tell you apart from " +
                "other people. Until then, only generic speech detection is used.";
        }
        else
        {
            TxtProfileStatus.Text =
                $"Voiceprint enrolled {_profile.CreatedUtc:yyyy-MM-dd} " +
                $"({_profile.VoicedSeconds:0.0}s of speech, self-similarity {_profile.SelfSimilarity:0.00}).";
        }
    }

    // ---------- engine ----------

    private void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.IsRunning)
        {
            StopEngine();
            return;
        }

        if (CmbMic.SelectedItem is not AudioDeviceInfo mic)
        {
            MessageBox.Show(this, "Select a microphone first.", "VoiceGate");
            return;
        }
        if (CmbOutput.SelectedItem is not AudioDeviceInfo output)
        {
            MessageBox.Show(this, "Select an output device first.", "VoiceGate");
            return;
        }
        if (!ModelRegistry.IsPresent(ModelRegistry.Vad))
        {
            MessageBox.Show(this,
                "The AI models are not downloaded yet. Click \"Download models...\" first.",
                "VoiceGate", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!output.IsVbCable)
        {
            var answer = MessageBox.Show(this,
                "The selected output is not the VB-Audio virtual cable.\n\n" +
                "Sending your mic to speakers can cause a feedback loop. Continue anyway?",
                "VoiceGate", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
                return;
        }

        // A profile enrolled with a different speaker model would never match (dead mic
        // in Strict mode), so verify compatibility before enabling the speaker check.
        bool modelMatches = _profile != null
            && (string.IsNullOrEmpty(_profile.ModelFileName)
                || _profile.ModelFileName == ModelRegistry.Speaker.FileName);
        bool speakerCheck = _profile != null && modelMatches && ModelRegistry.IsPresent(ModelRegistry.Speaker);
        if (_profile == null && CmbMode.SelectedIndex > 0)
            TxtStatusMsg.Text = "No voiceprint enrolled - running with speech detection only.";
        else if (_profile != null && !modelMatches)
            TxtStatusMsg.Text = "Your voiceprint was made with a different speaker model - please re-enroll. " +
                                "Running with speech detection only.";

        var cfg = new EngineConfig
        {
            MicDeviceId = mic.Id,
            OutputDeviceId = output.Id,
            Mode = (GateMode)Math.Clamp(CmbMode.SelectedIndex, 0, 2),
            AcceptThreshold = (float)SldThreshold.Value,
            DenoiseEnabled = ChkDenoise.IsChecked == true,
            NoiseReductionDb = (float)SldReduction.Value,
            LookaheadMs = (int)SldLookahead.Value,
            ReleaseMs = (int)SldRelease.Value,
            HoldMs = _settings.HoldMs,
            ExclusiveMic = ChkExclusive.IsChecked == true,
            MonitorOutput = ChkMonitor.IsChecked == true,
            VadModelPath = ModelRegistry.PathOf(ModelRegistry.Vad),
            SpeakerModelPath = speakerCheck ? ModelRegistry.PathOf(ModelRegistry.Speaker) : null,
            ProfileEmbedding = speakerCheck ? _profile!.Embedding : null,
        };

        try
        {
            _engine.Start(cfg);
            BtnStartStop.Content = "■  Stop";
            BtnStartStop.Style = (Style)FindResource("DangerButton");
            TxtStatusMsg.Text = $"Running ({_engine.ActualCaptureMode} capture, {_engine.DenoiserName}). " +
                (speakerCheck ? "Speaker verification active." : "Speech detection only.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not start the audio engine:\n\n" + ex.Message,
                "VoiceGate", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        SaveSettings();
    }

    private void StopEngine()
    {
        _engine.Stop();
        BtnStartStop.Content = "▶  Start";
        BtnStartStop.Style = (Style)FindResource("AccentButton");
        TxtStatusMsg.Text = "Stopped.";
        ResetMeters();
    }

    private void OnEngineError(string message)
    {
        StopEngine();
        MessageBox.Show(this, message, "VoiceGate - engine stopped",
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ---------- live UI ----------

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            UpdateLiveUi();
        }
        catch (Exception ex)
        {
            // A repeating tick exception must not turn into an endless MessageBox storm.
            _uiTimer.Stop();
            TxtStatusMsg.Text = "Live display stopped: " + ex.Message;
        }
    }

    private void UpdateLiveUi()
    {
        if (!_engine.IsRunning)
            return;

        MeterIn.Value = Math.Min(1, _engine.InputLevel);
        MeterOut.Value = Math.Min(1, _engine.OutputLevel);

        float sim = _engine.Similarity;
        if (float.IsNaN(sim))
        {
            MeterSim.Value = 0;
            TxtSim.Text = "match: -";
        }
        else
        {
            MeterSim.Value = Math.Clamp(sim, 0, 1);
            TxtSim.Text = $"match: {sim:0.00} (need {SldThreshold.Value:0.00})";
            MeterSim.Foreground = sim >= SldThreshold.Value ? LightGreen : LightRed;
        }

        GateLight.Fill = _engine.GateOpen ? LightGreen : LightOff;
        TxtGate.Text = _engine.GateOpen ? "Gate OPEN" : "Gate closed";
        VadLight.Fill = _engine.VadActive ? LightGreen : LightOff;
        TxtBuffer.Text = $"output buffer: {_engine.OutputBufferedMs:0} ms";
    }

    private void ResetMeters()
    {
        MeterIn.Value = 0;
        MeterOut.Value = 0;
        MeterSim.Value = 0;
        TxtSim.Text = "match: -";
        GateLight.Fill = LightOff;
        VadLight.Fill = LightOff;
        TxtGate.Text = "Gate closed";
        TxtBuffer.Text = "";
    }

    // ---------- settings handlers ----------

    private void UpdateSliderLabels()
    {
        CultureInfo ui = CultureInfo.CurrentCulture;
        TxtThreshold.Text = SldThreshold.Value.ToString("0.00", ui);
        TxtReduction.Text = ((int)SldReduction.Value).ToString(ui);
        TxtRelease.Text = ((int)SldRelease.Value).ToString(ui);
        TxtLookahead.Text = ((int)SldLookahead.Value).ToString(ui);
    }

    private void Threshold_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready)
            return;
        _engine.AcceptThreshold = (float)SldThreshold.Value;
        UpdateSliderLabels();
    }

    private void Reduction_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready)
            return;
        _engine.NoiseReductionDb = (float)SldReduction.Value;
        UpdateSliderLabels();
    }

    private void Release_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready)
            return;
        _engine.SetReleaseMs((int)SldRelease.Value);
        UpdateSliderLabels();
    }

    private void Lookahead_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready)
            return;
        UpdateSliderLabels();
        if (_engine.IsRunning)
            TxtStatusMsg.Text = "Lookahead applies after Stop / Start.";
    }

    private void Mode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
            return;
        _engine.Mode = (GateMode)Math.Clamp(CmbMode.SelectedIndex, 0, 2);
    }

    private void Denoise_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready)
            return;
        _engine.DenoiseEnabled = ChkDenoise.IsChecked == true;
    }

    private void SaveSettings()
    {
        _settings.MicDeviceId = (CmbMic.SelectedItem as AudioDeviceInfo)?.Id;
        _settings.OutputDeviceId = (CmbOutput.SelectedItem as AudioDeviceInfo)?.Id;
        _settings.GateMode = CmbMode.SelectedIndex;
        _settings.AcceptThreshold = (float)SldThreshold.Value;
        _settings.DenoiseEnabled = ChkDenoise.IsChecked == true;
        _settings.NoiseReductionDb = (float)SldReduction.Value;
        _settings.ReleaseMs = (int)SldRelease.Value;
        _settings.LookaheadMs = (int)SldLookahead.Value;
        _settings.ExclusiveMic = ChkExclusive.IsChecked == true;
        _settings.MonitorOutput = ChkMonitor.IsChecked == true;
        try
        {
            _settings.Save();
        }
        catch { /* non-fatal */ }
    }

    // ---------- actions ----------

    private void Enroll_Click(object sender, RoutedEventArgs e)
    {
        if (CmbMic.SelectedItem is not AudioDeviceInfo mic)
        {
            MessageBox.Show(this, "Select a microphone first.", "VoiceGate");
            return;
        }
        if (!ModelRegistry.AllPresent)
        {
            MessageBox.Show(this,
                "Enrollment needs the AI models. Click \"Download models...\" first.",
                "VoiceGate", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        bool wasRunning = _engine.IsRunning;
        if (wasRunning)
            StopEngine();

        var window = new EnrollmentWindow(
            mic.Id,
            ModelRegistry.PathOf(ModelRegistry.Vad),
            ModelRegistry.PathOf(ModelRegistry.Speaker),
            ModelRegistry.Speaker.FileName)
        {
            Owner = this
        };

        if (window.ShowDialog() == true && window.Result != null)
        {
            _profile = window.Result.Profile;
            _profile.Save();
            SldThreshold.Value = window.Result.SuggestedThreshold;
            UpdateProfileStatus();
            TxtStatusMsg.Text = $"Voiceprint saved. Threshold set to {window.Result.SuggestedThreshold:0.00}.";
        }

        // Resume the pipeline that was stopped for enrollment (picks up the new voiceprint).
        if (wasRunning && !_engine.IsRunning)
            StartStop_Click(sender, e);
    }

    private void DownloadModels_Click(object sender, RoutedEventArgs e)
    {
        var missing = ModelRegistry.All.Where(m => !ModelRegistry.IsPresent(m)).ToList();
        if (missing.Count == 0)
        {
            UpdateModelStatus();
            return;
        }
        var window = new DownloadWindow(missing) { Owner = this };
        window.ShowDialog();
        UpdateModelStatus();
    }

    private void SetDefaultMic_Click(object sender, RoutedEventArgs e)
    {
        var cable = DeviceService.FindCableCapture();
        if (cable == null)
        {
            MessageBox.Show(this,
                "VB-Audio Virtual Cable was not found. Install it first (see the banner link).",
                "VoiceGate", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var answer = MessageBox.Show(this,
            $"Set \"{cable.Name}\" as the Windows default microphone?\n\n" +
            "Apps that use the system default will then automatically receive only your cleaned voice.",
            "VoiceGate", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            DefaultDeviceSwitcher.SetDefaultRecordingDevice(cable.Id);
            TxtStatusMsg.Text = "Virtual mic is now the Windows default microphone.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not change the default device:\n" + ex.Message,
                "VoiceGate", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _uiTimer.Stop();
        _engine.Stop();
        _engine.Dispose();
        SaveSettings();
        base.OnClosing(e);
    }
}
