using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ASE.Services
{
    /// <summary>
    /// Handles direct execution of Atari ST programs (.PRG, .TOS, .TTP, .APP)
    /// by creating in-memory auto-booting FAT12 floppy disks, and auto-running
    /// executables from standard .ST disk images that boot into the desktop.
    /// </summary>
    public static class AtariExecutableLauncher
    {
        private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".prg", ".tos", ".ttp", ".app"
        };

        public static bool IsExecutable(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string ext = Path.GetExtension(path);
            return ExecutableExtensions.Contains(ext);
        }

        public static bool IsExecutableExtension(string ext)
        {
            if (string.IsNullOrWhiteSpace(ext)) return false;
            if (!ext.StartsWith('.')) ext = "." + ext;
            return ExecutableExtensions.Contains(ext);
        }

        public static volatile bool PendingDesktopAutoClick = false;
        private static int _autoClickCountdown = 0;
        private static int _autoClickStep = 0;

        public static void ArmDesktopAutoClick()
        {
            PendingDesktopAutoClick = true;
            _autoClickCountdown = 160; // ~3.2 seconds at 50Hz: gives GEM time to boot, draw desktop, open window and position mouse
            _autoClickStep = 0;
        }

        public static void CancelDesktopAutoClick()
        {
            if (PendingDesktopAutoClick)
            {
                PendingDesktopAutoClick = false;
                _autoClickCountdown = 0;
                _autoClickStep = 0;
            }
        }

        public static void ResetCountdownOnReset()
        {
            if (PendingDesktopAutoClick)
            {
                _autoClickCountdown = 160;
                _autoClickStep = 0;
            }
        }

        public static void StepAutoClick()
        {
            if (!PendingDesktopAutoClick) return;

            if (_autoClickCountdown > 0)
            {
                _autoClickCountdown--;
                return;
            }

            switch (_autoClickStep)
            {
                case 0:
                    // First click down
                    ACIA.MouseButtonChanged(left: true, pressed: true);
                    _autoClickStep++;
                    break;
                case 1:
                case 2:
                case 3:
                    // Hold button down (~60ms)
                    _autoClickStep++;
                    break;
                case 4:
                    // First click up
                    ACIA.MouseButtonChanged(left: true, pressed: false);
                    _autoClickStep++;
                    break;
                case 5:
                case 6:
                case 7:
                    // Delay between clicks (~60ms)
                    _autoClickStep++;
                    break;
                case 8:
                    // Second click down (double-click trigger)
                    ACIA.MouseButtonChanged(left: true, pressed: true);
                    _autoClickStep++;
                    break;
                case 9:
                case 10:
                case 11:
                    // Hold button down (~60ms)
                    _autoClickStep++;
                    break;
                case 12:
                    // Second click up
                    ACIA.MouseButtonChanged(left: true, pressed: false);
                    _autoClickStep++;
                    _autoClickCountdown = 50; // Safety retry delay
                    break;
                case 13:
                    // Retry click down
                    ACIA.MouseButtonChanged(left: true, pressed: true);
                    _autoClickStep++;
                    break;
                case 14:
                case 15:
                case 16:
                    _autoClickStep++;
                    break;
                case 17:
                    // Retry click up
                    ACIA.MouseButtonChanged(left: true, pressed: false);
                    _autoClickStep++;
                    break;
                case 18:
                case 19:
                case 20:
                    _autoClickStep++;
                    break;
                case 21:
                    // Retry second click down
                    ACIA.MouseButtonChanged(left: true, pressed: true);
                    _autoClickStep++;
                    break;
                case 22:
                case 23:
                case 24:
                    _autoClickStep++;
                    break;
                case 25:
                    // Retry second click up
                    ACIA.MouseButtonChanged(left: true, pressed: false);
                    PendingDesktopAutoClick = false;
                    _autoClickStep = 0;
                    ColoredConsole.WriteLine("[AtariExecutableLauncher] Dispatched auto-double-click for desktop program.", Config.ConfigOptions.DebugModes.Quiet);
                    break;
            }
        }

        public static string BuildDesktopInf(string fullExeName)
        {
            var sb = new StringBuilder();
            sb.Append("#E 18 12 \r\n");
            sb.Append("#W 00 00 00 07 4C 0C 00 A:\\*.*@\r\n");
            sb.Append("#W 00 00 02 0B 4C 09 00 @\r\n");
            sb.Append("#W 00 00 0A 0F 34 09 00 @\r\n");
            sb.Append("#W 00 00 0E 01 34 09 00 @\r\n");
            sb.Append("#M 00 00 00 FF A FLOPPY DISK@ @ \r\n");
            sb.Append("#M 00 01 00 FF B FLOPPY DISK@ @ \r\n");
            sb.Append("#T 00 03 02 FF   TRASH@ @ \r\n");
            sb.Append("#F FF 04   @ *.*@ \r\n");
            sb.Append("#D FF 01   @ *.*@ \r\n");
            sb.Append("#G 03 FF   *.APP@ @ \r\n");
            sb.Append("#G 03 FF   *.PRG@ @ \r\n");
            sb.Append("#P 03 FF   *.TTP@ @ \r\n");
            sb.Append("#F 03 04   *.TOS@ @ \r\n");
            sb.Append($"#Z 01 A:\\{fullExeName}@\r\n");
            return sb.ToString();
        }

        /// <summary>
        /// Creates an in-memory auto-booting Atari ST floppy disk image containing the specified executable.
        /// The program is placed in an AUTO folder (so TOS launches it before desktop) and configured in
        /// DESKTOP.INF (so GEM launches it if AES starts).
        /// </summary>
        public static byte[] CreateVirtualFloppy(string path, byte[] prgBytes, out FloppyImage.Configuration config)
        {
            config = null;
            if (prgBytes == null || prgBytes.Length == 0)
                return null;

            // Clean 8.3 filename
            string rawName = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
            var sb = new StringBuilder();
            foreach (char c in rawName)
            {
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')
                    sb.Append(c);
                if (sb.Length >= 8) break;
            }
            if (sb.Length == 0) sb.Append("GAME");
            string name8 = sb.ToString().PadRight(8, ' ').Substring(0, 8);

            string rawExt = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            if (rawExt.Length > 3) rawExt = rawExt.Substring(0, 3);
            if (rawExt.Length == 0) rawExt = "PRG";
            string ext3 = rawExt.PadRight(3, ' ').Substring(0, 3);

            // Determine geometry (720KB standard double-sided, or 1.44MB high-density if needed)
            int tracks = 80;
            int sides = 2;
            int spt = 9;
            int spc = 2; // sectors per cluster (1024 bytes)
            int sectorsPerFat = 5;
            int rootDirEntries = 112;
            byte mediaDescriptor = 0xF9;

            if (prgBytes.Length > 710 * 1024)
            {
                // High Density
                spt = 18;
                sectorsPerFat = 9;
                rootDirEntries = 224;
                mediaDescriptor = 0xF0;
            }

            int totalSectors = tracks * sides * spt;
            int sizeInBytes = totalSectors * 512;

            if (prgBytes.Length > sizeInBytes - (40 * 1024))
            {
                // Executable exceeds even 1.44MB floppy capacity
                return null;
            }

            config = new FloppyImage.Configuration
            {
                Sides = sides,
                Tracks = tracks,
                SectorsPerTrack = spt,
                SectorSize = 512
            };

            byte[] disk = new byte[sizeInBytes];

            // 1. Boot sector
            disk[0] = 0x60; disk[1] = 0x1C; // bra.s +28
            byte[] oem = Encoding.ASCII.GetBytes("ASE_ST  ");
            Array.Copy(oem, 0, disk, 2, 8);
            disk[8] = 0x12; disk[9] = 0x34; disk[10] = 0x56; // serial
            WriteLE16(disk, 11, 512); // bytes per sector
            disk[13] = (byte)spc; // sectors per cluster
            WriteLE16(disk, 14, 1); // reserved sectors
            disk[16] = 2; // number of FATs
            WriteLE16(disk, 17, rootDirEntries); // root entries
            WriteLE16(disk, 19, totalSectors); // total sectors
            disk[21] = mediaDescriptor; // media descriptor
            WriteLE16(disk, 22, sectorsPerFat); // sectors per FAT
            WriteLE16(disk, 24, spt); // sectors per track
            WriteLE16(disk, 26, sides); // sides
            WriteLE16(disk, 28, 0); // hidden sectors

            // 2. Initialize FATs
            int fat1Offset = 1 * 512;
            int fat2Offset = (1 + sectorsPerFat) * 512;
            disk[fat1Offset] = mediaDescriptor;
            disk[fat1Offset + 1] = 0xFF;
            disk[fat1Offset + 2] = 0xFF;
            disk[fat2Offset] = mediaDescriptor;
            disk[fat2Offset + 1] = 0xFF;
            disk[fat2Offset + 2] = 0xFF;

            // 3. Offsets for Root Dir & Data
            int rootDirSector = 1 + (2 * sectorsPerFat);
            int rootDirOffset = rootDirSector * 512;
            int rootDirBytes = rootDirEntries * 32;
            int dataStartSector = rootDirSector + (rootDirBytes / 512);
            int dataStartOffset = dataStartSector * 512;
            int clusterSize = spc * 512;

            // Cluster 2: AUTO directory
            // Clusters 3..(2 + fileClusters): PRG data
            // Cluster (3 + fileClusters): DESKTOP.INF
            int fileClusters = (prgBytes.Length + clusterSize - 1) / clusterSize;
            int desktopCluster = 3 + fileClusters;

            // Write FAT chains in FAT1 and FAT2
            WriteFat12(disk, fat1Offset, 2, 0xFFF);
            WriteFat12(disk, fat2Offset, 2, 0xFFF);

            for (int i = 0; i < fileClusters; i++)
            {
                int currentCluster = 3 + i;
                int nextCluster = (i == fileClusters - 1) ? 0xFFF : currentCluster + 1;
                WriteFat12(disk, fat1Offset, currentCluster, nextCluster);
                WriteFat12(disk, fat2Offset, currentCluster, nextCluster);
            }

            WriteFat12(disk, fat1Offset, desktopCluster, 0xFFF);
            WriteFat12(disk, fat2Offset, desktopCluster, 0xFFF);

            ushort dosTime = GetDosTime(DateTime.Now);
            ushort dosDate = GetDosDate(DateTime.Now);

            // 4. Root Directory Entries
            // Entry 0: AUTO folder
            WriteDirEntry(disk, rootDirOffset + 0 * 32, "AUTO    ", "   ", 0x10, 2, 0, dosTime, dosDate);
            // Entry 1: Program file in root
            WriteDirEntry(disk, rootDirOffset + 1 * 32, name8, ext3, 0x00, 3, (uint)prgBytes.Length, dosTime, dosDate);
            // Entry 2: DESKTOP.INF in root
            string fullExeName = $"{name8.TrimEnd()}.{ext3.TrimEnd()}";
            string desktopInf = BuildDesktopInf(fullExeName);
            byte[] desktopBytes = Encoding.ASCII.GetBytes(desktopInf);
            WriteDirEntry(disk, rootDirOffset + 2 * 32, "DESKTOP ", "INF", 0x00, (ushort)desktopCluster, (uint)desktopBytes.Length, dosTime, dosDate);

            // 5. Cluster 2 Data (AUTO directory table)
            int autoDirOffset = dataStartOffset + (2 - 2) * clusterSize;
            WriteDirEntry(disk, autoDirOffset + 0 * 32, ".       ", "   ", 0x10, 2, 0, dosTime, dosDate);
            WriteDirEntry(disk, autoDirOffset + 1 * 32, "..      ", "   ", 0x10, 0, 0, dosTime, dosDate);
            WriteDirEntry(disk, autoDirOffset + 2 * 32, name8, ext3, 0x00, 3, (uint)prgBytes.Length, dosTime, dosDate);

            // 6. Program Data
            int prgDataOffset = dataStartOffset + (3 - 2) * clusterSize;
            Array.Copy(prgBytes, 0, disk, prgDataOffset, prgBytes.Length);

            // 7. DESKTOP.INF Data
            int desktopDataOffset = dataStartOffset + (desktopCluster - 2) * clusterSize;
            Array.Copy(desktopBytes, 0, disk, desktopDataOffset, desktopBytes.Length);

            return disk;
        }

        /// <summary>
        /// Inspects a loaded in-memory .ST image. If it is non-bootable, lacks an AUTO folder or DESKTOP.INF,
        /// but contains executable .PRG/.TOS files in the root, it injects DESKTOP.INF to auto-launch the game.
        /// </summary>
        public static bool TryInjectAutoRun(byte[] diskImage, out string launchedProgram)
        {
            launchedProgram = null;
            if (diskImage == null || diskImage.Length < 512 * 18)
                return false;

            // Check if bootsector is already executable (checksum == 0x1234)
            ushort sum = 0;
            for (int i = 0; i < 512; i += 2)
                sum += (ushort)((diskImage[i] << 8) | diskImage[i + 1]);

            if (sum == 0x1234)
            {
                // Custom executable bootloader present, leave it untouched
                return false;
            }

            int bps = ReadLE16(diskImage, 11);
            int spc = diskImage[13];
            int resSectors = ReadLE16(diskImage, 14);
            int fatCount = diskImage[16];
            int rootDirEntries = ReadLE16(diskImage, 17);
            int sectorsPerFat = ReadLE16(diskImage, 22);

            if (bps != 512 || spc < 1 || fatCount < 1 || rootDirEntries < 16 || sectorsPerFat < 1)
                return false;

            int rootDirSector = resSectors + (fatCount * sectorsPerFat);
            int rootDirOffset = rootDirSector * bps;
            int rootDirBytes = rootDirEntries * 32;
            int dataStartSector = rootDirSector + (rootDirBytes / bps);
            int dataStartOffset = dataStartSector * bps;
            int clusterSize = spc * bps;

            if (rootDirOffset + rootDirBytes > diskImage.Length || dataStartOffset >= diskImage.Length)
                return false;

            bool hasAuto = false;
            bool hasDesktopInf = false;
            int firstFreeEntryOffset = -1;

            var candidates = new List<(string name8, string ext3, uint size, ushort cluster)>();

            for (int off = rootDirOffset; off < rootDirOffset + rootDirBytes; off += 32)
            {
                byte firstByte = diskImage[off];
                if (firstByte == 0x00)
                {
                    if (firstFreeEntryOffset == -1) firstFreeEntryOffset = off;
                    break;
                }
                if (firstByte == 0xE5)
                {
                    if (firstFreeEntryOffset == -1) firstFreeEntryOffset = off;
                    continue;
                }

                byte attr = diskImage[off + 11];
                if ((attr & 0x08) != 0) continue; // Volume label

                string name = Encoding.ASCII.GetString(diskImage, off, 8).TrimEnd();
                string ext = Encoding.ASCII.GetString(diskImage, off + 8, 3).TrimEnd();

                if ((attr & 0x10) != 0 && name.Equals("AUTO", StringComparison.OrdinalIgnoreCase))
                    hasAuto = true;

                if (name.Equals("DESKTOP", StringComparison.OrdinalIgnoreCase) && ext.Equals("INF", StringComparison.OrdinalIgnoreCase))
                    hasDesktopInf = true;

                if (name.Equals("NEWDESK", StringComparison.OrdinalIgnoreCase) && ext.Equals("INF", StringComparison.OrdinalIgnoreCase))
                    hasDesktopInf = true;

                if ((attr & 0x10) == 0 && (ext.Equals("PRG", StringComparison.OrdinalIgnoreCase) ||
                                           ext.Equals("TOS", StringComparison.OrdinalIgnoreCase) ||
                                           ext.Equals("TTP", StringComparison.OrdinalIgnoreCase) ||
                                           ext.Equals("APP", StringComparison.OrdinalIgnoreCase)))
                {
                    uint size = ReadLE32(diskImage, off + 28);
                    ushort cluster = (ushort)ReadLE16(diskImage, off + 26);
                    candidates.Add((name, ext, size, cluster));
                }
            }

            // If it already has an AUTO folder or has no programs, don't modify
            if (hasAuto || candidates.Count == 0)
                return false;

            // Pick the best candidate executable
            (string name8, string ext3, uint size, ushort cluster) best = candidates[0];
            string[] preferredNames = { "GAME", "START", "RUN", "MAIN", "PLAY", "AUTO" };

            foreach (var cand in candidates)
            {
                foreach (string pref in preferredNames)
                {
                    if (cand.name8.Equals(pref, StringComparison.OrdinalIgnoreCase))
                    {
                        best = cand;
                        goto FoundPreferred;
                    }
                }
            }

            // Fallback: pick the largest executable file
            foreach (var cand in candidates)
            {
                if (cand.size > best.size)
                    best = cand;
            }

        FoundPreferred:
            string fullExeName = $"{best.name8}.{best.ext3}";

            if (hasDesktopInf)
            {
                ArmDesktopAutoClick();
                launchedProgram = fullExeName;
                return true;
            }

            if (firstFreeEntryOffset == -1)
                return false;

            // Find a free cluster in FAT1
            int fat1Offset = resSectors * bps;
            int fat2Offset = (resSectors + sectorsPerFat) * bps;
            int maxClusters = (diskImage.Length - dataStartOffset) / clusterSize + 2;
            int freeCluster = -1;

            for (int c = 2; c < maxClusters; c++)
            {
                if (ReadFat12(diskImage, fat1Offset, c) == 0)
                {
                    freeCluster = c;
                    break;
                }
            }

            if (freeCluster == -1) return false;

            string desktopInf = BuildDesktopInf(fullExeName);
            byte[] desktopBytes = Encoding.ASCII.GetBytes(desktopInf);

            int clusterOffset = dataStartOffset + (freeCluster - 2) * clusterSize;
            if (clusterOffset + desktopBytes.Length > diskImage.Length)
                return false;

            Array.Copy(desktopBytes, 0, diskImage, clusterOffset, desktopBytes.Length);

            WriteFat12(diskImage, fat1Offset, freeCluster, 0xFFF);
            WriteFat12(diskImage, fat2Offset, freeCluster, 0xFFF);

            ushort dosTime = GetDosTime(DateTime.Now);
            ushort dosDate = GetDosDate(DateTime.Now);
            WriteDirEntry(diskImage, firstFreeEntryOffset, "DESKTOP ", "INF", 0x00, (ushort)freeCluster, (uint)desktopBytes.Length, dosTime, dosDate);

            ArmDesktopAutoClick();
            launchedProgram = fullExeName;
            return true;
        }

        private static void WriteDirEntry(byte[] disk, int offset, string name8, string ext3, byte attr, ushort cluster, uint size, ushort time, ushort date)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(name8.PadRight(8, ' ').Substring(0, 8));
            byte[] extBytes = Encoding.ASCII.GetBytes(ext3.PadRight(3, ' ').Substring(0, 3));

            Array.Copy(nameBytes, 0, disk, offset, 8);
            Array.Copy(extBytes, 0, disk, offset + 8, 3);
            disk[offset + 11] = attr;
            Array.Clear(disk, offset + 12, 10);
            WriteLE16(disk, offset + 22, time);
            WriteLE16(disk, offset + 24, date);
            WriteLE16(disk, offset + 26, cluster);
            WriteLE32(disk, offset + 28, size);
        }

        private static int ReadFat12(byte[] disk, int fatOffset, int cluster)
        {
            int idx = fatOffset + (cluster * 3) / 2;
            if (idx + 1 >= disk.Length) return 0xFFF;

            if ((cluster & 1) == 0)
                return disk[idx] | ((disk[idx + 1] & 0x0F) << 8);
            else
                return (disk[idx] >> 4) | (disk[idx + 1] << 4);
        }

        private static void WriteFat12(byte[] disk, int fatOffset, int cluster, int val)
        {
            int idx = fatOffset + (cluster * 3) / 2;
            if (idx + 1 >= disk.Length) return;

            if ((cluster & 1) == 0)
            {
                disk[idx] = (byte)(val & 0xFF);
                disk[idx + 1] = (byte)((disk[idx + 1] & 0xF0) | ((val >> 8) & 0x0F));
            }
            else
            {
                disk[idx] = (byte)((disk[idx] & 0x0F) | ((val << 4) & 0xF0));
                disk[idx + 1] = (byte)((val >> 4) & 0xFF);
            }
        }

        private static int ReadLE16(byte[] data, int offset) =>
            data[offset] | (data[offset + 1] << 8);

        private static uint ReadLE32(byte[] data, int offset) =>
            (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

        private static void WriteLE16(byte[] data, int offset, int val)
        {
            data[offset] = (byte)(val & 0xFF);
            data[offset + 1] = (byte)((val >> 8) & 0xFF);
        }

        private static void WriteLE32(byte[] data, int offset, uint val)
        {
            data[offset] = (byte)(val & 0xFF);
            data[offset + 1] = (byte)((val >> 8) & 0xFF);
            data[offset + 2] = (byte)((val >> 16) & 0xFF);
            data[offset + 3] = (byte)((val >> 24) & 0xFF);
        }

        private static ushort GetDosTime(DateTime dt) =>
            (ushort)((dt.Hour << 11) | (dt.Minute << 5) | (dt.Second >> 1));

        private static ushort GetDosDate(DateTime dt) =>
            (ushort)(((Math.Max(1980, dt.Year) - 1980) << 9) | (dt.Month << 5) | dt.Day);
    }
}
