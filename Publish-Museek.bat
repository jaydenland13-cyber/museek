@echo off
REM ============================================================
REM  Builds ONE self-contained Museek.exe that runs on any
REM  Windows 10 (2004+) / Windows 11 PC - no .NET install needed.
REM  Needs the .NET 8 SDK on THIS machine and internet (first time).
REM  ReadyToRun = pre-compiled code, so it starts faster and uses less CPU
REM  while starting. (Add -p:EnableCompressionInSingleFile=true for a smaller
REM  file at the cost of a slower start.)
REM  Output: publish\Museek.exe
REM ============================================================
cd /d "%~dp0"
echo Building Museek.exe (this takes a minute)...
dotnet publish Museek.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:PublishReadyToRun=true ^
  -p:DebugType=none -p:DebugSymbols=false ^
  -o publish
if errorlevel 1 (
  echo.
  echo BUILD FAILED - scroll up for the error.
  pause
  exit /b 1
)
echo.
echo SHA-256 of the exe (put this in your release/manifest so the in-app updater can verify downloads):
certutil -hashfile publish\Museek.exe SHA256 | findstr /v "hash CertUtil"
echo.
echo ============================================================
echo  Done!  Send your friends:  publish\Museek.exe   (ONLY that file)
echo  Do NOT send settings.json, .spotify_cache or .env - those hold YOUR logins/keys.
echo ============================================================
pause
