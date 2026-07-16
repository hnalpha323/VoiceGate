# Security policy

## Reporting a vulnerability

Please report security issues privately through
[GitHub's private vulnerability reporting](https://github.com/hnalpha323/VoiceGate/security/advisories/new)
rather than opening a public issue. You should get a first response within a week.

## Threat model

VoiceGate is a local desktop app. It has a deliberately small attack surface:

- **No network access at runtime**, except a one-time model download over HTTPS from
  `github.com/k2-fsa/sherpa-onnx` release assets. Downloaded files are size-validated against the
  values pinned in `ModelRegistry.cs`.
- **No audio ever leaves the machine.** There is no telemetry, no account, and no cloud component.
- **No elevation.** VoiceGate runs as a normal user. (VB-Audio Virtual Cable, a separate third-party
  driver, does need administrator rights to *install*, and that installer is not part of this project.)
- **What's stored on disk**: `%APPDATA%\VoiceGate\profile.json` (a compact voiceprint vector; speech
  cannot be reconstructed from it), `settings.json`, and the downloaded `.onnx` models.

Things worth reporting: a path that could execute a downloaded file, a way to make the app write
outside `%APPDATA%\VoiceGate`, a way for another process to obtain the raw microphone stream through
VoiceGate, or a dependency with a known CVE.
