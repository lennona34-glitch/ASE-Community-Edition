# native/ — Native Libraries by RID

Native libraries are separated by **.NET RID** (`<os>-<architecture>`).
The `.csproj` file copies to the output **only** the folder corresponding to the RID targeted for compilation/publishing (or, in a `dotnet build`/`run` without `-r`, the host machine's RID).

```
native/
  win-x64/      moira.dll, SDL2.dll, mt32emu-2.dll   (+ moira.lib, moira.exp — linking artifacts, not copied)
  osx-x64/      moira.dylib, libSDL2.dylib, libmt32emu.2.dylib
  osx-arm64/    moira.dylib, libSDL2.dylib, libmt32emu.2.dylib
  linux-x64/    moira.so, libmt32emu.so.2
  linux-arm64/  moira.so, libmt32emu.so.2, libtinyfiledialogs.so
```

## Folder Contents Breakdown

| RID           | Moira        | SDL2            | libmt32emu             | Notes                                                         |
|---------------|--------------|-----------------|------------------------|---------------------------------------------------------------|
| `win-x64`     | `moira.dll`  | `SDL2.dll`      | `mt32emu-2.dll`      | Moira built with static MSVC runtime (see `Moira/CMakeLists.txt`) |
| `osx-x64`     | `moira.dylib`| `libSDL2.dylib` | `libmt32emu.2.dylib` | Intel Mac (Not officially supported)                          |
| `osx-arm64`   | `moira.dylib`| `libSDL2.dylib` | `libmt32emu.2.dylib` | Apple Silicon Mac                                             |
| `linux-x64`   | `moira.so`   | —               | `libmt32emu.so.2`    | SDL2 is provided by the distribution (not packaged)           |
| `linux-arm64` | `moira.so`   | —               | `libmt32emu.so.2`    | Raspberry Pi / ARM; SDL2 from the distribution                |

## Building Moira for Each Architecture

You do not need to rebuild **Moira**, as it is already precompiled in the `native` directory.

If you do want to build it, note that **the Moira core is not part of this repository**. Only the files ASE owns live in `Moira/` — `CMakeLists.txt`, `MoiraConfig.h`, `Moira_dotnet.cpp/.h`, `build.cmd`, `build.sh` — and the core is fetched from the original project, so that you always build against the current version and the traffic goes where it belongs:

```
git clone https://github.com/dirkwhoffmann/Moira
```

Copy the contents of its `Moira/` subfolder into ASE's `Moira/` folder, **without overwriting `MoiraConfig.h`**, and run `build.cmd` (Windows) or `build.sh` (macOS/Linux). `Moira/.gitignore` is a whitelist, so anything you drop in there is ignored by git automatically, including whatever files a future Moira release adds.

> **`MoiraConfig.h` is ASE's, not upstream's.** It differs in three settings that change nothing you can see at build time and everything at run time: `MOIRA_PRECISE_TIMING` (`true` here, `false` upstream), `MOIRA_MIMIC_MUSASHI` (`false` / `true`) and `MOIRA_EMULATE_ADDRESS_ERROR` (`true` / `false`). With upstream's file the emulator still compiles and still boots — it just reports every bus access at the end of the instruction instead of when it happens, which quietly invalidates the palette timing, the bus wait states and the whole blitter model, and drops the address errors several games depend on. Both build scripts check these three before compiling and stop with an explanation, so a stray copy is caught rather than debugged.

> **The build patches one line of the core.** `Moira/CMakeLists.txt` moves the prefetch of `DIVS`/`DIVU` to the end of the instruction in the copy of `MoiraExec_cpp.h` you dropped in (marked `ASE_DIV_PREFETCH_LAST`, so it is done once and reported in the CMake output). A real 68000 issues that bus access last — yacht.txt, and the bus traces of `SingleStepTests/680x0` in 1239/1239 `DIVS` and 2494/2494 `DIVU` cases — while Moira issues it first, which with `MOIRA_PRECISE_TIMING` puts it 136 cycles early and is measurable through the blitter's bus arbitration (see *The blitter takes the bus* in `CLAUDE.md`). If upstream ever fixes it, the build stops with an explanation and the block can go.

When the upstream API grows, two files are yours to adapt: `Moira_dotnet.cpp` (the wrapper must implement every new pure virtual — `read32`/`write32` arrived in September 2026) and `CMakeLists.txt` (new translation units must be listed — `MoiraCore68000/68010/68020.cpp` arrived at the same time).

On Linux/macOS, `build.sh` targets the host architecture. For *cross-compilation* (e.g., building `linux-arm64` from x64, or `osx-x64` from Apple Silicon), pass the appropriate toolchain/architecture flag to the build generator. For Windows, the
script to make this magic is `build.cmd` (requires a developer environment with CMake an C++ compiler such as the Visual Studio Developer Command Prompt). Copy the resulting binary into the respective RID folder above, and you're done.

## libmt32emu (Munt)

**Roland MT-32** emulation relies on `libmt32emu`, from the **Munt** project: <https://github.com/munt/munt>. Unlike Moira, it is **not** built as part of ASE: precompiled binaries are committed directly to the corresponding RID folder and ship alongside the emulator across all three platforms (Linux builds do not rely on system package managers either). MT-32 ROMs are not distributed: they are the proprietary property of Roland and must be provided by the user.

To build the library yourself, clone the Munt repository and run:

```
cd munt/mt32emu

cmake -B build -DCMAKE_BUILD_TYPE=Release \
  -Dlibmt32emu_SHARED=ON \
  -Dlibmt32emu_C_INTERFACE=ON \
  -Dlibmt32emu_CPP_INTERFACE=OFF \
  -Dlibmt32emu_WITH_INTERNAL_RESAMPLER=ON

cmake --build build -j$(sysctl -n hw.ncpu)
```

Copy the built library into `native` using the naming convention outlined in the table above.

## TinyFileDialogs

The NuGet package does not include a precompiled binary for ARM. This repository already provides the ARM build. If you need to recompile it from source, follow the instructions at <https://sourceforge.net/projects/tinyfiledialogs/>.