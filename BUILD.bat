@echo off
echo Building ViPadLinker.exe (net48)...
echo.

where dotnet >nul 2>&1
if %errorlevel% neq 0 (
    echo ERROR: .NET SDK not found.
    echo Download from: https://dotnet.microsoft.com/download
    pause
    exit /b 1
)

dotnet build src\ViPadLinker.csproj -c Release -o .\bin\release
if %errorlevel% neq 0 goto :fail

echo.
echo SUCCESS! Files Output in bin\release\
echo.
echo Output Files needed:
echo   ViPadLinker.exe  - system tray app
echo   Nefarius.ViGEm.Client.dll
echo   ViGEmBus_setup.exe
exit /b 0

:fail
echo Build failed. Make sure .NET SDK is installed.
exit /b 1
