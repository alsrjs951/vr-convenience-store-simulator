@echo off
chcp 65001 >nul
setlocal
rem ------------------------------------------------------------------
rem  플랜 B-1: 헤드셋 없이 PC 창에서 게임이 스스로 한 판을 플레이한다 (더블클릭)
rem  자동 검사(-p2-demo-verify)를 그대로 돌린다: 튜토리얼 -> 손님 3명 -> 정산,
rem  중간에 처음부터 다시(F5)와 자리 맞춤(F6)도 스스로 해 본다. 약 5분, 끝나면 창이 저절로 닫힌다.
rem  도는 동안 마우스·키보드는 게임에 들어가지 않는다(검사가 막는다). 멈추려면 창을 닫는다.
rem  헤드셋(Link)은 일부러 쓰지 않는다.
rem ------------------------------------------------------------------
cd /d "%~dp0"
set "EXE=%~dp0Builds\Store\VRStore.exe"
if not exist "%EXE%" set "EXE=%~dp0..\..\Builds\Store\VRStore.exe"
if not exist "%EXE%" (
    echo  [오류] 게임 파일을 찾지 못했습니다. Builds\Store 폴더가 이 파일 옆에 있어야 합니다.
    pause
    exit /b 1
)
tasklist /fi "imagename eq VRStore.exe" 2>nul | find /i "VRStore.exe" >nul
if not errorlevel 1 (
    echo  게임이 이미 켜져 있습니다. 먼저 게임 창을 닫고 다시 실행하세요.
    pause
    exit /b 0
)
for /f %%i in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd_HHmmss"') do set "STAMP=%%i"
set "OUT=%~dp0capture\demo_logs\auto_%STAMP%"
mkdir "%OUT%" 2>nul
set "XR_RUNTIME_JSON=C:\none\none.json"
echo.
echo  PC 창에서 자동 플레이를 시작합니다(약 5분, 끝나면 창이 닫힙니다).
echo  로그: %OUT%\play.log
echo.
start "" "%EXE%" -p2-demo-verify -p2-evidence "%OUT%" -screen-fullscreen 0 -screen-width 1920 -screen-height 1080 -logFile "%OUT%\play.log"
timeout /t 4 >nul
exit /b 0
