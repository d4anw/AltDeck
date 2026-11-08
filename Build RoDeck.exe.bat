@echo off
rem Builds one RoDeck.exe that runs on any 64-bit Windows 10/11 PC, no .NET install needed.
rem The result is in the "publish" folder next to this file.
cd /d "%~dp0"
echo Building RoDeck.exe ...
dotnet publish -c Release -r win-x64 --self-contained true -o publish ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=none
if errorlevel 1 (
  echo.
  echo Build failed. Copy the red lines above when asking for help.
  pause
  exit /b 1
)
echo.
echo Done: %~dp0publish\RoDeck.exe
explorer "%~dp0publish"
pause
