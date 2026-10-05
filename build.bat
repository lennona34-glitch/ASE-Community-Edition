@echo off
setlocal enabledelayedexpansion

title Building Atari System Emulator (ASE)

echo ===================================================
echo     Atari System Emulator (ASE) - Build Script
echo ===================================================
echo.

:: 1. Check for dotnet SDK
where dotnet >nul 2>nul
if %ERRORLEVEL% neq 0 (
    echo [ERROR] .NET SDK is not found in your PATH.
    echo Please install the .NET SDK from: https://dotnet.microsoft.com/download
    echo.
    pause
    exit /b 1
)

:: Locate ASE.csproj relative to script location
set "SCRIPT_DIR=%~dp0"
if exist "%SCRIPT_DIR%ASE\ASE.csproj" (
    set "PROJ_PATH=%SCRIPT_DIR%ASE\ASE.csproj"
    set "EXE_DIR=%SCRIPT_DIR%ASE\bin\Release\net9.0"
) else if exist "%SCRIPT_DIR%ASE.csproj" (
    set "PROJ_PATH=%SCRIPT_DIR%ASE.csproj"
    set "EXE_DIR=%SCRIPT_DIR%bin\Release\net9.0"
) else if exist "%SCRIPT_DIR%ase-1.12\ASE\ASE.csproj" (
    set "PROJ_PATH=%SCRIPT_DIR%ase-1.12\ASE\ASE.csproj"
    set "EXE_DIR=%SCRIPT_DIR%ase-1.12\ASE\bin\Release\net9.0"
) else (
    echo [ERROR] Could not find ASE.csproj.
    pause
    exit /b 1
)

echo Building ASE in Release configuration...
echo Project: "%PROJ_PATH%"
echo.

dotnet build "%PROJ_PATH%" -c Release
if %ERRORLEVEL% neq 0 (
    echo.
    echo ===================================================
    echo [ERROR] Build failed! Check the error messages above.
    echo ===================================================
    echo.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo ===================================================
echo [SUCCESS] Atari System Emulator built successfully!
echo Executable: "%EXE_DIR%\ASE.exe"
echo ===================================================
echo.

set /p RUN_NOW="Do you want to launch the emulator now? (Y/N) [default: Y]: "
if /i "%RUN_NOW%"=="" set RUN_NOW=Y
if /i "%RUN_NOW%"=="Y" (
    echo Starting ASE...
    start "" "%EXE_DIR%\ASE.exe"
)
