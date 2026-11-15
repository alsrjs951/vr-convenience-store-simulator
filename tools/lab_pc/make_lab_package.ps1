# 실습실 PC로 가져갈 시연 묶음을 만든다: Builds\Package\DongA_Store_Demo\ 와 DongA_Store_Demo.zip
# 실행: powershell -NoProfile -ExecutionPolicy Bypass -File tools\lab_pc\make_lab_package.ps1
# 플레이어를 다시 빌드했으면 이것도 다시 돌린다. 묶음 안의 파일 이름은 압축이 어디서 풀려도 깨지지 않게 영문으로 둔다.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$out = Join-Path $repo 'Builds\Package'
$pkg = Join-Path $out 'DongA_Store_Demo'
$zip = Join-Path $out 'DongA_Store_Demo.zip'
if (-not (Test-Path (Join-Path $repo 'Builds\Store\VRStore.exe'))) { throw 'Builds\Store\VRStore.exe가 없다. 플레이어를 먼저 빌드한다.' }
if (Get-Process VRStore -ErrorAction SilentlyContinue) { throw '게임이 켜져 있다. 끄고 다시 돌린다.' }

if (Test-Path $pkg) { Remove-Item $pkg -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
New-Item -ItemType Directory -Force -Path $pkg | Out-Null

# 게임: 실행에 필요 없는 백업·디버그 폴더는 뺀다.
robocopy (Join-Path $repo 'Builds\Store') (Join-Path $pkg 'Builds\Store') /E /XD '*DontShip*' '*DoNotShip*' /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy 실패: $LASTEXITCODE" }

Copy-Item (Join-Path $repo '시연모드_켜기.bat') (Join-Path $pkg '2_start_demo.bat')
Copy-Item (Join-Path $PSScriptRoot '1_check_pc.bat') $pkg
Copy-Item (Join-Path $PSScriptRoot 'check_pc.ps1') $pkg
Copy-Item (Join-Path $PSScriptRoot '3_pc_autoplay.bat') $pkg
Copy-Item (Join-Path $PSScriptRoot '4_pc_keyboard.bat') $pkg
Get-Content (Join-Path $repo 'docs\p5\발표날_시연_진행순서.md') -Encoding UTF8 | Set-Content (Join-Path $pkg 'README.txt') -Encoding UTF8
$video = Join-Path $repo 'capture\film\presentation_v6_1min.mp4'
if (Test-Path $video) { Copy-Item $video (Join-Path $pkg 'demo_video_1min.mp4') }

& (Join-Path $env:SystemRoot 'System32\tar.exe') -a -cf $zip -C $out 'DongA_Store_Demo'
if ($LASTEXITCODE -ne 0) { throw "압축 실패: $LASTEXITCODE" }

$size = (Get-ChildItem $pkg -Recurse -File | Measure-Object Length -Sum).Sum
"폴더: $pkg ({0:N2} GB)" -f ($size / 1GB)
"압축: $zip ({0:N2} GB)" -f ((Get-Item $zip).Length / 1GB)
"플레이어 빌드 시각: {0}" -f (Get-Item (Join-Path $repo 'Builds\Store\GameAssembly.dll')).LastWriteTime   # VRStore.exe는 빌드해도 시각이 안 바뀐다
