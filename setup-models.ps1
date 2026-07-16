# Downloads the AI models VoiceGate needs (alternative to the in-app download button).
# Files come from the official sherpa-onnx GitHub releases: https://github.com/k2-fsa/sherpa-onnx
$ErrorActionPreference = "Stop"

$modelsDir = Join-Path $env:APPDATA "VoiceGate\models"
New-Item -ItemType Directory -Force -Path $modelsDir | Out-Null

# ExpectedSize must match ModelRegistry.cs exactly - VoiceGate validates it.
$models = @(
    @{
        Name         = "silero_vad.onnx"
        Url          = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx"
        ExpectedSize = 643854
    },
    @{
        Name         = "3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx"
        Url          = "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx"
        ExpectedSize = 28281164
    }
)

foreach ($m in $models) {
    $target = Join-Path $modelsDir $m.Name
    if ((Test-Path $target) -and (Get-Item $target).Length -eq $m.ExpectedSize) {
        Write-Host "Already present: $($m.Name)" -ForegroundColor Green
        continue
    }
    Write-Host "Downloading $($m.Name) ..." -ForegroundColor Cyan
    $temp = "$target.download"
    try {
        Invoke-WebRequest -Uri $m.Url -OutFile $temp -UseBasicParsing
        $size = (Get-Item $temp).Length
        if ($size -ne $m.ExpectedSize) {
            throw "Downloaded size $size does not match expected $($m.ExpectedSize) - truncated download?"
        }
        Move-Item -Force $temp $target
        Write-Host "Saved to $target" -ForegroundColor Green
    }
    finally {
        if (Test-Path $temp) { Remove-Item -Force $temp -Confirm:$false }
    }
}

Write-Host "`nAll models ready in $modelsDir" -ForegroundColor Green
