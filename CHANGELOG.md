# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres
to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- `VoiceGate.Core`, a new library holding the DSP, Silero VAD, speaker verification, and model
  management. It targets **.NET Standard 2.0** alongside .NET 9, so the voice-isolation core can be
  reused from .NET Framework 4.6.1+, Mono, Unity, Xamarin, and cross-platform .NET projects.

### Changed
- The WPF app now references `VoiceGate.Core` and keeps only the Windows-bound parts: the WASAPI
  audio engine, device enumeration and switching, settings, and the UI. Namespaces are unchanged.

## [1.0.0] - 2026-07-14

First public release.

### Added
- Real-time voice isolation pipeline: WASAPI capture → RNNoise → Silero VAD → CAM++ speaker
  verification → lookahead gate → virtual microphone.
- Voice enrollment wizard that builds a voiceprint, measures its consistency and suggests a matching
  gate threshold.
- Three gate modes: VAD only, Balanced, and Strict.
- Live panel: input/output meters, voice-match score against the threshold, gate and VAD indicators,
  and the output-buffer reading.
- One-click model download from the official sherpa-onnx releases (`setup-models.ps1` as a CLI
  alternative).
- Optional exclusive microphone capture, so no other app can read the raw signal.
- Optional monitoring of the processed output.
- "Make virtual mic the Windows default" via the `IPolicyConfig` COM interface.
- Managed spectral Wiener-filter denoiser as a fallback if the native RNNoise library can't load.
- Settings and voiceprint persisted in `%APPDATA%\VoiceGate`.

[1.0.0]: https://github.com/hnalpha323/VoiceGate/releases/tag/v1.0.0
