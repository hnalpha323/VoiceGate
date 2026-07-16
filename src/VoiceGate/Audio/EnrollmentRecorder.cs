using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoiceGate.Dsp;
using VoiceGate.Speech;

namespace VoiceGate.Audio;

/// <summary>Records the microphone straight to a 16 kHz mono buffer for voice enrollment.</summary>
public sealed class EnrollmentRecorder : IDisposable
{
    private WasapiCapture? _capture;
    private StreamResampler? _to16k;
    private readonly List<float> _samples = new(SileroVad.SampleRate * 40);
    private readonly object _lock = new();
    private float[] _convBuf = [];
    private float[] _outBuf = [];
    private volatile float _level;

    public float Level => _level;

    public double Seconds
    {
        get
        {
            lock (_lock)
                return _samples.Count / (double)SileroVad.SampleRate;
        }
    }

    public event Action<string>? ErrorOccurred;

    public void Start(string micDeviceId)
    {
        if (_capture != null)
            throw new InvalidOperationException("Already recording.");
        try
        {
            MMDevice mic = new MMDeviceEnumerator().GetDevice(micDeviceId);
            _capture = new WasapiCapture(mic, true, 20);
            int rate = _capture.WaveFormat.SampleRate;
            _to16k = new StreamResampler(rate, SileroVad.SampleRate);
            _convBuf = new float[rate * 2];
            _outBuf = new float[rate * 2];
            lock (_lock)
                _samples.Clear();
            _capture.DataAvailable += OnData;
            _capture.StartRecording();
        }
        catch
        {
            // Roll back so a retry doesn't hit "Already recording".
            try
            {
                _capture?.Dispose();
            }
            catch { }
            _capture = null;
            throw;
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var capture = _capture;
        if (capture == null)
            return;
        try
        {
            int frames = SampleConverter.ToMonoFloat(e.Buffer, e.BytesRecorded, capture.WaveFormat, _convBuf);
            float peak = 0f;
            for (int i = 0; i < frames; i++)
            {
                float a = Math.Abs(_convBuf[i]);
                if (a > peak)
                    peak = a;
            }
            _level = Math.Max(peak, _level * 0.85f);

            int n = _to16k!.Process(_convBuf.AsSpan(0, frames), _outBuf);
            lock (_lock)
            {
                for (int i = 0; i < n; i++)
                    _samples.Add(_outBuf[i]);
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(ex.Message);
        }
    }

    /// <summary>Stops recording and returns the captured 16 kHz mono samples.</summary>
    public float[] Stop()
    {
        var capture = _capture;
        _capture = null;
        if (capture != null)
        {
            capture.DataAvailable -= OnData;
            try
            {
                capture.StopRecording();
            }
            catch { }
            try
            {
                capture.Dispose();
            }
            catch { }
        }
        _level = 0f;
        lock (_lock)
            return _samples.ToArray();
    }

    public void Dispose() => Stop();
}
