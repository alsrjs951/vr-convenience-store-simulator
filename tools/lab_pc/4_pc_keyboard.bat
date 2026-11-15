@echo off
chcp 65001 >nul
setlocal
rem ------------------------------------------------------------------
rem  플랜 B-2: 헤드셋 없이 PC 창에서 키보드·마우스로 시연 모드를 직접 플레이한다 (더블클릭)
rem  헤드셋 대신 XR Device Simulator(모의 컨트롤러)가 켜진다. 조작은 README.txt의 표.
rem    마우스 오른쪽 버튼을 누른 채 움직이기: 고개 돌리기
rem    Y: 오른손 잡기/놓기 전환, T: 왼손 전환, Esc: 손 조작 끝내기
rem    손을 잡은 동안 마우스 이동·휠: 손 옮기기, R: 옮기기/돌리기 전환
rem    G: 쥐기(물건 집기), 마우스 왼쪽: 방아쇠, V: 손 위치 되돌리기
rem    F5: 처음부터 다시, F6: 계산대 자리로
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
if not exist "%~dp0capture\demo_logs" mkdir "%~dp0capture\demo_logs"
set "LOG=%~dp0capture\demo_logs\keyboard_%STAMP%.log"
set "XR_RUNTIME_JSON=C:\none\none.json"
echo.
echo  PC 창에서 키보드·마우스 시연 모드를 켭니다. 게임 창을 한 번 누르고 시작하세요.
echo  로그: %LOG%
echo.
start "" "%EXE%" -demo -screen-fullscreen 0 -screen-width 1920 -screen-height 1080 -logFile "%LOG%"
timeout /t 4 >nul
exit /b 0
