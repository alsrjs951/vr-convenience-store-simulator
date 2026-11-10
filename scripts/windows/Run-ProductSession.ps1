<#
.SYNOPSIS
    Reconstructs one product video: frames -> COLMAP -> nerfstudio dataset -> splatfacto -> eval -> PLY.
.DESCRIPTION
    3 fps, single SIMPLE_RADIAL camera, sequential overlap 12, 10,000 iterations, like the dakdari V01
    session except for training resolution. Reads the video only. Finished steps are skipped, so a rerun resumes.
    Run from the project root, because nerfstudio stores relative paths in its config.
.EXAMPLE
    .\scripts\windows\Run-ProductSession.ps1 -Video <mp4> -Session capture\products\capture_02\session_a -Experiment goraebap-a
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Video,
    [Parameter(Mandatory)][string]$Session,
    [Parameter(Mandatory)][string]$Experiment,
    [double]$Fps = 3,
    [double]$EndSeconds = 0,
    [int]$Iterations = 10000,
    [int]$Overlap = 12,
    # 1 trains at full resolution, the project standard since 2026-11-07. 0 keeps nerfstudio's automatic
    # choice, which trains 1080p video at 960x540: about half the time, but small print blurs.
    [int]$DownscaleFactor = 1,
    # Keep the sharpest frame of each 1/Fps seconds instead of the frame at each clock tick.
    [switch]$SharpFrames,
    # exhaustive also matches the far-apart views of an orbit that passes the same side twice.
    [ValidateSet('sequential', 'exhaustive')][string]$Matcher = 'sequential',
    # Names a training variant: its run and export folders get this suffix, and the frames and poses are shared.
    [string]$Variant = '',
    # Extra ns-train options, for example '--pipeline.model.use-bilateral-grid True'.
    [string]$TrainArgs = '',
    # Also save ground truth next to each rendered evaluation view (scripts\windows\compare_training_variants.py reads them).
    [switch]$SaveEvalImages,
    # Stops after the frames, poses and dataset, which do not depend on the training settings.
    [switch]$PrepareOnly
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Video)) { throw "Video not found: $Video" }
. "$PSScriptRoot\Enter-Nerfstudio.ps1"

New-Item -ItemType Directory -Force -Path $Session | Out-Null
$frames = Join-Path $Session 'frames'
$database = Join-Path $Session 'database.db'
$sparse = Join-Path $Session 'sparse'
$dataset = Join-Path $Session 'nerfstudio'
$outputs = Join-Path $Session 'outputs'
$timestamp = if ($DownscaleFactor -gt 0) { "trial-$Iterations-ds$DownscaleFactor" } else { "trial-$Iterations" }
if ($Variant) { $timestamp = "$timestamp-$Variant" }
$dataparser = if ($DownscaleFactor -gt 0) { "nerfstudio-data --downscale-factor $DownscaleFactor" } else { 'nerfstudio-data' }
$config = Join-Path $outputs "$Experiment\splatfacto\$timestamp\config.yml"
$export = Join-Path $Session 'export'

function Invoke-Step([string]$Name, [string]$Done, [string]$Command) {
    if ($Done -and (Test-Path -LiteralPath $Done)) { Write-Host "skip  $Name"; return }
    $log = Join-Path $Session "$Name.log"
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Write-Host "start $Name"
    & $env:ComSpec /d /c "$Command > `"$log`" 2>&1"
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE. See $log" }
    if ($Done -and -not (Test-Path -LiteralPath $Done)) { throw "$Name finished without producing $Done. See $log" }
    Write-Host ("done  {0} ({1:F1} min)" -f $Name, $watch.Elapsed.TotalMinutes)
}

New-Item -ItemType Directory -Force -Path $frames | Out-Null
$trim = if ($EndSeconds -gt 0) { "-t $EndSeconds" } else { '' }
$frameCommand = if ($SharpFrames) { "python scripts\windows\select_sharp_frames.py `"$Video`" `"$frames`" --fps $Fps" } else { "ffmpeg -hide_banner -i `"$Video`" $trim -vf fps=$Fps -q:v 2 `"$frames\frame_%04d.jpg`"" }
Invoke-Step 'frames' (Join-Path $frames 'frame_0001.jpg') $frameCommand
Invoke-Step 'features' $database "colmap feature_extractor --database_path `"$database`" --image_path `"$frames`" --ImageReader.camera_model SIMPLE_RADIAL --ImageReader.single_camera 1"
$matched = Join-Path $Session 'matching.log'
$matchCommand = if ($Matcher -eq 'exhaustive') { "colmap exhaustive_matcher --database_path `"$database`"" } else { "colmap sequential_matcher --database_path `"$database`" --SequentialMatching.overlap $Overlap" }
Invoke-Step 'matching' $matched $matchCommand
New-Item -ItemType Directory -Force -Path $sparse | Out-Null
Invoke-Step 'mapping' (Join-Path $sparse '0\images.bin') "colmap mapper --database_path `"$database`" --image_path `"$frames`" --output_path `"$sparse`""
Invoke-Step 'audit' (Join-Path $Session 'reconstruction_summary.json') "python scripts\windows\audit_product_reconstruction.py `"$Session`""
# The mapper can leave a stray two-image model in sparse\0 and the real one in sparse\1, so take the largest.
$models = Get-Content -LiteralPath (Join-Path $Session 'reconstruction_summary.json') -Raw | ConvertFrom-Json
$best = @($models | ForEach-Object { $_ }) | Sort-Object registered -Descending | Select-Object -First 1
Write-Host ("model {0}: {1}/{2} frames registered" -f $best.model, $best.registered, $best.input_frames)
if ($best.registration_fraction -lt 0.8) { throw "Only $($best.registered)/$($best.input_frames) frames registered in $Session" }
Invoke-Step 'processing' (Join-Path $dataset 'transforms.json') "ns-process-data images --data `"$frames`" --output-dir `"$dataset`" --skip-colmap --colmap-model-path ..\sparse\$($best.model) --num-downscales 2"
if ($PrepareOnly) { Write-Host "PASS prepared: $Session"; return }
# ns-train writes config.yml at startup, so only the last checkpoint proves that training finished.
$finalCheckpoint = Join-Path (Split-Path $config) ("nerfstudio_models\step-{0:D9}.ckpt" -f ($Iterations - 1))
Invoke-Step "training-$timestamp" $finalCheckpoint "ns-train splatfacto --data `"$dataset`" --output-dir `"$outputs`" --experiment-name $Experiment --timestamp $timestamp --max-num-iterations $Iterations --vis tensorboard $TrainArgs $dataparser"
$suffix = if ($DownscaleFactor -gt 0) { "-ds$DownscaleFactor" } else { '' }
if ($Variant) { $suffix = "$suffix-$Variant" }
$export = "$export$suffix"
$evalImages = ''
if ($SaveEvalImages) {
    $evalFolder = Join-Path $Session "eval-images$suffix"
    New-Item -ItemType Directory -Force -Path $evalFolder | Out-Null
    $evalImages = "--render-output-path `"$evalFolder`""
}
Invoke-Step "eval$suffix" (Join-Path $Session "eval$suffix.json") "ns-eval --load-config `"$config`" --output-path `"$Session\eval$suffix.json`" $evalImages"
Invoke-Step "export$suffix" (Join-Path $export 'splat.ply') "ns-export gaussian-splat --load-config `"$config`" --output-dir `"$export`""
Write-Host "PASS session finished: $Session"
Write-Host "config: $config"
