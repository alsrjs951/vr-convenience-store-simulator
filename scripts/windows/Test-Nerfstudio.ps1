<#
.SYNOPSIS
    P0 GPU smoke test: official poster data, both training methods, and PLY export.
#>
[CmdletBinding()]
param(
    [ValidateRange(10, 10000)][int]$MaxIterations = 150,
    [string]$WorkDir = "$env:USERPROFILE\nerfstudio-work"
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Enter-Nerfstudio.ps1" -WorkDir $WorkDir

function Invoke-Checked {
    param([string]$Executable, [string[]]$Arguments)
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE" }
}

$runId = Get-Date -Format 'yyyy-MM-dd_HHmmss'
$evidence = Join-Path $WorkDir "verification\$runId"
New-Item -ItemType Directory -Force $evidence | Out-Null
Start-Transcript -Path (Join-Path $evidence 'transcript.txt') | Out-Null
Push-Location $WorkDir
try {
    Invoke-Checked 'python' @('-c', 'import torch, tinycudann; print("Torch", torch.__version__, "CUDA", torch.version.cuda); assert torch.cuda.is_available(); print(torch.cuda.get_device_name(0)); print(torch.ones(1, device="cuda") + 1)')
    Invoke-Checked 'colmap' @('-h')
    Invoke-Checked 'ffmpeg' @('-version')
    $data = Join-Path $WorkDir 'data\nerfstudio\poster'
    if (Test-Path (Join-Path $WorkDir 'data\nerfstudio\.poster-download-incomplete')) {
        Invoke-Checked 'python' @("$PSScriptRoot\download_poster.py", (Join-Path $WorkDir 'data\nerfstudio'))
    }
    elseif (-not (Test-Path (Join-Path $data 'transforms.json'))) {
        & ns-download-data nerfstudio --capture-name poster
        if ($LASTEXITCODE -ne 0) {
            Write-Host 'Drive download unavailable; using the official Nerfstudio Hugging Face mirror.'
            Invoke-Checked 'python' @("$PSScriptRoot\download_poster.py", (Join-Path $WorkDir 'data\nerfstudio'))
        }
    }
    foreach ($method in @('nerfacto', 'splatfacto')) {
        # No interactive viewer: a successful run must actually exit.
        Invoke-Checked 'ns-train' @($method, '--data', $data,
            '--output-dir', (Join-Path $WorkDir 'outputs'),
            '--experiment-name', 'p0-smoke', '--timestamp', $runId,
            '--max-num-iterations', "$MaxIterations", '--vis', 'tensorboard')
    }
    $config = Join-Path $WorkDir "outputs\p0-smoke\splatfacto\$runId\config.yml"
    if (-not (Test-Path $config)) { throw "Missing splatfacto config: $config" }
    $export = Join-Path $WorkDir "exports\$runId"
    Invoke-Checked 'ns-export' @('gaussian-splat', '--load-config', $config, '--output-dir', $export)
    $ply = Join-Path $export 'splat.ply'
    Invoke-Checked 'python' @('-c', 'from plyfile import PlyData; import sys; p=PlyData.read(sys.argv[1]); n=len(p["vertex"]); assert n>0; print("PLY vertices:",n)', $ply)
    $result = [ordered]@{
        result = 'PASS'; runId = $runId; iterations = $MaxIterations
        methods = @('nerfacto', 'splatfacto'); config = $config; ply = $ply
    }
    $result | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $evidence 'result.json')
    Write-Host "P0 learning smoke test PASS: $evidence"
}
finally {
    Pop-Location
    Stop-Transcript | Out-Null
}
