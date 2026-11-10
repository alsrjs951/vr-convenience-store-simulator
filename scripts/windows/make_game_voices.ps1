# Speaks the game's lines with the Windows Korean TTS voice (Microsoft Heami, built into Korean Windows). A stand-in until
# the lines are voiced properly: the words are in Data/voice_lines.json, which is what any voice generator reads.
# Output: one 22.05 kHz mono WAV per line in $OutDir. make_game_sounds.py then trims, normalizes and makes male versions.
# Usage: powershell -File scripts/windows/make_game_voices.ps1 -OutDir <tts_raw_dir>
param([Parameter(Mandatory = $true)][string]$OutDir)

Add-Type -AssemblyName System.Speech
New-Item -ItemType Directory -Force $OutDir | Out-Null
$script = Join-Path $PSScriptRoot '..\..\Data\voice_lines.json'
$lines = (Get-Content -Raw -Encoding UTF8 $script | ConvertFrom-Json).lines
if (-not $lines -or $lines.Count -eq 0) { throw "No lines in $script" }

$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$synth.SelectVoice("Microsoft Heami Desktop")
$synth.Rate = 0
$format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(22050, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)

foreach ($line in $lines) {
    $synth.SetOutputToWaveFile((Join-Path $OutDir "$($line.id).wav"), $format)
    $synth.Speak($line.text)
}
$synth.SetOutputToNull()
$synth.Dispose()
"wrote $($lines.Count) lines to $OutDir"
