# Contributing to VoiceGate

Thanks for wanting to help. VoiceGate is a small, focused app, and contributions of every size are
welcome, from a one-line fix to a whole new denoiser backend.

## Ways to help that don't need code

- **Device compatibility reports.** Open an issue with your mic model, whether exclusive mode worked,
  and what the app reported. Windows audio drivers vary a lot between vendors, so real-world reports
  are genuinely useful.
- **Tuning feedback.** Which threshold worked for you? Did Strict mode clip your first word? This
  feedback shapes the defaults.
- **Documentation.** If something in the README confused you, that's a bug in the README.

## Getting set up

```powershell
git clone https://github.com/hnalpha323/VoiceGate.git
cd VoiceGate
dotnet build
dotnet test
```

You need the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0). Everything else comes from
NuGet, including the native binaries.

To run the pipeline end to end you also need [VB-Audio Virtual Cable](https://vb-audio.com/Cable/)
installed and the models downloaded (`./setup-models.ps1`, or the in-app **Download models** button).

Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) before touching `AudioEngine`. The fast-path and
slow-path split and the threading rules are not obvious from the code alone.

## Coding style

The repo ships an [`.editorconfig`](.editorconfig) and it is the authority. Before you push:

```powershell
dotnet format            # fixes formatting and style
dotnet build             # must be warning-free
dotnet test              # must be green
```

House rules beyond what the analyzer enforces:

- **Nothing allocates on the audio path.** No LINQ, no `new`, no boxing inside `ProcessFrame` or the
  code it calls. Pre-allocate in `Start()`.
- **Nothing blocks the capture callback.** It converts, resamples, and enqueues. That's it.
- **Comments explain why, not what.** If the code needs a comment to say what it does, rename things
  instead. Load-bearing constants such as `RejectMargin` and `SimFreshMs` do deserve a why.
- **Cross-thread state is `volatile` or lock-guarded, explicitly.** If you add a field that the audio
  thread and the UI thread both touch, say so at the declaration.

## Testing

DSP changes need a test. The suite in `tests/VoiceGate.Tests` is deliberately empirical rather than
snapshot-based; it asserts on measured behavior:

- an FFT round-trip is the identity;
- the resampler preserves a 1 kHz tone's frequency and produces the right sample count;
- the denoiser attenuates stationary noise by more than 8 dB;
- a bypassed denoiser reconstructs its input exactly, at a known fixed latency;
- the gate smoother opens within its attack window and closes within its release window.

Write tests in that spirit: if you claim a filter does something, measure it.

Anything that touches real audio devices can't be unit-tested in CI. Verify it by hand and say so in
the PR (for example, "tested with a Blue Yeti in exclusive mode at 44.1 kHz").

## Pull requests

1. Branch off `main`.
2. Keep the PR focused on one idea.
3. Explain the behavior change, not just the code change. If it affects audio, describe what you heard.
4. Make sure `dotnet build`, `dotnet test`, and `dotnet format --verify-no-changes` all pass. CI runs
   exactly those.

## Good first issues

- **Push-to-talk / hotkey** to force the gate open or closed regardless of verification.
- **System tray icon** with start/stop, so the window can be closed without stopping the pipeline.
- **Multiple voiceprints**, to let a couple or a co-streamer both pass.
- **Model picker** in the UI. sherpa-onnx ships several speaker models with different size and accuracy
  trade-offs, and `ModelRegistry` is already structured for it.
- **GTCRN denoiser backend.** sherpa-onnx ships a speech-enhancement model that would slot into
  `IDenoiser`.
- **Auto-threshold calibration:** record 10 s of someone else and compute the optimal threshold between
  the two distributions instead of estimating from self-similarity.
- **Waveform or spectrogram view** so users can see what got gated.

## Scope

VoiceGate deliberately does not try to be:

- a general audio-routing tool (use VoiceMeeter);
- a voice changer;
- a cloud service. It runs locally, always. A PR that sends audio anywhere will be closed.

## License

By contributing you agree that your work is licensed under the [MIT License](LICENSE) of this project.
