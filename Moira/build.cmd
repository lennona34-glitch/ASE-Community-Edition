@echo off
rem  Uso:
rem    build.cmd            compila para el equipo actual (RID autodetectado -> win-x64)
rem    build.cmd ARM64      cross-compila para win-arm64 (pasa -A a CMake)
rem  La carpeta de destino la resuelve CMakeLists.txt (native\<rid>\).
rem  El nucleo de Moira no vive en este repositorio: se descarga de
rem  https://github.com/dirkwhoffmann/Moira (ver native\README.md).
if not exist "Moira.cpp" (
    echo build.cmd: falta el nucleo de Moira en esta carpeta.
    echo            Clona https://github.com/dirkwhoffmann/Moira y copia aqui el
    echo            contenido de su carpeta Moira\, SIN sobrescribir MoiraConfig.h.
    exit /b 1
)

rem  Lo peligroso es MoiraConfig.h: el de upstream compila igual y arranca igual, pero
rem  deja PRECISE_TIMING en false, con lo que 'sync' pasa a llamarse al final de cada
rem  instruccion en vez de antes de cada acceso -- y con eso se cae todo lo calibrado
rem  sobre la posicion del acceso. No da ningun error: solo emula mal.
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
echo build.cmd: MoiraConfig.h no es el de ASE.
echo            Necesita PRECISE_TIMING=true, MIMIC_MUSASHI=false y
echo            EMULATE_ADDRESS_ERROR=true. Parece el de upstream; recuperalo con:
echo                git checkout Moira/MoiraConfig.h
exit /b 1
