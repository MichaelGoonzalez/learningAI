@echo off
setlocal
cd /d "%~dp0"

set "DOTNET_CLI_HOME=%CD%\.dotnet_cli"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_EXE=C:\Program Files\dotnet\dotnet.exe"
set "OUTPUT=%CD%\dist\VisionControl.Edge"

if not exist "%DOTNET_EXE%" set "DOTNET_EXE=dotnet"
if not exist "models\yolo26n-pose.onnx" (
  echo ERROR: falta models\yolo26n-pose.onnx
  exit /b 1
)
if not exist "models\model.manifest.json" (
  echo ERROR: falta models\model.manifest.json
  exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-source-integrity.ps1
if errorlevel 1 (
  echo ERROR: Fallo la verificacion de integridad de codigo fuente.
  exit /b 1
)

taskkill /f /im VisionControl.Edge.exe /im HandRaise.Desktop.exe /im HandRaise.Host.exe >nul 2>&1

if exist "%OUTPUT%" rmdir /s /q "%OUTPUT%" >nul 2>&1
if not exist "dist" mkdir "dist"

"%DOTNET_EXE%" restore src\HandRaise.Desktop\HandRaise.Desktop.csproj --runtime win-x64 --configfile NuGet.Config
if errorlevel 1 exit /b %errorlevel%

"%DOTNET_EXE%" publish src\HandRaise.Desktop\HandRaise.Desktop.csproj --configuration Release --runtime win-x64 --self-contained true --no-restore --output "%OUTPUT%" -p:PublishSingleFile=false
if errorlevel 1 exit /b %errorlevel%

copy /y "packaging\THIRD-PARTY-NOTICES.txt" "%OUTPUT%\THIRD-PARTY-NOTICES.txt" >nul
if errorlevel 1 exit /b %errorlevel%
copy /y "packaging\LEEME.txt" "dist\LEEME.txt" >nul
if errorlevel 1 exit /b %errorlevel%
if not exist "%OUTPUT%\data" mkdir "%OUTPUT%\data"

if not exist "%OUTPUT%\VisionControl.Edge.exe" exit /b 2
if not exist "%OUTPUT%\models\yolo26n-pose.onnx" exit /b 3
if not exist "%OUTPUT%\models\model.manifest.json" exit /b 4
if not exist "%OUTPUT%\appsettings.json" exit /b 5
if not exist "%OUTPUT%\DirectML.dll" exit /b 6
if not exist "%OUTPUT%\onnxruntime.dll" exit /b 7
if not exist "%OUTPUT%\OpenCvSharpExtern.dll" exit /b 8
if not exist "%OUTPUT%\opencv_videoio_ffmpeg500_64.dll" exit /b 9
if not exist "%OUTPUT%\THIRD-PARTY-NOTICES.txt" exit /b 10

echo ----------------------------------------
echo  VisionControl Edge publicado correctamente
echo ----------------------------------------
echo.
echo Ejecutable:
echo dist\VisionControl.Edge\VisionControl.Edge.exe
echo.
endlocal
