
# ASE = Atari System Emulator

![Logo.](Images/logo.png "Logo.")

**Atari System Emulator** or **ASE** is an **Atari ST** emulator that emulates the **ST F/FM**, the **Mega ST** and the **STE**. It is written in C# and utilizes the great **Motorola 68000 Moira** emulator created by **Dr. Dirk Hoffmann**.

📖 **New to ASE? Read the [User Manual](https://github.com/thebitculture/ase/wiki).**

This emulator has been partly ported from an old emulator that I started writing in C and assembly language in the late 90s, although in this released version, there is a bit of everything. Most of it has been rewritten, and other parts have been ported from **Hatari**, one of the best **Atari ST** emulators currently available.

The initial idea behind this emulator was for it to serve as a simple example to experiment with and study how an emulator works, as I explain on my **YouTube** channel, _**The Bit Culture**_. However, with later updates it has gained more complexity and is now more than just an easy-to-understand emulator. Even so, I invite you to check out the video on YouTube and follow the project comments if you’re curious about how an emulator really works.

Check out my channel, we won't only talk about emulators there, but we'll also look at some hardware and classic games.

👉 https://youtube.com/@thebitculture?si=2s4M5Iu4QbIdq_hn


## Minimum Requirements

It runs on **Windows**, **Mac**, and should also work on **Linux**, although I haven't tested it. **ASE** will work with any **TOS** version from 1.00 to 1.04 on the ST/Mega models, and from 1.06 to 2.06 on the STE.

## Downloads

You can download the latest release ready for use from the releases page, or better yet, you can download the source code to compile and experiment yourself. To compile the source code, you will need to download **Moira** from **Dr. Dirk Hoffmann's** repository and copy only the 68k emulator into that directory, excluding the `MoiraConfig.h` configuration file. In the **ASE** **Moira** directory, you will find `Moira_dotnet.cpp` and `Moira_dotnet.h`, which are responsible for exposing Moira to the C# wrapper, as well as the files to compile **Moira** (`build.cmd` and `build.sh`), depending on the OS you use.

To compile **Moira** on **Windows**, you will need to have `cmake.exe` in your system path or open a **Visual Studio** developer console.

### Native libraries layout

The native libraries live under `ASE/native/`, organized **per .NET RID** (operating system + architecture). When publishing, `ASE.csproj` copies **only** the library matching the target RID next to the executable:

```
ASE/native/
  win-x64/      moira.dll, SDL2.dll, mt32emu-2.dll
  osx-x64/      moira.dylib, libSDL2.dylib, libmt32emu.2.dylib
  osx-arm64/    moira.dylib, libSDL2.dylib, libmt32emu.2.dylib
  linux-x64/    moira.so, libmt32emu.so.2
  linux-arm64/  moira.so, libmt32emu.so.2, libtinyfiledialogs.so
```

`SDL2` is bundled only on Windows and macOS; on **Linux** the distribution's own SDL2 is used. `libmt32emu` (see below) is committed **already compiled** for every RID, so it never has to be built as part of ASE. The build scripts place the compiled **Moira** library straight into the matching `native/<rid>/` folder:

- `build.cmd` / `build.sh` with no argument build for the current machine.
- To target another platform, pass its RID: `./build.sh osx-x64`, `./build.sh linux-arm64`, or `build.cmd ARM64` (cross-compiling requires the corresponding toolchain). See `ASE/native/README.md` for details.

Then publish for that RID, e.g. `dotnet publish -c Release -r linux-arm64 --self-contained`.

### Roland MT-32 emulation

The **Roland MT-32** (and its CM-32L / LAPC-I relatives) is the module a good number of Atari ST games and demos were composed for, through the MIDI ports every ST shipped with. ASE emulates the ST's MIDI interface and can either bridge it to the host's real MIDI ports (*System managed MIDI*) or play it through a built-in MT-32, whose synthesis comes from **Munt**'s `libmt32emu`:

👉 https://github.com/munt/munt

You don’t need to build this library; the binary is already included in this repository. However, if you still want to build it yourself, take a look at `ASE/native/README.md`.

`libmt32emu` is distributed under the **LGPL v2.1 or later**. The **MT-32 ROM images are not included**: just like TOS, they are copyrighted (by Roland) and have to be supplied by the user.

👉 ASE Releases: https://github.com/thebitculture/ase/releases/

👉 Moira Releases: https://github.com/dirkwhoffmann/Moira/releases

👉 Munt (libmt32emu): https://github.com/munt/munt

---

## Enhanced Fork Features

This repository includes custom enhancements designed for large retro collections and immersive play:

- 🎮 **Instant Random Game Launcher**:
  - Click the **`[ 🎲 Random Game ]`** button or press <kbd>Ctrl+R</kbd> / <kbd>F5</kbd> anywhere in the library to immediately boot into a random game from your collection without modal prompts.
- 📦 **Massive TOSEC & Multi-Disk Library Performance**:
  - High-speed directory and metadata caching (`.ase_library_cache.json`) allowing silky navigation across libraries with 10,000+ files.
  - Fast 300-games-per-page grid with pagination controls (`<< First`, `< Prev`, `Next >`, `Last >>`) and live collection counters.
  - Authentic 3.5" Atari ST floppy diskette placeholder styling with retro embossed labels for titles awaiting box art.
  - One-click TOSEC quick-selector button.
- 🖼️ **Libretro CDN & Internet Archive Cover Scraper**:
  - Direct, zero-configuration online artwork scraper connecting to Libretro's Atari ST CDN (over 1,000 authentic box arts, 1,800 title screens, and 4,000 snapshots) with Internet Archive fallback.
  - No ScreenScraper developer accounts, API keys, or registration required.
  - Smart fuzzy title matching (`CleanGameTitle`) that effortlessly pairs complex TOSEC dump codes (e.g. `(1992)(Codemasters)[cr Cynix]`) with clean box art titles.
  - Both bulk missing artwork scraping and quick per-page scraping supported.

---

## 🥽 Meta Quest (Quest 2 / 3 / Pro) Play Guide

Experience Atari ST gaming inside a virtual retro living room, curved arcade cabinet, or personal IMAX cinema on your Meta Quest headset!

### Setup via Virtual Desktop (Recommended)

1. **Launch Virtual Desktop Streamer** on your Windows PC and open the **Virtual Desktop** app on your Meta Quest.
2. Select an environment:
   - **Retro Cinema / Home Theater**: Sit in front of a giant cinema screen.
   - **The Void**: Float in pure darkness with zero distractions.
3. **Screen Tuning**:
   - In the Virtual Desktop overlay menu, enable **Curved Screen** (set curve to ~20-30%) to emulate the gentle curvature of an authentic 14" Atari SC1224 or Philips CM8833 color CRT monitor.
   - Set the refresh rate to **90Hz or 120Hz** for ultra-smooth 50Hz/60Hz PAL/NTSC Atari ST frame pacing.
4. **Controllers & Input**:
   - Pair an Xbox, PlayStation, or 8BitDo Bluetooth controller directly to your Quest or PC.
   - You can also keep your physical mechanical keyboard nearby for Atari ST typing, flight sims, and adventure games.
5. **Spatial Audio**:
   - Enjoy spatialized stereo Atari ST YM2149 chiptune sound and Roland MT-32 MIDI right through the Quest's built-in 3D spatial audio drivers.

### Setup via Meta Quest Link / Air Link / Immersed

1. Connect your headset to your PC via high-speed USB-C (Quest Link) or high-bandwidth 5GHz/6GHz Wi-Fi (Air Link).
2. Open the Desktop view from the Oculus Dash.
3. Launch ASE, switch to Fullscreen or borderless windowed mode, and pin the window in your VR space.

---

## 🍴 Forking and Contributing

If you wish to contribute to or fork this project:

1. Fork the upstream repository at [thebitculture/ase](https://github.com/thebitculture/ase).
2. Clone your fork locally:
   ```bash
   git clone https://github.com/<your-username>/ase.git
   ```
3. Add the upstream remote to stay up-to-date:
   ```bash
   git remote add upstream https://github.com/thebitculture/ase.git
   ```
4. Build with .NET 9.0 SDK:
   ```bash
   dotnet build -c Release
   ```

---

(C) _The Bit Culture_ - Public License Version 3
