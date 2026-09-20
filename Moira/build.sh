#!/bin/bash
set -e

# Uso:
#   ./build.sh                 compila para el equipo actual (RID autodetectado)
#   ./build.sh osx-arm64       macOS Apple Silicon
#   ./build.sh osx-x64         macOS Intel (cross desde Apple Silicon)
#   ./build.sh <os>-<arch>     fuerza la carpeta de salida native/<os>-<arch>/
# Los argumentos adicionales se pasan tal cual a la configuración de cmake,
# p. ej. una toolchain de cross-compilado:
#   ./build.sh linux-arm64 -DCMAKE_TOOLCHAIN_FILE=/ruta/aarch64.cmake
#
# La carpeta de destino la resuelve CMakeLists.txt (native/<rid>/), acorde
# al layout por RID de ASE.csproj.

# El nucleo de Moira no vive en este repositorio: se descarga de
# https://github.com/dirkwhoffmann/Moira (ver native/README.md). Sin el, cmake
# fallaria con un error sobre ficheros que faltan, que no dice que hay que hacer.
if [ ! -f Moira.cpp ]; then
    echo "build.sh: falta el nucleo de Moira en esta carpeta."
    echo "          Clona https://github.com/dirkwhoffmann/Moira y copia aqui el"
    echo "          contenido de su carpeta Moira/, SIN sobrescribir MoiraConfig.h."
    exit 1
fi

# Y con el nucleo presente, lo peligroso es MoiraConfig.h: el de upstream compila
# igual y arranca igual, pero deja PRECISE_TIMING en false, con lo que 'sync' pasa a
# llamarse al final de cada instruccion en vez de antes de cada acceso -- y con eso se
# cae todo lo calibrado sobre la posicion del acceso (el adelanto de paleta de
# Spectrum 512, ApplyBusWait y el blitter entero). No da ningun error: solo emula mal.
check_flag() {
    if ! grep -Eq "^#define[[:space:]]+$1[[:space:]]+$2([[:space:]]|\$)" MoiraConfig.h; then
        echo "build.sh: MoiraConfig.h no es el de ASE ($1 deberia ser $2)."
        echo "          Parece el de upstream. Recuperalo con:  git checkout Moira/MoiraConfig.h"
        exit 1
    fi
}
check_flag MOIRA_PRECISE_TIMING       true
check_flag MOIRA_MIMIC_MUSASHI        false
check_flag MOIRA_EMULATE_ADDRESS_ERROR true

target="${1:-}"
[ $# -gt 0 ] && shift
extra_args=("$@")
cmake_args=()

case "$target" in
    "")        ;;                                                      # host
    osx-arm64) cmake_args+=(-DCMAKE_OSX_ARCHITECTURES=arm64  -DMOIRA_RID=osx-arm64) ;;
    osx-x64)   cmake_args+=(-DCMAKE_OSX_ARCHITECTURES=x86_64 -DMOIRA_RID=osx-x64) ;;
    *-*)       cmake_args+=(-DMOIRA_RID="$target") ;;
    *)         echo "build.sh: destino desconocido '$target' (usa <os>-<arch>, p. ej. linux-arm64)"; exit 1 ;;
esac

rm -rf build
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release "${cmake_args[@]}" "${extra_args[@]}"

# Core count, portably: getconf is available on both Linux and macOS, unlike the BSD-only
# `sysctl -n hw.ncpu` this used to call, which made the script abort on Linux before building
# anything. Falls back to a single job if the query is unavailable.
njobs=$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 1)
cmake --build build --config Release -j "$njobs"
