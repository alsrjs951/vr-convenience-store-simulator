@echo off
chcp 65001 >nul
setlocal
rem ------------------------------------------------------------------
rem  동아편의점 시연 모드 켜기 (더블클릭)
rem  순서: Link 연결 -> 헤드셋을 쓰고 Link 홈(하얀 배경) 확인 -> 이 파일 실행
rem        -> 게임 창을 한 번 누르고 F5. 다음 사람도 F5, 자리가 어긋나면 F6.
rem  자세한 순서: README.txt(실습실 PC 묶음) 또는 docs\p5\발표날_시연_진행순서.md(저장소)
rem  로그: capture\demo_logs\demo_날짜_시각.log
rem  뒤에 인자를 붙이면 게임에 그대로 넘긴다.
rem ------------------------------------------------------------------
cd /d "%~dp0"
set "EXE=%~dp0Builds\Store\VRStore.exe"

if not exist "%EXE%" (
    echo.
    echo  [오류] 게임 파일이 없습니다: %EXE%
    echo  Builds\Store 폴더를 통째로 이 파일 옆에 두어야 합니다.
    echo.
    pause
    exit /b 1
)

tasklist /fi "imagename eq VRStore.exe" 2>nul | find /i "VRStore.exe" >nul
if not errorlevel 1 (
    echo.
    echo  게임이 이미 켜져 있습니다. 새로 켜지 않습니다.
    echo  - 처음부터 다시: 게임 창을 한 번 누르고 F5
    echo  - 헤드셋에 안 보이면: 게임 창을 닫고 이 파일을 다시 실행
    echo.
    pause
    exit /b 0
)

tasklist /fi "imagename eq OVRServer_x64.exe" 2>nul | find /i "OVRServer_x64.exe" >nul
if errorlevel 1 (
    echo.
    echo  [주의] Meta Horizon Link가 켜져 있지 않습니다.
    echo  지금 켜면 헤드셋이 아니라 PC 창에서만 돕니다.
    echo  Link 앱을 켜고 헤드셋을 연결한 뒤 다시 실행하는 것을 권합니다.
    echo.
    choice /c YN /m " 그래도 켤까요"
    if errorlevel 2 exit /b 0
)

for /f %%i in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd_HHmmss"') do set "STAMP=%%i"
if not exist "%~dp0capture\demo_logs" mkdir "%~dp0capture\demo_logs"
set "LOG=%~dp0capture\demo_logs\demo_%STAMP%.log"

echo.
echo  시연 모드를 켭니다. 헤드셋에 매장이 보이면
echo  PC의 게임 창을 한 번 누르고 F5 를 누르세요.
echo  로그: %LOG%
echo.
start "" "%EXE%" -demo -screen-fullscreen 0 -screen-width 1920 -screen-height 1080 -logFile "%LOG%" %*
timeout /t 4 >nul
exit /b 0
