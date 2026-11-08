<#
.SYNOPSIS
    Dot-source this file to activate the local P0 learning toolchain.
.EXAMPLE
    . .\scripts\windows\Enter-Nerfstudio.ps1
    ns-train --help
#>
[CmdletBinding()]
param(
    [string]$CondaRoot = "$env:USERPROFILE\miniforge3",
    [string]$EnvName = 'nerfstudio',
    [string]$WorkDir = "$env:USERPROFILE\nerfstudio-work"
)

$condaExe = Join-Path $CondaRoot 'Scripts\conda.exe'
$vcvars = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat'
if (-not (Test-Path -LiteralPath $condaExe)) { throw "Conda not found: $condaExe" }
if (-not (Test-Path -LiteralPath $vcvars)) { throw "VS 2022 C++ Build Tools not found: $vcvars" }

# Import the compiler environment only into this PowerShell process.
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$env:PYTHONUTF8 = '1'
$env:PYTHONIOENCODING = 'utf-8'
$compilerEnvironment = & $env:ComSpec /d /c ('chcp 65001 >nul && call "{0}" x64 -vcvars_ver=14.29 >nul && set' -f $vcvars)
if ($LASTEXITCODE -ne 0) { throw 'MSVC 14.29 initialization failed.' }
foreach ($line in $compilerEnvironment) {
    if ($line -match '^([^=]+)=(.*)$') {
        [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process')
    }
}
(& $condaExe shell.powershell hook) | Out-String | Invoke-Expression
conda activate $EnvName
if ($env:CONDA_DEFAULT_ENV -ne $EnvName) { throw "Could not activate $EnvName" }

$env:CUDA_PATH = $env:CONDA_PREFIX
$env:CUDA_HOME = $env:CONDA_PREFIX
$env:Path = "$(Join-Path $env:CONDA_PREFIX 'bin');$env:Path"

$env:TCNN_CUDA_ARCHITECTURES = '89'
$env:TORCH_CUDA_ARCH_LIST = '8.9'
$env:MAX_JOBS = '4'
$env:DISTUTILS_USE_SDK = '1'
# vcvarsall above already selected the CUDA-compatible compiler. Prevent nvcc
# from reinitializing the newest installed MSVC during gsplat's JIT build.
$env:NVCC_PREPEND_FLAGS = '--use-local-env'
$colmapBin = Join-Path $WorkDir 'tools\colmap\bin'
if (Test-Path -LiteralPath $colmapBin) { $env:Path = "$colmapBin;$env:Path" }
$env:QT_PLUGIN_PATH = Join-Path $WorkDir 'tools\colmap\plugins'
Write-Host "Activated $EnvName with MSVC 14.29 (process-local)."
