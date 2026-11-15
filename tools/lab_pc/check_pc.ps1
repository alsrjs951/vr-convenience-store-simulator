# 동아편의점 시연용 PC 사양 확인 (Windows PowerShell 5.1 이상)
# 1_check_pc.bat이 부른다. 결과를 화면에 보이고 같은 폴더의 check_result_<PC이름>_<시각>.txt로 저장한다.
# 아무것도 설치하거나 바꾸지 않는다. 읽기만 한다.
$ErrorActionPreference = 'SilentlyContinue'
$lines = New-Object System.Collections.Generic.List[string]
function Say([string]$text) { $lines.Add($text); Write-Host $text }
function Verdict([string]$mark, [string]$text) { Say ("  [{0}] {1}" -f $mark, $text) }

Say "동아편의점 시연용 PC 사양 확인  $(Get-Date -Format 'yyyy-MM-dd HH:mm')  PC: $env:COMPUTERNAME"
Say "기준: RTX 4070에서 GPU 평균 5.9ms, 최대 11.9ms(72fps 한도 13.9ms). 아래 판정은 대략이다."
Say ""

# 1. 그래픽카드
Say "1. 그래픽카드"
$gpus = @(Get-CimInstance Win32_VideoController | Where-Object { $_.Name -notmatch 'Virtual|Remote|Basic Display|Mirror|Parsec|USB Mobile' })
$best = 'none'
foreach ($g in $gpus) {
    $mem = ''
    $key = Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}' |
        Where-Object { (Get-ItemProperty $_.PSPath).DriverDesc -eq $g.Name } | Select-Object -First 1
    if ($key) { $q = (Get-ItemProperty $key.PSPath).'HardwareInformation.qwMemorySize'; if ($q) { $mem = '{0:N1} GB' -f ($q / 1GB) } }
    Say ("   {0}  (드라이버 {1}{2})" -f $g.Name, $g.DriverVersion, $(if ($mem) { ", 메모리 $mem" } else { '' }))
    $n = $g.Name
    if ($n -match 'RTX\s*(40|50)\d\d|RTX\s*30[6-9]0|RTX\s*20[78]0|RTX\s*A[2-6]000|RX\s*(6[6-9]|7\d|9\d)\d\d') { $best = 'good' }
    elseif ($best -ne 'good' -and $n -match 'RTX\s*(2060|3050)|GTX\s*(1070|1080|1660)|RX\s*(5[67]\d\d|6500)') { $best = 'tight' }
    elseif ($best -eq 'none' -and $n -match 'NVIDIA|AMD|Radeon') { $best = 'weak' }
}
switch ($best) {
    'good'  { Verdict '좋음' '이 게임을 72fps로 돌릴 가능성이 높다.' }
    'tight' { Verdict '빠듯' '돌아는 가겠지만 프레임이 빠질 수 있다. Link 앱에서 렌더 해상도를 낮추고 72Hz로 둔다.' }
    'weak'  { Verdict '부족' 'Link 최소 사양에 못 미치거나 겨우 맞는다. 헤드셋 체험은 위험하다. 영상으로 대신할 준비를 한다.' }
    default { Verdict '부족' 'NVIDIA/AMD 그래픽카드를 찾지 못했다(내장 그래픽만 있음). Link가 안 된다.' }
}
Say ""

# 2. CPU, 메모리, Windows
Say "2. CPU · 메모리 · Windows"
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$ram = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB
$os = Get-CimInstance Win32_OperatingSystem
Say ("   CPU: {0}" -f $cpu.Name.Trim())
Say ("   메모리: {0:N0} GB" -f $ram)
Say ("   Windows: {0} (빌드 {1})" -f $os.Caption, $os.BuildNumber)
if ($ram -ge 15) { Verdict '좋음' '메모리 충분.' } elseif ($ram -ge 7.5) { Verdict '빠듯' '메모리 8GB: Link 최소 사양. 다른 프로그램을 모두 닫고 켠다.' } else { Verdict '부족' '메모리가 8GB보다 적다.' }
Say ""

# 3. USB 3 (Link 케이블)
Say "3. USB 3 컨트롤러 (Link 케이블은 USB 3 포트에 꽂는다)"
$usb = @(Get-CimInstance Win32_USBController | Select-Object -ExpandProperty Name)
$usb | ForEach-Object { Say "   $_" }
if ($usb -match '3\.\d|xHCI|eXtensible|확장') { Verdict '좋음' 'USB 3 컨트롤러가 있다. 파란색 USB-A 또는 USB-C 포트에 꽂는다.' } else { Verdict '확인' 'USB 3 컨트롤러 이름을 못 찾았다. 포트를 눈으로 확인한다.' }
Say ""

# 4. Link 앱과 OpenXR 런타임
Say "4. Meta Horizon Link 앱"
$base = (Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Oculus VR, LLC\Oculus').Base
if ($base -and (Test-Path $base)) { Verdict '좋음' "설치돼 있다: $base" } else { Verdict '없음' '설치돼 있지 않다. meta.com에서 Link 앱을 받아 설치해야 한다(관리자 권한 필요).' }
$server = Get-Process OVRServer_x64 -ErrorAction SilentlyContinue
Say ("   Link 서비스 실행 중: {0}" -f $(if ($server) { '예' } else { '아니오' }))
$rt = (Get-ItemProperty 'HKLM:\SOFTWARE\Khronos\OpenXR\1').ActiveRuntime
Say ("   현재 OpenXR 런타임: {0}" -f $(if ($rt) { $rt } else { '(없음)' }))
if ($rt -match 'oculus|Meta') { Verdict '좋음' 'Link가 OpenXR 런타임으로 잡혀 있다.' }
elseif ($rt) { Verdict '고칠것' '다른 프로그램(SteamVR 등)이 OpenXR을 잡고 있다. Link 앱 > 설정 > 일반 > OpenXR 런타임에서 Meta Horizon Link로 바꾼다.' }
else { Verdict '확인' 'OpenXR 런타임이 없다. Link 앱을 설치하면 생긴다.' }
Say ""

# 5. 권한과 디스크
Say "5. 관리자 권한 · 디스크"
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$inAdminGroup = (whoami /groups) -match 'S-1-5-32-544'
Say ("   지금 창이 관리자 권한: {0} / 관리자 그룹 소속: {1}" -f $(if ($admin) { '예' } else { '아니오' }), $(if ($inAdminGroup) { '예' } else { '아니오' }))
if (-not $inAdminGroup -and -not $base) { Verdict '위험' '관리자가 아니고 Link 앱도 없다. 설치를 관리자(조교)에게 부탁해야 한다.' }
$here = Split-Path -Qualifier $PSScriptRoot
$drive = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$here'"
if ($drive) { Say ("   {0} 드라이브 남은 공간: {1:N1} GB (게임 약 0.8GB, Link 앱 수 GB)" -f $here, ($drive.FreeSpace / 1GB)) }
$frozen = Get-Service -Name 'DFServ', 'DeepFreeze*', 'MagicFreeze*' -ErrorAction SilentlyContinue
if ($frozen) { Verdict '주의' '복원 프로그램(Deep Freeze 류)이 보인다. 다시 켜면 설치한 것이 사라질 수 있다. 발표 전에 재부팅하지 않는다.' }
Say ""

$out = Join-Path $PSScriptRoot ("check_result_{0}_{1}.txt" -f $env:COMPUTERNAME, (Get-Date -Format 'yyyyMMdd_HHmm'))
$lines | Out-File -FilePath $out -Encoding utf8
Write-Host "결과를 저장했다: $out"
Write-Host "이 파일을 사진 찍거나 보내 주면 판단을 돕겠다."
