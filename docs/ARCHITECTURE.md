# VoiceGate architecture

This document explains how the app is put together, why the design choices were made, and where to
look when you want to change something. If you only want to *use* VoiceGate, the
[README](../README.md) is the right place.

## The problem

Conventional noise suppressors (RNNoise, Krisp, RTX Voice) are trained to separate **speech** from
**non-speech**. A second human voice is speech, so it passes straight through. To block other people
you need a different question answered: not *"is this speech?"* but *"is this **this specific
person's** speech?"*

That question is **speaker verification**, and it is expensive. A neural embedding model runs on
roughly a second of audio and produces a vector, so you cannot run it per 10 ms frame. The design
therefore splits into a **fast path** that must never stall (audio in, audio out) and a **slow path**
that answers the identity question asynchronously and updates a flag the fast path reads.

## Signal flow

```
                         ┌──────────────────────── fast path (per 10 ms frame) ─────────────────────────┐

 WASAPI capture   →  mono, 48 kHz  →  high-pass 80 Hz  →  denoiser  →  lookahead delay  →  gate  →  WasapiOut
 (any format)        SampleConverter   BiQuadFilter       IDenoiser     DelayLine       GateSmoother    (virtual cable)
                     StreamResampler                          │
                                                              ↓  resample to 16 kHz
                                                        ┌───────────────┐
                                                        │  Silero VAD   │ → is anyone speaking?  (per 512 samples)
                                                        └───────────────┘
                                                              │  rolling 0.6–2.0 s window, every ~250 ms of speech
                                                              ↓
                         ┌──────────────────── slow path (own thread) ─────────────────┐
                                                        ┌───────────────┐
                                                        │ CAM++ speaker │ → embedding vector
                                                        │   embedding   │ → cosine vs. enrolled voiceprint
                                                        └───────────────┘
                                                              │
                                                              └─→ _lastSim + timestamp  (read by the gate)
```

## Threads

| Thread | Owns | Must never |
|---|---|---|
| **WASAPI capture callback** (NAudio) | Format conversion, resample to 48 kHz, write into `FloatRingBuffer` | Block. It only converts and enqueues. |
| **Process thread** (`Highest` priority) | Denoise, VAD, gate, write to `BufferedWaveProvider` | Wait on the embedding model. |
| **Embedding thread** (`AboveNormal`) | Speaker embedding and cosine similarity | Touch the audio buffers directly (it gets an immutable snapshot). |
| **UI dispatcher** | Meters, settings, start/stop | Read engine state other than through `volatile` telemetry fields. |

Communication between them is deliberately minimal:

- **Capture to Process:** `FloatRingBuffer`, a lock-guarded FIFO that drops the oldest samples when
  full. A stalled consumer must never block the audio driver's callback.
- **Process to Embedding:** a single `float[]` snapshot under a lock, plus an `AutoResetEvent`. If the
  embedding thread is still busy, the newer snapshot replaces the pending one, since stale identity
  data is worse than no data.
- **Embedding to Process:** `_lastSim` (volatile float) and `_lastSimMs` (timestamp). The gate treats a
  verdict older than 1.5 s as "not fresh" and falls back to mode-specific behavior.
- **Engine to UI:** volatile telemetry fields polled by a 66 ms `DispatcherTimer`. No events on the hot
  path, no allocations, no locks.

Both worker threads receive their `CancellationToken` by value at construction, so a teardown that
nulls the engine's `CancellationTokenSource` can never null-deref them mid-flight.

## The gate

`AudioEngine.ComputeGateTarget()` is the decision, and it is deliberately small:

```
speech      = VAD says someone is talking
fresh       = we have a speaker verdict from the last 1.5 s
spkOk       = hysteresis latch:  sim >= threshold          -> true
                                 sim <  threshold - 0.08    -> false
                                 in between                 -> keep previous verdict

VadOnly   : open = speech
Balanced  : open = speech && (!fresh || spkOk)      // opens immediately, closes on rejection
Strict    : open = speech && fresh && spkOk         // never opens until verified
hold      : after any open frame, stay open for HoldMs unless an impostor is detected
```

Three properties fall out of this, and all three matter:

1. **Hysteresis** (`RejectMargin = 0.08`) stops the gate chattering when your similarity score
   oscillates around the threshold.
2. **Hold** (default 350 ms) bridges the natural pauses between words without reopening the gate for
   room noise. An impostor verdict cancels the hold immediately, so another speaker never rides your
   hold window.
3. **Lookahead** (`DelayLine`) delays the audio while the gate decisions are made on the undelayed
   signal. The gate therefore opens slightly before your first syllable reaches the output, instead of
   chopping it off. This is the only significant latency in the app, and it is a user setting.

The gain itself is applied by `GateSmoother` with an exponential attack (4 ms) and configurable
release, so the gate fades rather than clicks.

## Denoising

`IDenoiser` has two implementations:

- **`RnNoiseDenoiser`** (default) is the native RNNoise recurrent network at 48 kHz, 480-sample frames,
  with zero added latency. The strength slider becomes a wet/dry mix.
- **`SpectralDenoiser`** (fallback, fully self-contained) is a 1024-point STFT with 50% overlap,
  sqrt-Hann analysis/synthesis windows (COLA-compliant), and a decision-directed Wiener gain. Its noise
  estimate is VAD-informed: it only tracks upward while the VAD says nobody is speaking, so your voice
  never gets absorbed into the noise floor.

Both honor a **`Bypass`** flag that makes them an identity pass-through without changing latency, so
toggling noise reduction mid-call cannot glitch the stream. `SpectralDenoiserTests.Bypass_IsIdentity_AtFixedLatency`
proves the STFT reconstructs the input to within 1e-3, and that test caught a real off-by-512 latency
bug during development.

## Enrollment

`EnrollmentProcessor.Process()` does the following:

1. Runs the full recording through Silero VAD offline and keeps only the voiced regions (silence and
   breaths would poison the embedding).
2. Requires at least 6 s of voiced audio, and refuses politely if you mumbled.
3. Computes one embedding over all voiced audio; that is the voiceprint.
4. Computes embeddings over 4 equal chunks and measures each one's cosine similarity to the overall
   embedding. That mean, the **self-similarity**, is how internally consistent your voice was during
   enrollment.
5. Suggests a gate threshold of `selfSimilarity - 0.18`, clamped to [0.22, 0.55]. A consistent
   voiceprint earns a stricter threshold; a noisy one gets a lenient threshold and a warning.

The voiceprint is stored as `%APPDATA%\VoiceGate\profile.json` together with the model filename it was
made with. If the speaker model ever changes, `MainWindow` and `AudioEngine` both detect the mismatch
(by filename and by embedding dimension) and degrade to VAD-only with a visible message, rather than
silently never opening the gate.

## Latency budget

| Stage | Cost |
|---|---|
| WASAPI capture buffer | ~10 ms |
| RNNoise | 0 ms (spectral fallback: ~21 ms) |
| **Lookahead delay** | **0–300 ms (user setting, default 120 ms)** |
| WasapiOut buffer | ~20 ms |
| VB-Cable | ~1 ms |
| **Total (default)** | **~150 ms** |

Speaker verification adds no latency to the audio path. It runs on a separate thread and only updates a
flag, which is the point of the fast/slow split.

## Project layout

The code is split by portability. Everything that does not touch a Windows API lives in
**VoiceGate.Core**, which targets `netstandard2.0` as well as `net9.0` so it can be reused from
.NET Framework 4.6.1+, Mono, Unity and Xamarin. The WPF app holds the parts that are Windows-bound
by nature: WASAPI capture/render, COM device switching, and the UI.

```
src/VoiceGate.Core/             netstandard2.0 + net9.0 - no Windows APIs
├── Dsp/
│   ├── IDenoiser.cs            denoiser abstraction (Bypass = zero-latency identity)
│   ├── RnNoiseDenoiser.cs      native RNNoise
│   ├── SpectralDenoiser.cs     STFT Wiener denoiser (fallback, dependency-free)
│   ├── Fft.cs                  radix-2 complex FFT
│   ├── StreamResampler.cs      NAudio WDL resampler wrapper
│   ├── SampleConverter.cs      any WASAPI format -> mono float
│   ├── DelayLine.cs            lookahead
│   ├── GateSmoother.cs         click-free gain ramps
│   └── FloatRingBuffer.cs      lock-guarded FIFO, drops oldest on overflow
├── Speech/
│   ├── SileroVad.cs            streaming + offline VAD
│   ├── SpeakerVerifier.cs      embeddings + cosine similarity
│   ├── VoiceProfile.cs         the stored voiceprint
│   └── EnrollmentProcessor.cs  recording -> voiceprint + suggested threshold
├── Models/                     model registry (URLs, exact sizes) + downloader
└── Compat/IsExternalInit.cs    netstandard2.0 polyfill so records/init compile

src/VoiceGate/                  net9.0-windows WPF app
├── Audio/
│   ├── AudioEngine.cs          the real-time pipeline; start here
│   └── EnrollmentRecorder.cs   mic -> 16 kHz buffer for enrollment
├── Devices/                    endpoint enumeration, VB-Cable detection, default-device switching
├── Config/                     persisted settings
└── *.xaml(.cs)                 WPF UI: main window, enrollment wizard, download dialog
```

### Writing code that compiles for both targets

`VoiceGate.Core` shares one source set across `netstandard2.0` and `net9.0`, so it sticks to APIs
both have: `Math.Max` over `MathF.Max`, three-argument `Array.Clear`, `Span.Slice(n)` over the `[n..]`
range operator, plain `using` over `await using`, and the array-based `Stream.ReadAsync` overloads.
The net9.0 analyzers flag several of these and are suppressed in the project file, with the reason
recorded there. The alternative (an `#if` for every one) buys nothing here.

## Design decisions worth knowing

**Why a virtual cable instead of a real virtual audio driver?**
A proper virtual mic driver requires a signed kernel-mode (or APO) driver, which means a code-signing
certificate, WHQL, and a very different distribution story. VB-Cable is free, mature, widely used, and
installed once by the user. The application stays a normal user-mode app that anyone can build from
source.

**Why 48 kHz internally when the models want 16 kHz?**
Audio quality for the listener is decided at 48 kHz; the models only need a downsampled side-chain.
Doing it the other way round would ship 16 kHz audio to Discord and sound like a phone call.

**Why does the ring buffer drop samples instead of blocking?**
It is fed from a WASAPI callback. Blocking there causes driver-level glitches for the entire system,
which is far worse than dropping a few milliseconds when the machine is momentarily overloaded.

**Why is the exact model file size checked, not just its existence?**
A truncated `.onnx` (interrupted download) makes the native ONNX runtime fail with an unhelpful error
or hard-crash the process. Since the models are immutable GitHub release assets, the exact byte size is
a free integrity check that turns a cryptic crash into "please download the models".
