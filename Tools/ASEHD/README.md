# ASEHD — ACSI hard disk driver

A self-contained hard disk driver for the Atari ST, written for images created by ASE.
It exists so an ASE disk image can be booted and used without a third-party driver, and 
so the whole path: image format, driver, installer, is readable in one place.

**Scope:** ACSI target 0, LUN 0, TOS 1.04 or later (1.06/1.62 are what ASE boots on an
STE). It does not scan the bus and does not pretend to be a general-purpose driver, but
since 2.0 it does mount **every** GEM/BGM partition a disk carries, including AHDI
extended (XGM) chains and ICD-style extra primaries, so disks partitioned by other tools
read fine.

## Files

The bootloader and the driver are separate sources, so the driver can grow
without fighting the 450 bytes a boot sector leaves:

- `BOOT.S` — the boot sector. Loads the resident driver from the reserved sectors and
  calls its init entry; that is all it does.
- `ASEHD.S` — the resident driver: partition table parsing, BPBs, the transfer engine.
- `build.cmd` — assembles both with **vasm** and copies the binaries into
  `ASE/Resources/`, where `ASE.csproj` embeds them.

**Nothing runs on the ST.** The assembled binaries live inside ASE as embedded resources
(`ASE/Resources/ASEHD.BOT` and `ASE/Resources/ASEHD.SYS`, wired up in `AtariHdDriver.cs`),
and ASE writes them to the image itself, both when creating a disk and through
*Reinstall driver* on an existing one. There is no installer program to build or maintain.
The resources are optional: a build without them works fine and simply creates unbootable
images, saying so on the console.

## Disk layout

```
sector 0          root sector: bootstrap + Atari partition table
sectors 1-31      resident driver (15.5 KB reserved; 2.0 needs ~7 sectors)
sector 32...      partition C:, FAT16 (FAT12 on very small images)
```

The reserved sectors were chosen over the other option (a driver file inside the FAT
file system) on purpose: the FAT can never allocate them, no desktop operation can
delete or move the driver, and *Reinstall driver* works on any image whose partition
starts at sector 32 which every ASE-created image does.

### Root sector (sector 0)

| Offset | Size | Contents |
|---|---|---|
| `$000` | 2 | `BRA.S` to the bootstrap |
| `$002` | 6 | `ASEHD2` signature |
| `$008` | 2 | driver length in sectors (written by the installer) |
| `$00A` | 4 | driver length in bytes (written by the installer) |
| `$00E`-`$1C1` | - | bootstrap code |
| `$1C2` | 4 | total sectors on the disk |
| `$1C6` | 48 | four primary partition entries: flag, `GEM`/`BGM`/`XGM` id, start, length |
| `$1F6` | 8 | bad sector list, unused |
| `$1FE` | 2 | checksum |

TOS executes this sector at boot **only** when its 256 big-endian words add up to `$1234`.
ASE leaves the checksum at zero when it creates an image, so a fresh disk never tries to
boot; ASE fixes it up as the last step of installing. That ordering is on purpose, if
an install fails half way, the disk simply does not boot, instead of booting into a driver
that was never fully written.

Note that ASE's *Boot from hard disk* switch works by invalidating that same checksum in
the copy handed to the DMA, so with it off the disk stays readable but does not boot.

### Resident driver (sectors 1..31)

| Offset | Size | Contents |
|---|---|---|
| `+0` | 4 | `BRA.W` to the init entry — the bootstrap jumps here |
| `+4` | 8 | `ASEHD2`,0,0 — verified by the bootstrap after loading, and by the installer |

That is the whole contract: unlike 1.1 there is no fixed entry table and no patched
geometry. The driver installs its own vectors PC-relatively and parses the partition
table itself, so the installer only ever writes lengths into the boot sector.

## Building

```
cd tools\asehd
build.cmd
```

`build.cmd` looks for `vasmm68k_mot` on the PATH and assembles both sources with
`-Fbin -no-opt`, checks the boot sector came out at exactly 512 bytes and the driver
fits the 31 reserved sectors, and copies the results to `ASE/Resources/`. Rebuild ASE
afterwards to embed them.

Things the sources depend on, checked by `AtariHdDriver.Validate` when ASE loads the
binaries:

- **`-no-opt` is not optional.** The driver must open with a 4-byte `BRA.W` ($60 $00)
  and the boot sector's header offsets must not move; an assembler shortening branches
  would shift both.
- **The code must stay fully PC-relative** — it runs wherever `Malloc` puts it, and
  nothing relocates it.
- **Reserved blocks are `dcb`, never `ds`.** An assembler may put `ds` in BSS, and the
  bootstrap only loads what the file actually contains — the sector buffer would then
  live in memory nobody reserved.
- **Hardware addresses are written sign-extended to 32 bits** (`$FFFF8604`, not
  `$FF8604`), which is what short absolute addressing needs.

## Installing

**Creating a disk.** Create the image from the *Hard disk* tab, attach it with *Boot from
hard disk* switched on, and reset. ASE writes the partition table, the file system and the
driver, and the disk boots itself with `C:` present.

**Putting the driver on a disk that already exists** — an image made by an older build, or
one currently using another vendor's driver: point the ACSI field at it and press
*Reinstall driver*. It reports what it found and asks before writing, because it replaces
whatever boot sector and driver the disk had. Only sector 0 and the reserved sectors are
rewritten, so the files in the partition are untouched. It refuses outright when the
partition starts too early for the driver to fit in front of it.

## How it works

**Boot** TOS reads sector 0 of ACSI target 0 during start-up and calls it. GEMDOS is
already running at that point, so the bootstrap can `Malloc` room for the driver, read it
off the disk with its own cut-down ACSI routine, verify the `ASEHD2` magic (so a disk
whose driver area was overwritten is not jumped into blind), and `JSR` to the init entry.
Everything else happens inside the driver:

1. capture `hdv_rw`/`hdv_bpb`/`hdv_mediach` (before installing over them — captured
   afterwards they would point back into the driver and every floppy call would loop);
2. read the root sector and collect every partition the tables describe;
3. for each one, read its FAT boot record and build the BPB; partitions with an
   unreadable or implausible file system are skipped, the rest still mount;
4. install the three vectors, publish the new drives in `_drvbits`, print the banner.

If nothing could be mounted the driver says so, installs nothing, and returns 0 — the
bootstrap then frees its memory and lets the machine boot from floppy as if the driver
had never been there.

**Boot drive** (2.1). A partition whose table flag has **bit 7** set — AHDI's "bootable"
flag — becomes the drive the machine boots from: the first such partition mounted is
written to `_bootdev` and made the current drive with `Dsetdrv`. Both are needed, because
TOS never sets `_bootdev` itself and GEMDOS takes its current drive from it *before* any
disk boots; with only the variable changed, the AUTO folder and the desktop's INF file
would still come from A:. A disk with no flagged partition is left alone and the machine
keeps booting from A:, which is what images created by ASE's *Create image* are (flag
`$01`). The library's hard-disk games are built with `$81`, so their `DESKTOP.INF` on C:
starts the game; a disk partitioned by AHDI or HDX usually flags C: the same way.

**Partitions** - three sources, mounted in this order:

- the four primary entries at `$1C6` (`GEM`/`BGM`);
- `XGM` entries open an AHDI extended chain: each extended root holds `GEM`/`BGM`
  entries **relative to that root** and possibly another `XGM` link **relative to the
  first extended root** (that asymmetry is AHDI's, not ours). Guarded against loops
  and dead links;
- the eight extra primary entries of the ICD format at `$156`.

Each mountable partition takes the next free drive letter from `C:` up. *Free* is read
from `_drvbits`, so letters already claimed — ASE's GEMDOS host drive publishes itself
before the boot sector runs — are skipped instead of collided with.

**Drives that are not ours** — `hdv_rw`, `hdv_bpb` and `hdv_mediach` are not hard disk
vectors: every drive goes through them, floppies included. Anything not addressed to a
mounted partition is handed to whoever owned the vector before — TOS itself for `A:` and
`B:`, another driver for anything it mounted. The hand-off is a `MOVE.L old,-(SP)` +
`RTS`, which leaves the caller's parameters exactly where the old routine expects them.

**Transfers** - the fixes that make large files, multi-file copies and PPutnik-style
hard disk games work:

- *Chunking.* `Rwabs` carries a 16-bit sector count but a class-0 ACSI command holds an
  8-bit one (and on this controller 0 means 0, not 256). 1.1 passed the count straight
  through, so any transfer of 256+ sectors was silently truncated — a Desktop copy of a
  big file, or a loader that `Fread`s a whole game, asks for hundreds of sectors in one
  call and got back a buffer with only its head filled in. 2.0 splits every transfer
  into commands of at most 255 sectors.
- *Odd buffers.* The ST DMA cannot land on an odd address, and GEMDOS passes user
  `Fread` buffers straight through. Odd transfers go one sector at a time through an
  internal bounce buffer, copied by the CPU.
- *Retries and error codes.* A failed command is retried (3 attempts; `Rwabs` bit 1
  disables that), and errors come back as real BIOS codes: `ESECNF` for out of range,
  `EREADF`/`EWRITF` for a transfer that kept failing, `EUNDEV` for a device nobody owns.
- *Logical sectors.* A BPB `recsiz` above 512 bytes (how AHDI/ICD format partitions
  beyond 32 MB without 32-bit sector numbers) is honoured: `Rwabs` then counts in
  logical sectors and the driver converts to physical ones.
- *Physical mode.* `Rwabs` bit 3 addresses physical units: device 2 is ACSI unit 0,
  served absolutely over the whole disk; floppies chain, other units answer `EUNDEV`.

The DMA sequence per command is unchanged from 1.1 and matches Hatari's `hdc.c` (which
is what ASE emulates): lock the VBL out with `flock`, load the address into
`$FF8609/0B/0D`, clear the FIFO by toggling the direction bit, set the sector count, feed
the packet through `$FF8604` — first byte with A1 low, the **last byte written with the
DMA already enabled** — wait for GPIP5 with a timeout, read the status byte, give the bus
back to the FDC (`$0080`). Past sector 2097151 (the 21-bit LBA of class 0) commands
switch to the class-1 `READ(10)`/`WRITE(10)` forms via the ICD `$1F` escape.

**BPB** — built once per partition at boot (a fixed disk's file system does not move).
The boot-record values are little-endian (it is a PC-format BPB); the partition length is
taken from the 32-bit field in the partition table, because the boot record's own 16-bit
field is exactly what limits an unaided TOS partition to 32 MB. Whether the FAT is 12- or
16-bit is not a setting but a consequence of the cluster count — 4085 and above is FAT16 —
so the driver derives it. What remains genuinely 16-bit is the cluster count itself, so
ASE picks the cluster size when it formats: 1 KB clusters on a 32 MB disk, 16 KB on a
512 MB one.

**Banner** — the driver announces itself and every drive it mounted:

```
ASEHD 2.1 - ACSI hard disk driver
Drive C: 512 MB
Drive D: 100 MB
Boot drive C:
```

The last line appears only when a flagged partition made its drive the boot drive.

If it cannot even be loaded the bootstrap says so in one line, because otherwise TOS just
carries on to the floppy and a machine that comes up without `C:` is a puzzle.

## Known limits

- No media change handling for mounted drives — correct for a fixed disk, wrong if you
  swap images while the machine runs. Reset after swapping. Floppy media changes are
  unaffected: `hdv_mediach` is passed on for every drive that is not ours.
- Requires TOS 1.04+ for the 32-bit sector number (`lrecno`); on older TOS it would work
  up to 32 MB per partition, but has not been tried there.
- One ACSI target. Partitions on a second image would need bus scanning and per-slot
  target numbers; the slot structure has room, the code does not do it.
- The driver can only be installed from ASE. An image used on real hardware would need
  the boot sector and the reserved sectors copied to the physical disk some other way.
