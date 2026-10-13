@echo off
chcp 65001 >nul
setlocal
title AI Usage Monitor 빌드

set "PROJECT=%~dp0src\UsageMonitor.csproj"
if not exist "%PROJECT%" set "PROJECT=%~dp0소스\UsageMonitor.csproj"
set "OUTPUT=%~dp0artifacts\publish"

echo AI Usage Monitor 실행 파일을 만듭니다.
echo.

if not exist "%PROJECT%" (
    echo [실패] 소스 폴더를 찾을 수 없습니다.
    echo 배치파일을 src 또는 소스 폴더와 같은 위치에 놓아 주세요.
    goto failed
)

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [실패] .NET 9 SDK가 필요합니다. 설치 후 다시 실행해 주세요.
    goto failed
)

dotnet --list-sdks | findstr /b /l /c:"9." >nul
if errorlevel 1 (
    echo [실패] .NET 9 SDK를 찾을 수 없습니다.
    echo Runtime만 설치된 경우에는 SDK도 설치해야 합니다.
    goto failed
)

dotnet publish "%PROJECT%" -c Release -o "%OUTPUT%" -p:RuntimeFrameworkVersion=9.0.11 -p:NuGetAudit=false
if errorlevel 1 goto failed
if not exist "%OUTPUT%\AI Usage Monitor.exe" goto failed

echo.
echo [완료] 실행 파일을 만들었습니다.
echo "%OUTPUT%\AI Usage Monitor.exe"
if /i "%~1"=="--no-ui" exit /b 0
start "" "%OUTPUT%"
pause
exit /b 0

:failed
echo.
echo 빌드하지 못했습니다. 위의 오류 내용을 확인해 주세요.
if /i "%~1"=="--no-ui" exit /b 1
pause
exit /b 1
