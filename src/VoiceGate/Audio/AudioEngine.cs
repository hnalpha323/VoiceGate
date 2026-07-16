using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using VoiceGate.Dsp;
using VoiceGate.Speech;

namespace VoiceGate.Audio;

public enum GateMode
{
    /// <summary>Gate on voice activity only (no speaker check).</summary>
    VadOnly = 0,
    /// <summary>Open on speech immediately; close as soon as the speaker check rejects.</summary>
    Balanced = 1,
    /// <summary>Open only after the speaker check confirms it is the enrolled voice.</summary>
    Strict = 2,
}

public sealed class EngineConfig
{
    public required string MicDeviceId { get; init; }
    public required string OutputDeviceId { get; init; }
    public GateMode Mode { get; init; } = GateMode.Balanced;
    public float AcceptThreshold { get; init; } = 0.40f;
    public bool DenoiseEnabled { get; init; } = true;
    public float NoiseReductionDb { get; init; } = 18f;
    public int LookaheadMs { get; init; } = 120;
    public int ReleaseMs { get; init; } = 120;
    public int HoldMs { get; init; } = 350;
    public bool ExclusiveMic { get; init; }
    public bool MonitorOutput { get; init; }
    public required string VadModelPath { get; init; }
    public string? SpeakerModelPath { get; init; }
    public float[]? ProfileEmbedding { get; init; }
}

/// <summary>
/// Real-time pipeline: mic capture -> mono 48 kHz -> high-pass -> spectral denoise ->
/// (16 kHz side-chain: Silero VAD + speaker verification) -> lookahead gate -> virtual mic output.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public const int Rate = 48000;
    public const int FrameSize = 480; // 10 ms

    private const int SimFreshMs = 1500;       // how long a speaker verdict stays valid
    private const int SnapshotIntervalFrames = 25; // embedding cadence: every 250 ms of speech
    private const float RejectMargin = 0.08f;  // hysteresis below the accept threshold

    // Pipeline objects (created in Start, torn down in Stop)
    private WasapiCapture? _capture;
    private WasapiOut? _output;
    private BufferedWaveProvider? _outProvider;
    private WasapiOut? _monitor;
    private BufferedWaveProvider? _monitorProvider;
    private StreamResampler? _micResampler;
    private StreamResampler? _to16k;
    private IDenoiser? _denoiser;
    private BiQuadFilter? _highpass;
    private DelayLine? _delay;
    private GateSmoother? _smoother;
    private SileroVad? _vad;
    private SpeakerVerifier? _verifier;
    private float[]? _profile;

    private readonly FloatRingBuffer _captureRing = new(Rate * 4);
    private float[] _convBuf = [];
    private float[] _resampBuf = [];
    private readonly float[] _frame = new float[FrameSize];
    private float[] _buf16 = [];
    private readonly byte[] _frameBytes = new byte[FrameSize * sizeof(float)];

    // Rolling 16 kHz window for speaker snapshots (3 s)
    private readonly float[] _spkWindow = new float[SileroVad.SampleRate * 3];
    private int _spkPos;
    private long _spkTotal;
    private long _speechRunStart;
    private int _framesSinceSnapshot;
    private bool _prevVad;

    // Threads / signaling
    private Thread? _procThread;
    private Thread? _embThread;
    private readonly AutoResetEvent _dataReady = new(false);
    private readonly AutoResetEvent _snapReady = new(false);
    private readonly object _snapLock = new();
    private float[]? _pendingSnapshot;
    private CancellationTokenSource? _cts;

    // Gate state (process thread only, except where volatile)
    private long _holdUntilMs;
    private bool _spkOk;
    private volatile float _lastSim = float.NaN;
    private long _lastSimMs = long.MinValue;
    private volatile bool _vadActive;

    // Live-adjustable settings
    private volatile GateMode _mode;
    private volatile float _acceptThreshold;
    private volatile bool _denoiseEnabled;
    private volatile int _holdMs;

    // Telemetry for the UI (volatile: written on audio threads, read on UI thread)
    private volatile float _inputLevel;
    private volatile float _outputLevel;
    private volatile bool _gateOpen;
    private volatile bool _isRunning;
    private volatile float _bufferedMs;

    public float InputLevel => _inputLevel;
    public float OutputLevel => _outputLevel;
    public float Similarity => _lastSim;
    public bool GateOpen => _gateOpen;
    public bool VadActive => _vadActive;
    public bool IsRunning => _isRunning;
    public float OutputBufferedMs => _bufferedMs;
    public string? ActualCaptureMode { get; private set; }
    public string? DenoiserName => _denoiser?.Name;

    /// <summary>Raised from audio threads; marshal to the UI thread before touching UI.</summary>
    public event Action<string>? ErrorOccurred;
    public event Action<string>? StatusMessage;

    public GateMode Mode { get => _mode; set => _mode = value; }
    public float AcceptThreshold { get => _acceptThreshold; set => _acceptThreshold = value; }
    public bool DenoiseEnabled { get => _denoiseEnabled; set => _denoiseEnabled = value; }
    public int HoldMs { get => _holdMs; set => _holdMs = value; }

    public float NoiseReductionDb
    {
        get => _denoiser?.ReductionDb ?? 18f;
        set
        {
            if (_denoiser != null)
                _denoiser.ReductionDb = value;
        }
    }

    public void SetReleaseMs(int releaseMs) => _smoother?.Configure(Rate, 4f, releaseMs);

    public void Start(EngineConfig cfg)
    {
        if (_isRunning)
            throw new InvalidOperationException("Engine already running.");

        var enumerator = new MMDeviceEnumerator();
        MMDevice mic = enumerator.GetDevice(cfg.MicDeviceId);
        MMDevice outDev = enumerator.GetDevice(cfg.OutputDeviceId);

        try
        {
            _mode = cfg.Mode;
            _acceptThreshold = cfg.AcceptThreshold;
            _denoiseEnabled = cfg.DenoiseEnabled;
            _holdMs = cfg.HoldMs;

            _capture = CreateCapture(mic, cfg.ExclusiveMic);
            int micRate = _capture.WaveFormat.SampleRate;
            _micResampler = micRate == Rate ? null : new StreamResampler(micRate, Rate);
            _convBuf = new float[micRate * 2];
            _resampBuf = new float[Rate * 2];
            _to16k = new StreamResampler(Rate, SileroVad.SampleRate);
            _buf16 = new float[_to16k.MaxOutputFor(FrameSize)];

            try
            {
                _denoiser = new RnNoiseDenoiser { ReductionDb = cfg.NoiseReductionDb };
            }
            catch (Exception)
            {
                // Native rnnoise.dll unavailable on this machine; use the managed fallback.
                _denoiser = new SpectralDenoiser { ReductionDb = cfg.NoiseReductionDb };
            }
            _highpass = BiQuadFilter.HighPassFilter(Rate, 80f, 0.707f);
            _delay = new DelayLine(cfg.LookaheadMs * Rate / 1000);
            _smoother = new GateSmoother(Rate, 4f, cfg.ReleaseMs);

            if (!File.Exists(cfg.VadModelPath))
                throw new FileNotFoundException("VAD model not found. Download models first.", cfg.VadModelPath);
            _vad = new SileroVad(cfg.VadModelPath);

            if (cfg.SpeakerModelPath != null && cfg.ProfileEmbedding is { Length: > 0 })
            {
                if (!File.Exists(cfg.SpeakerModelPath))
                    throw new FileNotFoundException("Speaker model not found. Download models first.", cfg.SpeakerModelPath);
                _verifier = new SpeakerVerifier(cfg.SpeakerModelPath);
                if (_verifier.Dim != cfg.ProfileEmbedding.Length)
                {
                    // Profile was enrolled with a different model: verifying against it
                    // would silently never match. Degrade to VAD-only and tell the user.
                    _verifier.Dispose();
                    _verifier = null;
                    StatusMessage?.Invoke(
                        "Your voiceprint does not match the installed speaker model - please re-enroll. " +
                        "Running with speech detection only.");
                }
                else
                {
                    _profile = cfg.ProfileEmbedding;
                }
            }

            var outFormat = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1);
            _outProvider = new BufferedWaveProvider(outFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(2),
            };
            _output = new WasapiOut(outDev, AudioClientShareMode.Shared, true, 20);
            _output.Init(_outProvider);
            _output.PlaybackStopped += OnPlaybackStopped;

            if (cfg.MonitorOutput)
            {
                MMDevice def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (def.ID != outDev.ID)
                {
                    _monitorProvider = new BufferedWaveProvider(outFormat)
                    {
                        DiscardOnBufferOverflow = true,
                        BufferDuration = TimeSpan.FromSeconds(2),
                    };
                    _monitor = new WasapiOut(def, AudioClientShareMode.Shared, true, 20);
                    _monitor.Init(_monitorProvider);
                }
            }

            ResetState();
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            _procThread = new Thread(() => ProcessLoop(token))
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest,
                Name = "VoiceGate.Process",
            };
            _embThread = new Thread(() => EmbeddingLoop(token))
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
                Name = "VoiceGate.Verify",
            };

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;

            _isRunning = true;
            _output.Play();
            _monitor?.Play();
            _capture.StartRecording();
            // Threads start last: everything above can throw (device in use, format
            // rejected, device unplugged), and a failed Start must never leave a live
            // pipeline behind. The 4 s capture ring absorbs the spin-up gap.
            _procThread.Start();
            _embThread.Start();
        }
        catch
        {
            AbortStart();
            throw;
        }
    }

    /// <summary>Rolls back a partially-started engine so a retry starts from a clean slate.</summary>
    private void AbortStart()
    {
        _isRunning = false;
        _cts?.Cancel();
        _dataReady.Set();
        _snapReady.Set();
        if (_procThread is { IsAlive: true })
            _procThread.Join(1000);
        if (_embThread is { IsAlive: true })
            _embThread.Join(1000);
        _procThread = null;
        _embThread = null;
        TearDown();
    }

    private WasapiCapture CreateCapture(MMDevice mic, bool exclusive)
    {
        if (exclusive)
        {
            WasapiCapture? capture = null;
            try
            {
                capture = new WasapiCapture(mic, true, 10) { ShareMode = AudioClientShareMode.Exclusive };
                WaveFormat[] candidates =
                [
                    new WaveFormat(48000, 16, 1),
                    new WaveFormat(48000, 16, 2),
                    new WaveFormat(44100, 16, 1),
                    new WaveFormat(44100, 16, 2),
                    new WaveFormat(48000, 24, 2),
                ];
                foreach (WaveFormat f in candidates)
                {
                    if (mic.AudioClient.IsFormatSupported(AudioClientShareMode.Exclusive, f))
                    {
                        capture.WaveFormat = f;
                        ActualCaptureMode = "exclusive";
                        StatusMessage?.Invoke("Microphone opened in exclusive mode - other apps cannot use the raw mic.");
                        return capture;
                    }
                }
            }
            catch
            {
                // fall through to shared
            }
            capture?.Dispose();
            StatusMessage?.Invoke("Exclusive mic mode not supported by this device - using shared mode.");
        }
        ActualCaptureMode = "shared";
        return new WasapiCapture(mic, true, 10);
    }

    private void ResetState()
    {
        _captureRing.Clear();
        Array.Clear(_spkWindow);
        _spkPos = 0;
        _spkTotal = 0;
        _speechRunStart = 0;
        _framesSinceSnapshot = 0;
        _prevVad = false;
        _holdUntilMs = 0;
        _spkOk = false;
        _lastSim = float.NaN;
        Volatile.Write(ref _lastSimMs, long.MinValue);
        _vadActive = false;
        _inputLevel = 0;
        _outputLevel = 0;
        _gateOpen = false;
        lock (_snapLock)
            _pendingSnapshot = null;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var capture = _capture;
        if (capture == null || !_isRunning)
            return;
        try
        {
            int frames = SampleConverter.ToMonoFloat(e.Buffer, e.BytesRecorded, capture.WaveFormat, _convBuf);
            ReadOnlySpan<float> mono = _convBuf.AsSpan(0, frames);
            if (_micResampler != null)
            {
                int n = _micResampler.Process(mono, _resampBuf);
                mono = _resampBuf.AsSpan(0, n);
            }
            _captureRing.Write(mono);
            _dataReady.Set();
        }
        catch (Exception ex)
        {
            RaiseError("Capture error: " + ex.Message);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null && _isRunning)
            RaiseError("Microphone stopped unexpectedly: " + e.Exception.Message);
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null && _isRunning)
            RaiseError("Output device stopped unexpectedly: " + e.Exception.Message);
    }

    private void ProcessLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            _dataReady.WaitOne(20);
            while (!token.IsCancellationRequested && _captureRing.Available >= FrameSize)
            {
                _captureRing.Read(_frame);
                try
                {
                    ProcessFrame(_frame);
                }
                catch (Exception ex)
                {
                    RaiseError("Processing error: " + ex.Message);
                    return;
                }
            }
        }
    }

    private void ProcessFrame(float[] frame)
    {
        // Input meter
        float peak = 0f;
        for (int i = 0; i < frame.Length; i++)
        {
            float a = Math.Abs(frame[i]);
            if (a > peak)
                peak = a;
        }
        _inputLevel = Math.Max(peak, _inputLevel * 0.92f);

        // High-pass rumble filter
        var hp = _highpass!;
        for (int i = 0; i < frame.Length; i++)
            frame[i] = hp.Transform(frame[i]);

        // Denoise runs every frame so the pipeline latency never jumps when the
        // user toggles the checkbox; Bypass makes it an identity pass-through.
        var denoiser = _denoiser!;
        denoiser.VoiceLikely = _vadActive;
        denoiser.Bypass = !_denoiseEnabled;
        denoiser.Process(frame);

        // 16 kHz side-chain for VAD + speaker verification
        int n16 = _to16k!.Process(frame, _buf16);
        if (n16 > 0)
        {
            _vad!.Feed(_buf16.AsSpan(0, n16));
            bool vadNow = _vad.IsSpeech;
            if (vadNow && !_prevVad)
                _speechRunStart = _spkTotal;
            _prevVad = vadNow;
            _vadActive = vadNow;

            for (int i = 0; i < n16; i++)
            {
                _spkWindow[_spkPos] = _buf16[i];
                _spkPos++;
                if (_spkPos == _spkWindow.Length)
                    _spkPos = 0;
            }
            _spkTotal += n16;
        }

        if (_verifier != null)
        {
            if (_vadActive)
            {
                _framesSinceSnapshot++;
                if (_framesSinceSnapshot >= SnapshotIntervalFrames)
                {
                    _framesSinceSnapshot = 0;
                    TakeSpeakerSnapshot();
                }
            }
            else
            {
                // Fire the first snapshot quickly once speech starts.
                _framesSinceSnapshot = SnapshotIntervalFrames - 10;
            }
        }

        float target = ComputeGateTarget();

        // Lookahead: delay the audio, gate with "future" decisions.
        _delay!.Process(frame);
        _smoother!.Process(frame, target);
        _gateOpen = target > 0.5f;

        float outPeak = 0f;
        for (int i = 0; i < frame.Length; i++)
        {
            float a = Math.Abs(frame[i]);
            if (a > outPeak)
                outPeak = a;
        }
        _outputLevel = Math.Max(outPeak, _outputLevel * 0.92f);

        MemoryMarshal.AsBytes(frame.AsSpan()).CopyTo(_frameBytes);
        var provider = _outProvider!;
        provider.AddSamples(_frameBytes, 0, _frameBytes.Length);
        double buffered = provider.BufferedDuration.TotalMilliseconds;
        _bufferedMs = (float)buffered;
        if (buffered > 250)
            provider.ClearBuffer(); // clock drift or a stall built up a backlog; reset latency

        if (_monitorProvider != null)
        {
            _monitorProvider.AddSamples(_frameBytes, 0, _frameBytes.Length);
            if (_monitorProvider.BufferedDuration.TotalMilliseconds > 250)
                _monitorProvider.ClearBuffer();
        }
    }

    private float ComputeGateTarget()
    {
        long now = Environment.TickCount64;
        bool speech = _vadActive;

        bool open;
        bool impostor = false;
        if (_verifier == null || _profile == null || _mode == GateMode.VadOnly)
        {
            open = speech;
        }
        else
        {
            float sim = _lastSim;
            long simMs = Volatile.Read(ref _lastSimMs);
            bool fresh = !float.IsNaN(sim) && now - simMs < SimFreshMs;
            float accept = _acceptThreshold;
            float reject = accept - RejectMargin;

            if (fresh)
            {
                if (sim >= accept)
                    _spkOk = true;
                else if (sim < reject)
                    _spkOk = false;
                // between reject and accept: hysteresis keeps the previous verdict
            }

            impostor = fresh && !_spkOk;
            open = _mode == GateMode.Strict
                ? speech && fresh && _spkOk
                : speech && (!fresh || _spkOk);
        }

        if (open)
            _holdUntilMs = now + _holdMs;
        bool held = now < _holdUntilMs && !impostor;
        return open || held ? 1f : 0f;
    }

    private void TakeSpeakerSnapshot()
    {
        // Window: from a little before this speech run started, up to 2 s, at least 0.6 s.
        double runSeconds = (_spkTotal - _speechRunStart) / (double)SileroVad.SampleRate;
        int winLen = (int)(Math.Clamp(runSeconds + 0.3, 0.6, 2.0) * SileroVad.SampleRate);
        winLen = (int)Math.Min(winLen, Math.Min(_spkTotal, _spkWindow.Length));
        if (winLen < SileroVad.SampleRate / 2)
            return;

        var snap = new float[winLen];
        int start = _spkPos - winLen;
        if (start < 0)
            start += _spkWindow.Length;
        for (int i = 0; i < winLen; i++)
        {
            snap[i] = _spkWindow[start];
            start++;
            if (start == _spkWindow.Length)
                start = 0;
        }
        lock (_snapLock)
            _pendingSnapshot = snap;
        _snapReady.Set();
    }

    private void EmbeddingLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            _snapReady.WaitOne(100);
            float[]? snap;
            lock (_snapLock)
            {
                snap = _pendingSnapshot;
                _pendingSnapshot = null;
            }
            var verifier = _verifier;
            var profile = _profile;
            if (snap == null || verifier == null || profile == null)
                continue;

            try
            {
                float[] emb = verifier.ComputeEmbedding(snap);
                if (emb.Length == 0)
                    continue;
                float sim = SpeakerVerifier.Cosine(emb, profile);
                if (float.IsNaN(sim))
                    continue;

                long now = Environment.TickCount64;
                float prev = _lastSim;
                long prevMs = Volatile.Read(ref _lastSimMs);
                if (!float.IsNaN(prev) && now - prevMs < 1000)
                    sim = 0.65f * sim + 0.35f * prev; // light smoothing across verdicts
                _lastSim = sim;
                Volatile.Write(ref _lastSimMs, now);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                RaiseError("Speaker verification error: " + ex.Message);
                return;
            }
        }
    }

    private void RaiseError(string message)
    {
        if (!_isRunning)
            return;
        ErrorOccurred?.Invoke(message);
    }

    public void Stop()
    {
        if (!_isRunning)
            return;
        _isRunning = false;

        _cts?.Cancel();
        _dataReady.Set();
        _snapReady.Set();

        try
        {
            _capture?.StopRecording();
        }
        catch { /* device may already be gone */ }
        bool procDone = _procThread == null || _procThread.Join(3000);
        bool embDone = _embThread == null || _embThread.Join(5000);
        _procThread = null;
        _embThread = null;

        // If a worker is wedged inside a native call, leak the native objects
        // rather than dispose them under a live thread (which would crash the process).
        TearDown(disposeNative: procDone && embDone);
    }

    private void TearDown(bool disposeNative = true)
    {
        if (_capture != null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            try
            {
                _capture.Dispose();
            }
            catch { }
            _capture = null;
        }
        if (_output != null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            try
            {
                _output.Stop();
            }
            catch { }
            try
            {
                _output.Dispose();
            }
            catch { }
            _output = null;
        }
        _outProvider = null;
        try
        {
            _monitor?.Stop();
        }
        catch { }
        try
        {
            _monitor?.Dispose();
        }
        catch { }
        _monitor = null;
        _monitorProvider = null;

        if (disposeNative)
        {
            _denoiser?.Dispose();
            _vad?.Dispose();
            _verifier?.Dispose();
        }
        _denoiser = null;
        _vad = null;
        _verifier = null;
        _profile = null;
        _cts?.Dispose();
        _cts = null;

        _inputLevel = 0;
        _outputLevel = 0;
        _gateOpen = false;
        _vadActive = false;
    }

    public void Dispose() => Stop();
}
