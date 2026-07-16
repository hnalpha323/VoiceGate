using System.Windows;
using System.Windows.Threading;
using VoiceGate.Audio;
using VoiceGate.Speech;

namespace VoiceGate;

public partial class EnrollmentWindow : Window
{
    private const double MaxSeconds = 30;
    private const double MinSeconds = 12;

    private readonly string _micId;
    private readonly string _vadModelPath;
    private readonly string _speakerModelPath;
    private readonly string _speakerModelFileName;
    private readonly EnrollmentRecorder _recorder = new();
    private readonly DispatcherTimer _timer;
    private bool _recording;
    private bool _processing;

    public EnrollmentResult? Result { get; private set; }

    public EnrollmentWindow(string micId, string vadModelPath, string speakerModelPath, string speakerModelFileName)
    {
        InitializeComponent();
        _micId = micId;
        _vadModelPath = vadModelPath;
        _speakerModelPath = speakerModelPath;
        _speakerModelFileName = speakerModelFileName;

        _recorder.ErrorOccurred += msg => Dispatcher.BeginInvoke(() =>
        {
            TxtStatus.Text = "Recording error: " + msg;
            StopRecording(process: false);
        });

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _timer.Tick += Timer_Tick;
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (!_recording)
            return;
        double seconds = _recorder.Seconds;
        MeterLevel.Value = Math.Min(1, _recorder.Level);
        PrgTime.Value = Math.Min(MaxSeconds, seconds);
        TxtTime.Text = $"{seconds:0} s";
        if (seconds >= MaxSeconds)
            StopRecording(process: true);
    }

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_processing)
            return;
        if (_recording)
        {
            if (_recorder.Seconds < MinSeconds)
            {
                TxtStatus.Text = $"Keep reading - at least {MinSeconds:0} seconds are needed.";
                return;
            }
            StopRecording(process: true);
            return;
        }

        try
        {
            Result = null;
            BtnAccept.IsEnabled = false;
            _recorder.Start(_micId);
            _recording = true;
            _timer.Start();
            BtnRecord.Content = "⏹  Stop";
            TxtStatus.Text = "Recording... read the text aloud now.";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "Could not start recording: " + ex.Message;
        }
    }

    private async void StopRecording(bool process)
    {
        if (!_recording)
            return;
        _recording = false;
        _timer.Stop();
        BtnRecord.Content = "⏺  Record";
        MeterLevel.Value = 0;

        float[] samples = _recorder.Stop();
        if (!process)
            return;

        _processing = true;
        BtnRecord.IsEnabled = false;
        TxtStatus.Text = "Analyzing your voice... this takes a few seconds.";
        try
        {
            EnrollmentResult result = await Task.Run(() =>
            {
                using var verifier = new SpeakerVerifier(_speakerModelPath);
                return EnrollmentProcessor.Process(samples, _vadModelPath, verifier, _speakerModelFileName);
            });

            Result = result;
            BtnAccept.IsEnabled = true;
            TxtStatus.Text =
                $"Analysis complete: {result.VoicedSeconds:0.0}s of clear speech, " +
                $"voiceprint consistency {result.SelfSimilarity:0.00}, " +
                $"suggested gate threshold {result.SuggestedThreshold:0.00}." +
                (result.Warning != null ? "\n⚠ " + result.Warning : "\nClick \"Save voiceprint\" to finish.");
        }
        catch (Exception ex)
        {
            TxtStatus.Text = ex.Message + "\nPress Record to try again.";
        }
        finally
        {
            _processing = false;
            BtnRecord.IsEnabled = true;
        }
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (Result == null)
            return;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _timer.Stop();
        _recorder.Dispose();
        base.OnClosing(e);
    }
}
