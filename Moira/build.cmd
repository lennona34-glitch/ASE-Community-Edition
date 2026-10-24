@echo off
rem  Usage:
rem    build.cmd            build for this machine (RID auto-detected -> win-x64)
rem    build.cmd ARM64      cross-build for win-arm64 (passes -A to CMake)
rem  The output folder is resolved by CMakeLists.txt (native\<rid>\).
rem  The Moira core is committed in this folder, a copy of
rem  https://github.com/dirkwhoffmann/Moira (see native\README.md). Should it be missing,
rem  cmake would fail with an error about missing files that does not say what to do.
if not exist "Moira.cpp" (
    echo build.cmd: the Moira core is missing from this folder.
    echo            It is committed with ASE: restore it with  git checkout -- Moira/
    echo            or copy the contents of the Moira\ folder of
    echo            https://github.com/dirkwhoffmann/Moira here, WITHOUT overwriting MoiraConfig.h.
    exit /b 1
)

rem  The dangerous file is MoiraConfig.h: upstream's builds and boots just the same, but
rem  leaves PRECISE_TIMING false, so 'sync' is called at the end of each instruction
rem  instead of before each access -- and that brings down everything calibrated on
rem  where the access lands. It raises no error: it just emulates wrongly.
findstr /r /c:"^#define  *MOIRA_PRECISE_TIMING  *true" MoiraConfig.h >nul || goto badconfig
findstr /r /c:"^#define  *MOIRA_MIMIC_MUSASHI  *false" MoiraConfig.h >nul || goto badconfig
findstr /r /c:"^#define  *MOIRA_EMULATE_ADDRESS_ERROR  *true" MoiraConfig.h >nul || goto badconfig

rmdir /s /q build 2>nul
set "CMAKE_ARGS="
if not "%~1"=="" set "CMAKE_ARGS=-A %~1"
cmake -S . -B build %CMAKE_ARGS%
cmake --build build --config Release

goto :eof

:badconfig
echo build.cmd: MoiraConfig.h is not ASE's.
echo            It needs PRECISE_TIMING=true, MIMIC_MUSASHI=false and
echo            EMULATE_ADDRESS_ERROR=true. It looks like upstream's; restore it with:
echo                git checkout Moira/MoiraConfig.h
exit /b 1
