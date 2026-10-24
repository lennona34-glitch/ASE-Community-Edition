<p align="center">
  <img src="Images/logo.png" alt="Atari System Emulator" width="516">
</p>

<p align="center">
  An Atari ST emulator for playing, exploring, and reimagining your favourite ST games.
</p>

<p align="center">
  <a href="https://github.com/thebitculture/ase/releases">Download</a> ·
  <a href="https://github.com/thebitculture/ase/wiki">Wiki</a> ·
  <a href="#building-from-source">Build from source</a>
</p>

ASE emulates the Atari ST F/FM, Mega ST, and STE. It's written in C# and uses [Moira](https://github.com/dirkwhoffmann/Moira), Dr. Dirk Hoffmann's Motorola 68000 emulator. You can run ST software as it was, or use a few conveniences that weren't around on the original machines.

<p align="center">
  <img src="Images/ASE.png" alt="ASE running Rainbow Islands" width="804"><br>
  <sub>Rainbow Islands running in ASE.</sub>
</p>

## Playing and browsing

The library gives your games and applications a place to live beyond a list of filenames. You can browse cover art and see each game's metadata, screenshots, and video previews.

<p align="center">
  <img src="Images/library.png" alt="ASE library showing game cover art" width="800"><br>
  <sub>Browse the collection by its cover art.</sub>
</p>

Save states let you stop and come back later. **Time warp** keeps a short history of play, so you can go back to a moment before a missed jump and carry on from there.

<p align="center">
  <img src="Images/timewarp.png" alt="Time warp window showing recent moments of play" width="764"><br>
  <sub>Pick a moment from the last 60 seconds of play.</sub>
</p>

## Playing together

Online play lets you share one running game over a local network or the internet. For games that used two STs, ASE can also connect their emulated MIDI or null modem ports. This part is still in preview, so expect a few rough edges.

<p align="center">
  <img src="Images/netplay.png" alt="Online play connection options" width="644"><br>
  <sub>Share a game, or link two emulated STs by MIDI or null modem.</sub>
</p>

## Sound and MIDI

MIDI can go to your system's MIDI ports or to the built-in MT-32 emulation, powered by [Munt](https://github.com/munt/munt). The MT-32 Toolbox also lets you assign an MT-32 instrument to each of the Atari ST's three Yamaha YM2149 sound channels. You can save those choices for a game and hear its original sound voices played with different instruments.

<p align="center">
  <img src="Images/mt32toolbox.png" alt="MT-32 Toolbox with instrument choices for the three YM sound channels" width="399"><br>
  <sub>Map each YM voice to an MT-32 instrument.</sub>
</p>

## Looking under the hood

The built-in debugger lets you inspect the CPU and memory. Its bitmap explorer reads graphics straight from emulated RAM, which is handy when you want to see how a game puts an image together.

<p align="center">
  <img src="Images/bitmapexplorer.png" alt="Bitmap explorer showing graphics in emulated memory" width="800"><br>
  <sub>Graphics in memory, viewed with the machine's current palette.</sub>
</p>

The [wiki](https://github.com/thebitculture/ase/wiki) has more detail on the emulator and its features.

## Getting started

Download a ready-to-run build from the [releases page](https://github.com/thebitculture/ase/releases/). ASE runs on Windows and macOS. Linux builds are available, although I haven't tested Linux myself.

ASE includes [EmuTOS 1.4](https://emutos.sourceforge.io), so it can boot without a TOS ROM of your own. For the best compatibility, use a TOS image dumped from your own machine: versions 1.00–1.04 for ST/Mega ST, or 1.06–2.06 for STE.

MT-32 ROM images aren't included; you'll need to provide those yourself if you want to use the built-in MT-32 emulation or the Toolbox.

## Building from source

You'll need the .NET 10 SDK, CMake, and a C++ compiler with C++20 support. The ASE repository includes the Moira configuration and C# wrapper, but you'll need to get the Moira emulator source separately:

1. Download or clone [Dr. Dirk Hoffmann's Moira repository](https://github.com/dirkwhoffmann/Moira) somewhere outside the ASE checkout.
2. Copy the contents of its `Moira/` subfolder into ASE's `Moira/` folder. **Keep ASE's `MoiraConfig.h`; do not overwrite it with the upstream file.** Leave ASE's `Moira_dotnet.cpp`, `Moira_dotnet.h`, `CMakeLists.txt`, and build scripts in place too.
3. Build the native Moira library for your machine. On Windows, run this from a Visual Studio Developer Command Prompt with CMake available:

   ```bat
   cd Moira
   build.cmd
   cd ..
   ```

   On macOS or Linux, use:

   ```sh
   cd Moira
   ./build.sh
   cd ..
   ```

The scripts put the resulting library in the matching `ASE/native/<rid>/` folder. If you're building for another platform or architecture, pass its RID to `build.sh` (for example, `./build.sh linux-arm64`) or use `build.cmd ARM64` on Windows. Cross-compiling also requires the appropriate toolchain.

With Moira built, run this from the ASE repository root:

```sh
dotnet build ASE/ASE.csproj
```

To publish, use the same RID as the Moira library you built. For example:

```sh
dotnet publish ASE/ASE.csproj -c Release -r linux-arm64 --self-contained
```

The other native libraries, including Munt's `libmt32emu`, are already provided under `ASE/native/`. SDL2 is bundled on Windows and macOS; on Linux, install it through your distribution.

## About the project

I started an earlier version of ASE in C and assembly language in the late 1990s. The current emulator mixes ideas from that project with mostly new code, plus some work adapted from Hatari and the MiSTer Atari ST core.

ASE began as a way to learn how emulators work and share what I found. I talk through that process, along with retro hardware and games, on [The Bit Culture](https://youtube.com/@thebitculture) YouTube channel.

Thanks to the people behind [Moira](https://github.com/dirkwhoffmann/Moira), [Munt](https://github.com/munt/munt), and [EmuTOS](https://emutos.sourceforge.io). EmuTOS ships with its own [licence](ASE/emutos-license.txt) and [source information](ASE/emutos.txt); Munt's `libmt32emu` is distributed under LGPL 2.1 or later.

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
ASE is © The Bit Culture and contributors. Licensed under the [GNU GPL v3](LICENSE).
