@echo off
REM Boots the Auxilia presentation stand in its own console window.
REM Requirements: Docker Desktop running, .NET 10 SDK. The first boot on a fresh
REM machine builds all workflow images from source (~10 minutes); later boots are
REM fast. Data (login, connectors, configurations) persists across restarts in
REM local Docker volumes. The dashboard URL is printed in the stand's window.
cd /d "%~dp0"

docker info >nul 2>&1
if errorlevel 1 (
    echo Docker Desktop is not running - start it first.
    pause
    exit /b 1
)

echo Building the dev stand ...
dotnet build Auxilia.DevStand\Auxilia.DevStand.csproj --nologo -v q
if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
)

start "Auxilia DevStand" Auxilia.DevStand\bin\Debug\net10.0\Auxilia.DevStand.exe --presentation --keep-data
echo Stand starting in its own window - the dashboard URL appears there when ready.
