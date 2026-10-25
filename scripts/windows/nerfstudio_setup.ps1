<#
.SYNOPSIS
    Windows RTX 4070 데스크톱용 nerfstudio 학습 환경 셋업 + 검증 스크립트.

.DESCRIPTION
    마일스톤 P0-L4(RTX 4070 원격 학습 환경 구축, D3)에 대응하며,
    P0 종료 기준 3번 — "RTX 4070 데스크톱에서 ns-train splatfacto 및 ns-train nerfacto
    CLI가 에러 없이 구동됨" — 을 샘플 데이터 단기 학습으로 자동 검증한다.

    수행 순서:
      1. 사전 설치물 존재 확인(nvidia-smi, git, conda)
      2. conda 환경 생성(python 3.10) → PyTorch cu118 → cuda-toolkit 11.8 → tiny-cuda-nn → nerfstudio
      3. COLMAP / FFmpeg 확인(없으면 설치 시도 또는 안내 출력)
      4. 검증: ns-train --help 확인
      5. 검증: nerfstudio 공식 샘플 데이터(poster) 내려받아
         nerfacto / splatfacto 각각 --max-num-iterations 150 단기 학습(에러 없이 종료하면 PASS)

.NOTES
    - 검증 기준일: 2026-10-25
      * nerfstudio PyPI 최신: 1.1.5 (pypi.org/pypi/nerfstudio/json 조회 확인)
      * 설치 절차는 nerfstudio 공식 문서(docs/quickstart/installation.md, main 브랜치)를 따른다:
        torch 2.1.2+cu118 권장, cuda-toolkit 11.8은 conda 채널로 설치, tiny-cuda-nn은 git 설치.
      * 샘플 데이터·학습 명령은 공식 문서(docs/quickstart/first_nerf.md) 기준:
        ns-download-data nerfstudio --capture-name=poster / ns-train nerfacto --data data/nerfstudio/poster
    - conda python 버전: 공식 문서 예시는 python=3.8이지만, nerfstudio PyPI 요구는 >=3.8이므로
      본 스크립트는 요청 사양에 따라 3.10을 사용한다(3.10~3.11 모두 충족).
    - nerfstudio 공식 문서는 Windows를 "less tested and more fragile"라고 명시한다.
      문제 지속 시 Linux/WSL2 전환을 검토한다(문서 동일 권고).

.PREREQUISITES (사전 요구 사항 — 스크립트 실행 전 수동 설치)
    1. NVIDIA 드라이버: RTX 4070용 최신 드라이버 설치. `nvidia-smi`가 동작하고
       표시되는 "CUDA Version"이 11.8 이상이면 충분(이 값은 드라이버가 지원하는
       최대 CUDA 런타임을 뜻함 — 낮으면 드라이버를 먼저 업데이트).
    2. Visual Studio 2022 (또는 Build Tools): "Desktop Development with C++" 워크로드 필수.
       ※ 공식 문서 요구: CUDA 관련 설치 전에 먼저 설치할 것.
       ※ MSVC v143이 실패하면 MSVC v142(14.29) 추가 설치가 필요할 수 있음(문서 명시).
       ※ tiny-cuda-nn/gsplat CUDA 컴파일이 필요하므로, 아래 vcvars64 활성화 안내를 따를 것:
          "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
          - PowerShell에서 실행하려면 먼저 cmd에서 활성화하거나,
            "x64 Native Tools Command Prompt for VS 2022"를 열어 그 안에서 PowerShell을 실행한다.
    3. CUDA Toolkit: 별도 설치 불요 — cuda-toolkit 11.8을 conda 채널(nvidia/label/cuda-11.8.0)로
      환경 안에 설치한다(공식 문서 권장 방식). 시스템 전역 CUDA 설치는 충돌 원인이 될 수 있으므로
      다른 버전을 미리 설치해 둔 경우 버전 충돌 항목은 docs/p0/RTX4070_학습환경_세팅_가이드.md 참조.
    4. Git: https://git-scm.com/downloads (tiny-cuda-nn git 설치에 필요)
    5. Miniconda: https://docs.conda.io/en/latest/miniconda.html
       설치 후 PowerShell에서 `conda init powershell`을 1회 실행하고 터미널을 다시 열 것.
    6. COLMAP(선택 — 실제 영상·사진 처리 시 필요): 공식 winget 패키지가 확인되지 않으므로(2026-10-25 기준)
       본 스크립트는 winget 검색을 시도하고, 실패 시 공식 릴리스 zip 수동 설치를 안내한다.
    7. FFmpeg(선택 — ns-process-data video에 필요): winget Gyan.FFmpeg 설치를 시도한다.

.EXAMPLE
    # 사전 요구 사항 설치 후, PowerShell에서:
    Set-ExecutionPolicy -Scope Process Bypass -Force
    .\nerfstudio_setup.ps1
    # 단기 학습 반복 수 변경(기본 150, 요청 범위 100~200):
    .\nerfstudio_setup.ps1 -MaxIterations 200
    # 의존성 설치 생략(이미 설치된 상태에서 검증만 재실행):
    .\nerfstudio_setup.ps1 -VerifyOnly
#>

[CmdletBinding()]
param(
    # 단기 학습 반복 수(P0 종료 기준 검증용). 요청 범위: 100~200. 기본 150.
    [int]$MaxIterations = 150,
    # 스크립트 작업 디렉터리(샘플 데이터·outputs가 생기는 곳).
    [string]$WorkDir = "$HOME\nerfstudio-work",
    # conda 환경 이름.
    [string]$EnvName = "nerfstudio",
    # conda python 버전(3.10 또는 3.11 권장).
    [string]$PythonVersion = "3.10",
    # 의존성 설치를 건너뛰고 검증(4~5단계)만 실행.
    [switch]$VerifyOnly
)

$ErrorActionPreference = "Stop"
# 주의(2026-10-25 기록 — Windows 실기 실행 미검증): Windows PowerShell 5.1에서는 EAP=Stop 상태에서
# 네이티브 명령의 stderr 리다이렉트(2>$null, *>$null — 본 스크립트의 nvidia-smi/pip/winget/colmap/
# ns-train 리다이렉트 지점 전체)가 NativeCommandError 예외로 이어져 스크립트가 중단될 수 있다
# (특히 pip처럼 stderr에 경고를 자주 쓰는 명령). 실기 첫 실행 시 이 지점에서 중단되면, 해당
# 리다이렉트 구간에서만 $ErrorActionPreference를 'Continue'로 바꿨다 복원하거나 PowerShell 7(pwsh)로
# 실행한다. docs/p0/RTX4070_학습환경_세팅_가이드.md §4 참조.
Set-StrictMode -Version Latest

# ---------------------------------------------------------------- helpers
function Write-Step([string]$Msg)  { Write-Host "`n==== $Msg ====" -ForegroundColor Cyan }
function Write-Ok([string]$Msg)    { Write-Host "  [OK]   $Msg" -ForegroundColor Green }
function Write-Warn2([string]$Msg) { Write-Host "  [WARN] $Msg" -ForegroundColor Yellow }
function Write-Fail([string]$Msg)  { Write-Host "  [FAIL] $Msg" -ForegroundColor Red }
function Test-Cmd([string]$Name)   { return [bool](Get-Command $Name -ErrorAction SilentlyContinue) }

# 설치 단계 전체의 성공/실패 추적(검증은 별도 진행)
$script:P0ExitCriterion3Pass = $false

# ---------------------------------------------------------------- 1. pre-flight
Write-Step "1/5 사전 요구 사항 확인"

$missing = @()

if (Test-Cmd "nvidia-smi") {
    $smi = & nvidia-smi --query-gpu=name,driver_version --format=csv,noheader 2>$null
    Write-Ok "NVIDIA 드라이버: $smi"
    $smiOut = (& nvidia-smi 2>$null) -join "`n"
    if ($smiOut -match "CUDA Version\s*:\s*([0-9.]+)") {
        Write-Host "         (드라이버 최대 CUDA 런타임: $($Matches[1]) — 11.8 이상이면 충분, 미만이면 드라이버 먼저 업데이트)"
    }
} else {
    $missing += "NVIDIA 드라이버 (nvidia-smi 미발견)"
}

if (Test-Cmd "git") { Write-Ok "Git: $((git --version))" } else { $missing += "Git" }

if (Test-Cmd "conda") { Write-Ok "Conda: $((conda --version))" } else { $missing += "Miniconda (conda 미발견)" }

if ($missing.Count -gt 0) {
    Write-Fail "다음 사전 요구 사항이 없어 중단합니다: $($missing -join ', ')"
    Write-Host  "  스크립트 상단 .PREREQUISITES 주석의 설치 안내를 따른 뒤 다시 실행하세요." -ForegroundColor Yellow
    exit 1
}

# CUDA 컴파일(tiny-cuda-nn, gsplat 첫 실행)에는 MSVC 빌드 환경이 활성화되어 있어야 한다.
# vcvars64.bat은 cmd 배치라 이 스크립트가 직접 활성화할 수 없으므로 사용자에게 안내한다.
$vcvarsCandidates = @(
    "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat",
    "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat",
    "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
)
$vcvarsFound = $vcvarsCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not (Test-Cmd "cl")) {
    if ($vcvarsFound) {
        Write-Warn2 "cl.exe가 PATH에 없습니다. CUDA 컴파일 전 MSVC 환경 활성화가 필요합니다:"
        Write-Host  "    1) cmd에서:  `"$vcvarsFound`"  실행 후  powershell  입력"
        Write-Host  "    2) 또는 'x64 Native Tools Command Prompt for VS 2022'를 열어 이 스크립트 실행"
        Write-Host  "    (nerfstudio 공식 문서: 'No CUDA toolset found' 오류 시 위 절차 필요)"
    } else {
        Write-Warn2 "Visual Studio 2022 C++ 빌드 도구(vcvars64.bat)를 찾지 못했습니다."
        Write-Host  "    tiny-cuda-nn/gsplat 컴파일 단계에서 실패할 수 있습니다. .PREREQUISITES 2번 참조."
    }
}

if (-not $VerifyOnly) {
    # ---------------------------------------------------------------- 2. conda env + 의존성
    Write-Step "2/5 conda 환경 '$EnvName' (python $PythonVersion) 생성 및 의존성 설치"

    # conda PowerShell 훅(공식 문서화된 초기화 방식)
    (& conda "shell.powershell" "hook") | Out-String | Invoke-Expression

    # 환경 존재 확인 → 없으면 생성 → 활성화
    $envExists = [bool](conda env list | Select-String -Pattern ("^" + [regex]::Escape($EnvName) + "\s") -Quiet)
    if (-not $envExists) {
        conda create --name $EnvName -y python=$PythonVersion
        if ($LASTEXITCODE -ne 0) { Write-Fail "conda 환경 생성 실패"; exit 1 }
    }
    conda activate $EnvName
    if (-not $?) { Write-Fail "conda activate 실패 — 'conda init powershell' 실행 여부 확인"; exit 1 }
    if ((& python --version) -notmatch "Python (3\.10|3\.11)") {
        Write-Fail "환경 python이 3.10/3.11이 아닙니다: $(& python --version)"
        exit 1
    }
    Write-Ok "활성 환경: $EnvName ($(& python --version))"

    # 이전 torch/functorch/tinycudann 잔여본 제거(공식 문서 권장, 없으면 skip 경고로 정상)
    python -m pip install --upgrade pip | Out-Null
    pip uninstall -y torch torchvision functorch tinycudann 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Warn2 "이전 torch 계열 언인스틀 skip(신규 환경이면 정상)" }

    # PyTorch 2.1.2 + CUDA 11.8 (공식 문서 권장 조합)
    pip install torch==2.1.2+cu118 torchvision==0.16.2+cu118 --extra-index-url https://download.pytorch.org/whl/cu118
    if ($LASTEXITCODE -ne 0) { Write-Fail "PyTorch cu118 설치 실패"; exit 1 }
    Write-Ok "PyTorch 2.1.2+cu118 설치"

    # CUDA toolkit 11.8을 conda 채널로(컴파일용 — 공식 문서 권장)
    conda install -c "nvidia/label/cuda-11.8.0" cuda-toolkit -y
    if ($LASTEXITCODE -ne 0) { Write-Fail "conda cuda-toolkit 11.8 설치 실패"; exit 1 }
    Write-Ok "cuda-toolkit 11.8 (conda) 설치"

    # tiny-cuda-nn torch 바인딩(공식 문서 Windows 탭 명령 그대로)
    pip install git+https://github.com/NVlabs/tiny-cuda-nn/#subdirectory=bindings/torch
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "tiny-cuda-nn 설치 실패 — MSVC 환경 활성화(vcvars64) 여부와 CUDA/MSVC 버전을 확인하세요."
        Write-Host  "  대처법은 docs/p0/RTX4070_학습환경_세팅_가이드.md '자주 나는 오류' 참조." -ForegroundColor Yellow
        exit 1
    }
    Write-Ok "tiny-cuda-nn 설치"

    # nerfstudio 본체
    pip install nerfstudio
    if ($LASTEXITCODE -ne 0) { Write-Fail "nerfstudio 설치 실패"; exit 1 }
    Write-Ok "nerfstudio 설치 완료 (설치된 버전: $((pip show nerfstudio | Select-String '^Version').ToString()))"

    # ---------------------------------------------------------------- 3. COLMAP / FFmpeg
    Write-Step "3/5 COLMAP / FFmpeg 확인 (실제 데이터 처리 시 필요 — 검증 학습에는 불필요)"

    if (Test-Cmd "colmap") {
        Write-Ok "COLMAP 이미 PATH에 있음: (colmap -h 동작 확인)"
        colmap -h *>$null
        if ($LASTEXITCODE -eq 0) { Write-Ok "colmap -h 정상" } else { Write-Warn2 "colmap -h 종료 코드 비정상 — PATH/설치 확인" }
    } else {
        # 공식 winget 패키지가 있는지 검색 시도(2026-10-25 기준 공식 ID는 확인되지 않았으므로
        # 검색 결과가 있어도 사람이 확인 후 설치한다 — 무조건 설치하지 않음)
        Write-Warn2 "COLMAP이 PATH에 없습니다. winget 검색을 시도합니다..."
        winget search colmap --accept-source-agreements 2>$null
        if ($LASTEXITCODE -ne 0) { Write-Warn2 "winget 검색 실패/무결과" }
        # 단일 따옴표 here-string: 내부 $env:Path 등이 실행 시점에 치환되지 않고 그대로 출력됨
        Write-Host @'
    [COLMAP 수동 설치 안내 — 권장]
      1) https://github.com/colmap/colmap/releases 에서 최신 릴리스의
         colmap-x64-windows-cuda.zip 다운로드 (2026-10-25 기준 최신: 4.2.1)
      2) 적절한 폴더(예: C:\Tools\colmap)에 압축 해제
      3) 폴더를 사용자 PATH에 추가:
         [Environment]::SetEnvironmentVariable("Path", $env:Path + ";C:\Tools\colmap\bin", "User")
      4) 터미널 재시작 후  colmap -h  로 확인
      (대안: nerfstudio 공식 문서의 vcpkg 경로 — .\vcpkg install colmap[cuda]:x64-windows)
'@ -ForegroundColor Yellow
    }

    if (Test-Cmd "ffmpeg") {
        Write-Ok "FFmpeg 이미 있음"
    } elseif (Test-Cmd "winget") {
        Write-Warn2 "FFmpeg 없음 — winget으로 설치 시도 (ns-process-data video에 필요)"
        winget install --id Gyan.FFmpeg -e --accept-package-agreements --accept-source-agreements
        if ($LASTEXITCODE -eq 0) { Write-Ok "FFmpeg 설치 완료(터미널 재시작 필요)" }
        else { Write-Warn2 "FFmpeg winget 설치 실패 — https://ffmpeg.org/download.html 에서 수동 설치" }
    } else {
        Write-Warn2 "FFmpeg 없음 + winget 없음 — ns-process-data video 사용 전 수동 설치 필요(https://ffmpeg.org/download.html)"
    }
} else {
    Write-Step "2~3단계 건너뜀 (-VerifyOnly)"
}

# ---------------------------------------------------------------- 4. CLI 검증
Write-Step "4/5 검증: ns-train --help"

if (-not (Test-Cmd "ns-train")) {
    Write-Fail "ns-train 미발견 — conda 환경 '$EnvName' 활성화 상태에서 다시 실행하세요."
    exit 1
}
ns-train --help *>$null
if ($LASTEXITCODE -ne 0) { Write-Fail "ns-train --help 실패 (종료 코드 $LASTEXITCODE)"; exit 1 }
Write-Ok "ns-train --help 정상"

# ---------------------------------------------------------------- 5. 샘플 데이터 단기 학습 (P0 종료 기준 3)
Write-Step "5/5 검증: 샘플 데이터(poster) 단기 학습 — nerfacto / splatfacto"

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
Push-Location $WorkDir
try {
    $dataDir = Join-Path $WorkDir "data\nerfstudio\poster"
    if (-not (Test-Path $dataDir)) {
        Write-Host "  샘플 데이터 내려받는 중: ns-download-data nerfstudio --capture-name=poster"
        ns-download-data nerfstudio --capture-name=poster
        if ($LASTEXITCODE -ne 0) {
            Write-Fail "샘플 데이터 다운로드 실패(네트워크 재시도 권장)."
            Write-Host  "  공개 데이터 서버 특성상 간헐적 실패가 있을 수 있다 — 명령을 다시 실행해 보세요." -ForegroundColor Yellow
            exit 1
        }
        Write-Ok "샘플 데이터 준비 완료: $dataDir"
    } else {
        Write-Ok "샘플 데이터 이미 존재: $dataDir (다운로드 생략)"
    }

    # NOTE: splatfacto는 gsplat CUDA 코드를 "첫 실행 시" 컴파일한다(공식 문서).
    #       첫 실행이 수 분 더 걸리는 것은 정상이며, MSVC 환경(vcvars64)이 활성화되어 있어야 한다.
    $results = [ordered]@{}

    Write-Host "  [1/2] ns-train nerfacto --max-num-iterations $MaxIterations --data data/nerfstudio/poster"
    ns-train nerfacto --max-num-iterations $MaxIterations --data data/nerfstudio/poster
    $results["nerfacto"]  = ($LASTEXITCODE -eq 0)

    Write-Host "  [2/2] ns-train splatfacto --max-num-iterations $MaxIterations --data data/nerfstudio/poster"
    ns-train splatfacto --max-num-iterations $MaxIterations --data data/nerfstudio/poster
    $results["splatfacto"] = ($LASTEXITCODE -eq 0)

    # ---------------------------------------------------------------- 결과 요약
    Write-Step "검증 결과 요약 (P0 종료 기준 3번: 'splatfacto/nerfacto CLI 에러 없이 구동')"
    foreach ($k in $results.Keys) {
        if ($results[$k]) { Write-Ok "$k 단기 학습($MaxIterations iter) 에러 없이 완료" }
        else              { Write-Fail "$k 학습 실패 (종료 코드 비정상) — 위 로그 확인" }
    }
    $script:P0ExitCriterion3Pass = ($results.Values -notcontains $false)

    Write-Host ""
    if ($script:P0ExitCriterion3Pass) {
        Write-Host "  >>> P0 종료 기준 3번 충족: splatfacto + nerfacto 모두 에러 없이 구동됨 <<<" -ForegroundColor Green
    } else {
        Write-Host "  >>> P0 종료 기준 3번 미충족 — 실패한 명령의 로그를 확인하세요 <<<" -ForegroundColor Red
    }
    Write-Host ""

    # ---------------------------------------------------------------- 실제 데이터 처리 예시 (주석)
    <#
    실제 상품 스캔(P0-L4, D3 2단계 경로)에 사용하는 명령 예시:

    # 휴대폰 영상(회전 촬영 mp4) → COLMAP 포즈 추정 포함 학습 데이터셋 생성:
    ns-process-data video --data C:\capture\product01.mp4 --output-dir C:\nerfstudio-work\data\product01

    # 사진 시퀀스(100~150장)로 처리:
    ns-process-data images --data C:\capture\product01_photos --output-dir C:\nerfstudio-work\data\product01

    # 생성된 데이터셋 학습(3DGS) 및 .ply 내보내기:
    ns-train splatfacto --data C:\nerfstudio-work\data\product01
    ns-export gaussian-splat --load-config C:\nerfstudio-work\outputs\product01\splatfacto\<날짜시간>\config.yml --output-dir C:\nerfstudio-work\exports\splat
    #   → exports/splat/splat.ply 를 맥으로 옮겨 SuperSplat 정제 → Unity .asset 변환(3장 파이프라인)
    # (ns-process-data는 COLMAP·FFmpeg 필요, video는 처리 시간이 오래 걸림 — 공식 문서)
    #>
} finally {
    Pop-Location
}

exit $(if ($script:P0ExitCriterion3Pass) { 0 } else { 1 })
