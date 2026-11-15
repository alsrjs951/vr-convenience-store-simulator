@echo off
chcp 65001 >nul
rem 동아편의점 시연용 PC 사양 확인 (더블클릭). 읽기만 하고 아무것도 바꾸지 않는다.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0check_pc.ps1"
echo.
pause
