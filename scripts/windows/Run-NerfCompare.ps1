<#
.SYNOPSIS
    NeRF (nerfacto) vs 3DGS (splatfacto) on the same photos, poses and test split (implementation doc 9.3, P4-K2).
.DESCRIPTION
    Trains both methods on each session's existing nerfstudio dataset at full resolution, then runs ns-eval, which
    gives PSNR, SSIM, LPIPS and render speed. Camera optimization is off for nerfacto so both use the same COLMAP poses
    (splatfacto does not optimize them by default). Time per step and model size go to <OutRoot>\results.json.
    Finished steps are skipped, so a rerun resumes.
.EXAMPLE
    .\scripts\windows\Run-NerfCompare.ps1 -CaptureRoot C:\Projects\vr-convenience-store-simulator\capture\products
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CaptureRoot,
    [string]$OutRoot = 'capture\compare',
    [int]$SplatIterations = 10000,
    [int]$NerfIterations = 30000
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Enter-Nerfstudio.ps1"

# One matte paper box and one glossy film bag: the two material groups the store has (doc 9.3 asks for matte and reflective).
$sessions = @(
    @{ Name = 'goraebap-matte'; Data = Join-Path $CaptureRoot 'capture_02\session_a\nerfstudio' },
    @{ Name = 'cheetos-glossy'; Data = Join-Path $CaptureRoot 'capture_09\session_front\nerfstudio' }
)
$methods = @(
    @{ Name = 'splatfacto'; Iterations = $SplatIterations; Extra = '' },
    @{ Name = 'nerfacto'; Iterations = $NerfIterations; Extra = '--pipeline.model.camera-optimizer.mode off' }
)

New-Item -ItemType Directory -Force -Path $OutRoot | Out-Null
$resultsPath = Join-Path $OutRoot 'results.json'
$results = @{}
if (Test-Path -LiteralPath $resultsPath) {
    (Get-Content -Raw -LiteralPath $resultsPath | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $results[$_.Name] = $_.Value }
}

function Save-Results { $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $resultsPath -Encoding UTF8 }

function Invoke-Step([string]$Log, [string]$Command) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Write-Host "start $Log"
    & $env:ComSpec /d /c "$Command > `"$Log`" 2>&1"
    if ($LASTEXITCODE -ne 0) { throw "Failed with exit code $LASTEXITCODE. See $Log" }
    Write-Host ("done  {0} ({1:F1} min)" -f $Log, $watch.Elapsed.TotalMinutes)
    return [math]::Round($watch.Elapsed.TotalSeconds, 1)
}

foreach ($session in $sessions) {
    if (-not (Test-Path -LiteralPath (Join-Path $session.Data 'transforms.json'))) { throw "No dataset at $($session.Data)" }
    $root = Join-Path $OutRoot $session.Name
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    foreach ($method in $methods) {
        $key = "$($session.Name)/$($method.Name)"
        $run = Join-Path $root "$($session.Name)\$($method.Name)\cmp"
        $config = Join-Path $run 'config.yml'
        $eval = Join-Path $root "$($method.Name)-eval.json"
        $entry = if ($results.ContainsKey($key)) { $results[$key] } else { [pscustomobject]@{ iterations = $method.Iterations } }

        if (-not $entry.PSObject.Properties['train_seconds']) {
            $seconds = Invoke-Step (Join-Path $root "$($method.Name)-train.log") "ns-train $($method.Name) --data `"$($session.Data)`" --output-dir `"$root`" --experiment-name $($session.Name) --timestamp cmp --max-num-iterations $($method.Iterations) --vis tensorboard $($method.Extra) nerfstudio-data --downscale-factor 1"
            $entry | Add-Member -NotePropertyName train_seconds -NotePropertyValue $seconds -Force
            $results[$key] = $entry
            Save-Results
        }
        if (-not (Test-Path -LiteralPath $eval)) {
            # Without --render-output-path: saving the pictures happens inside ns-eval's timer and would hide the render speed.
            $seconds = Invoke-Step (Join-Path $root "$($method.Name)-eval.log") "ns-eval --load-config `"$config`" --output-path `"$eval`""
            $entry | Add-Member -NotePropertyName eval_seconds -NotePropertyValue $seconds -Force
        }
        $renders = Join-Path $root "$($method.Name)-renders"
        if (-not (Test-Path -LiteralPath $renders)) {
            Invoke-Step (Join-Path $root "$($method.Name)-renders.log") "ns-eval --load-config `"$config`" --output-path `"$(Join-Path $root "$($method.Name)-renders.json")`" --render-output-path `"$renders`"" | Out-Null
        }
        $checkpoint = Get-ChildItem -LiteralPath (Join-Path $run 'nerfstudio_models') -Filter '*.ckpt' | Sort-Object Length -Descending | Select-Object -First 1
        $entry | Add-Member -NotePropertyName checkpoint_mb -NotePropertyValue ([math]::Round($checkpoint.Length / 1MB, 1)) -Force
        $results[$key] = $entry
        Save-Results
    }
}
Write-Host "PASS comparison finished: $resultsPath"
