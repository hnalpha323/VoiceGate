# Builds a ready-to-run VoiceGate into .\dist
param(
    [switch]$SelfContained  # include the .NET runtime (bigger, runs on machines without .NET 9)
)
$ErrorActionPreference = "Stop"

$args = @(
    "publish", "src\VoiceGate\VoiceGate.csproj",
    "-c", "Release",
    "-r", "win-x64",
    "-o", "dist"
)
if ($SelfContained) { $args += "--self-contained" } else { $args += "--no-self-contained" }

& dotnet @args
Write-Host "`nDone. Run dist\VoiceGate.exe" -ForegroundColor Green
