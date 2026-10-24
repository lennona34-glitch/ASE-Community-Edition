#!/bin/bash
set -e

# Usage:
#   ./build.sh                 build for this machine (RID auto-detected)
#   ./build.sh osx-arm64       macOS Apple Silicon
#   ./build.sh osx-x64         macOS Intel (cross-built from Apple Silicon)
#   ./build.sh <os>-<arch>     forces the output folder native/<os>-<arch>/
# Extra arguments go straight to cmake's configure step, e.g. a cross-compiling
# toolchain (the one for linux-arm64 from an x86-64 Linux, WSL included, is in toolchains/):
#   ./build.sh linux-arm64 -DCMAKE_TOOLCHAIN_FILE=toolchains/aarch64-linux-gnu.cmake
# An <os>-<arch> other than this machine's requires that toolchain (or
# -DCMAKE_CXX_COMPILER); without one the script refuses. The two osx-* on a Mac
# are the exception.
#
# The output folder is resolved by CMakeLists.txt (native/<rid>/), following
# ASE.csproj's per-RID layout.

# The Moira core is committed in this folder, a copy of
# https://github.com/dirkwhoffmann/Moira (see native/README.md). Should it be missing
# (a partial checkout, a folder copied by hand) cmake would fail with an error about
# missing files that does not say what to do.
if [ ! -f Moira.cpp ]; then
    echo "build.sh: the Moira core is missing from this folder."
    echo "          It is committed with ASE: restore it with  git checkout -- Moira/"
    echo "          or copy the contents of the Moira/ folder of"
    echo "          https://github.com/dirkwhoffmann/Moira here, WITHOUT overwriting MoiraConfig.h."
    exit 1
fi

# With the core present, the dangerous file is MoiraConfig.h: upstream's builds and
# boots just the same, but leaves PRECISE_TIMING false, so 'sync' is called at the end
# of each instruction instead of before each access -- and that brings down everything
# calibrated on where the access lands (the Spectrum 512 palette lead, ApplyBusWait and
# the whole blitter). It raises no error: it just emulates wrongly.
check_flag() {
    if ! grep -Eq "^#define[[:space:]]+$1[[:space:]]+$2([[:space:]]|\$)" MoiraConfig.h; then
        echo "build.sh: MoiraConfig.h is not ASE's ($1 should be $2)."
        echo "          It looks like upstream's. Restore it with:  git checkout Moira/MoiraConfig.h"
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
    *)         echo "build.sh: unknown target '$target' (use <os>-<arch>, e.g. linux-arm64)"; exit 1 ;;
esac

# A foreign RID without a toolchain is not a cross build: cmake uses this machine's
# compiler and MOIRA_RID only renames the output folder, so './build.sh linux-arm64'
# on an x64 PC left an x86-64 binary in native/linux-arm64/ without a word (October
# 2026). The Raspberry Pi would have rejected it at load time, with a
# DllNotFoundException that does not say why. macOS is the exception: its clang
# generates both architectures through CMAKE_OSX_ARCHITECTURES, which is what the
# osx-* cases above set.
if [ -n "$target" ]; then
    case "$(uname -s)" in
        Linux)                host_os=linux ;;
        Darwin)               host_os=osx ;;
        MINGW*|MSYS*|CYGWIN*) host_os=win ;;
        *)                    host_os=unknown ;;
    esac
    case "$(uname -m)" in
        x86_64|amd64)  host_arch=x64 ;;
        aarch64|arm64) host_arch=arm64 ;;
        *)             host_arch=unknown ;;
    esac
    target_os="${target%%-*}"
    target_arch="${target#*-}"

    cross_toolchain=false
    [ -n "${CMAKE_TOOLCHAIN_FILE:-}" ] && cross_toolchain=true    # cmake >= 3.21 reads it from the environment
    for arg in "${extra_args[@]}"; do
        case "$arg" in
            -DCMAKE_TOOLCHAIN_FILE=*|-DCMAKE_CXX_COMPILER=*) cross_toolchain=true ;;
        esac
    done

    if [ "$target_os" != "$host_os" ] ||
       { [ "$target_arch" != "$host_arch" ] && [ "$host_os" != osx ]; }; then
        if [ "$cross_toolchain" != true ]; then
            # Point at the toolchain this repository ships for the target, if there is one.
            toolchain_hint=/path/toolchain.cmake
            [ "$target" = linux-arm64 ] && [ "$host_os" = linux ] &&
                toolchain_hint=toolchains/aarch64-linux-gnu.cmake
            echo "build.sh: '$target' is not this machine's platform ($host_os-$host_arch)."
            echo "          Without a toolchain cmake would build for $host_os-$host_arch and put"
            echo "          that binary in native/$target/. Pass the target's toolchain, e.g.:"
            echo "            ./build.sh $target -DCMAKE_TOOLCHAIN_FILE=$toolchain_hint"
            exit 1
        fi
    fi
fi

rm -rf build
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release "${cmake_args[@]}" "${extra_args[@]}"

# Core count, portably: getconf is available on both Linux and macOS, unlike the BSD-only
# `sysctl -n hw.ncpu` this used to call, which made the script abort on Linux before building
# anything. Falls back to a single job if the query is unavailable.
njobs=$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 1)
cmake --build build --config Release -j "$njobs"
