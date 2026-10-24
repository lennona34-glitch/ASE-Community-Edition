/*
 *
 * PPutnik's hard-disk adaptations in the game library.
 *
 * PPutnik adapted hundreds of floppy games to run from a hard disk: a folder of files
 * started by START.TOS or RUNME.TOS, loading through the hard disk driver instead of
 * the FDC. PPAdaptedGames.json lists them, the folder (or zip) name in "Path" and the
 * title in "Name", which is what the scraper looks up on ScreenScraper.
 *
 * Launching one builds a throwaway ACSI image around the game and powers the machine
 * on from it:
 *
 *  - the image is the same disk the Hard disk tab creates (AtariHdImage: one FAT16
 *    partition, the ASEHD driver in the reserved sectors), 4 MB unless the game needs
 *    more, with the game's folder copied to the root of C:;
 *  - the desktop's INF file at the root of C: carries a #Z line, the autostart TOS 1.04
 *    introduced, so the desktop runs the launcher as soon as it comes up. It is built
 *    from the default the ROM itself carries, so its format and language are the ones
 *    the loaded TOS expects;
 *  - C: has to be the boot drive for TOS to read that file, and nothing makes it so on
 *    its own: TOS never writes _bootdev ($446), and GEMDOS sets its current drive from
 *    _bootdev (Dsetdrv) *before* the floppy and DMA boot — $FC966E in TOS 1.04, $E09882
 *    in 1.62, $E150AC in 2.06. So the partition is flagged bootable (bit 7, AHDI's
 *    convention) and the ASEHD driver, from 2.1, makes a flagged partition the boot
 *    drive itself: _bootdev and Dsetdrv, right after mounting it.
 *
 * The game owns the boot: the image replaces the configured ACSI disk and drive A: is
 * emptied, since a floppy boot sector would run before the hard disk's. The session is
 * fixed at each power-on (PowerOn), so a reset restarts the game and putting a floppy
 * in A: ends it on the next one. The image itself is temporary; what keeps a game is a
 * snapshot, which carries the disk as it was (SaveState / LoadState) — the saved RAM
 * holds GEMDOS and driver buffers pointing into it, so nothing else would do.
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

using ASE.Models;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace ASE
{
    public static class HardDiskGames
    {
        /// <summary>The programs that start a PPutnik adaptation; every one has exactly one.</summary>
        static readonly string[] Launchers = { "START.TOS", "RUNME.TOS" };

        /// <summary>No adaptation is bigger than this, so it is the size every image gets
        /// unless a game proves otherwise (then the next size up that holds it).</summary>
        const int ImageMB = 4;
        const int MaxImageMB = 64;

        const string CatalogueFile = "PPAdaptedGames.json";

        // ==================== Catalogue ====================

        static List<PPAdaptedGame> _catalogue;

        /// <summary>Every adaptation PPAdaptedGames.json lists (empty when the file is missing
        /// or unreadable, which is reported once).</summary>
        public static IReadOnlyList<PPAdaptedGame> Catalogue
        {
            get
            {
                if (_catalogue != null)
                    return _catalogue;

                try
                {
                    string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, CatalogueFile));
                    _catalogue = JsonSerializer.Deserialize<List<PPAdaptedGame>>(json) ?? new();
                    _catalogue.RemoveAll(g => string.IsNullOrWhiteSpace(g.Path) || string.IsNullOrWhiteSpace(g.Name));
                }
                catch (Exception ex)
                {
                    ColoredConsole.WriteLine($"Library: could not read {CatalogueFile}, hard-disk games are not recognised: {ex.Message}");
                    _catalogue = new();
                }

                return _catalogue;
            }
        }

        /// <summary>
        /// The adaptations a library folder or zip holds, by its name (the "Path" of the
        /// catalogue; a zip without its extension), or an empty list. Usually one; a few dozen
        /// folders are listed under two or three names — alternative titles, or a bundle.
        /// Matching the name is not enough on its own: an ordinary floppy zip may happen to be
        /// called like an adaptation, so the entry must also contain a launcher.
        /// </summary>
        public static List<PPAdaptedGame> Identify(string libraryEntry)
        {
            string name = Directory.Exists(libraryEntry)
                ? Path.GetFileName(Path.TrimEndingDirectorySeparator(libraryEntry))
                : Path.GetExtension(libraryEntry).Equals(".zip", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileNameWithoutExtension(libraryEntry)
                    : null;

            if (string.IsNullOrEmpty(name))
                return new();

            var games = Catalogue.Where(g => g.Path.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();

            if (games.Count > 0 && !HasLauncher(libraryEntry))
                return new();

            return games;
        }

        static bool HasLauncher(string libraryEntry)
        {
            try
            {
                return FindLauncher(ReadSource(libraryEntry, withData: false)) != null;
            }
            catch
            {
                return false;   // an unreadable zip is not a game we can run
            }
        }

        // ==================== Session ====================

        sealed class Session
        {
            public string ImagePath;
            public string Title;
            public string Source;       // the library folder or zip, for the console and snapshots
        }

        static Session _pending;        // what the next power-on boots (UI thread)
        static Session _attached;       // what the running machine was powered on with
        static readonly List<string> _retired = new();

        /// <summary>True when the machine was powered on from a hard-disk game, whose image
        /// then takes the ACSI bus. Fixed until the next power-on.</summary>
        public static bool Active => _attached != null;

        /// <summary>The image of the game the machine was powered on with, or null.</summary>
        public static string ImagePath => _attached?.ImagePath;

        /// <summary>The title of the game the machine was powered on with, or null.</summary>
        public static string Title => _attached?.Title;

        /// <summary>
        /// Called from ASEMain.TurnOn before the disks attach: the game chosen by then is the
        /// one this power-on boots, and stays so until the next one whatever the UI does.
        /// </summary>
        public static void PowerOn()
        {
            _attached = _pending;
        }

        /// <summary>
        /// Called from ASEMain.TurnOn once the disks are attached: the images of games no
        /// longer in use can go now that the ACSI emulation has let go of them.
        /// </summary>
        public static void DeleteRetiredImages()
        {
            _retired.RemoveAll(path => path != _attached?.ImagePath && TryDelete(path));
        }

        /// <summary>The emulator is closing (ASEMain.Shutdown, after the ACSI image was closed).</summary>
        public static void Shutdown()
        {
            if (_attached != null) _retired.Add(_attached.ImagePath);
            if (_pending != null && _pending != _attached) _retired.Add(_pending.ImagePath);
            _attached = _pending = null;

            _retired.RemoveAll(TryDelete);
        }

        /// <summary>
        /// The next power-on boots the machine's own disks again: a floppy went into A:, or the
        /// hard disk configuration changed. The running game keeps its disk until then.
        /// </summary>
        public static void End()
        {
            if (_pending == null)
                return;

            _retired.Add(_pending.ImagePath);
            _pending = null;
        }

        /// <summary>
        /// Builds the image for the adaptation in <paramref name="source"/> (a folder or a zip)
        /// and makes it what the next power-on boots. The caller resets. <paramref name="warning"/>
        /// comes back non-null when the game will boot but not start on its own.
        /// </summary>
        public static bool Start(string source, string title, out string error, out string warning)
        {
            error = warning = null;

            byte[] rom = ASEMain._mem?.ROM;
            if (rom == null || rom.Length < 0x30)
            {
                error = "There is no TOS loaded. Choose one in the Configuration window first.";
                return false;
            }

            int tosVersion = (rom[2] << 8) | rom[3];
            bool emuTos = rom[0x2C] == 'E' && rom[0x2D] == 'T' && rom[0x2E] == 'O' && rom[0x2F] == 'S';

            // The ASEHD driver, and the desktop's autostart, both start at TOS 1.04
            if (!emuTos && tosVersion < 0x104)
            {
                error = $"Hard-disk games need TOS 1.04 or later, and TOS {tosVersion >> 8}.{tosVersion & 0xFF:X2} " +
                        "is loaded. Choose a newer one in the Configuration window.";
                return false;
            }

            if (!AtariHdDriver.Available)
            {
                error = $"This build cannot make a bootable hard disk: {AtariHdDriver.Unavailable}.";
                return false;
            }

            List<SourceEntry> entries;
            try
            {
                entries = ReadSource(source, withData: true);
            }
            catch (Exception ex)
            {
                error = $"Could not read '{Path.GetFileName(source)}': {ex.Message}";
                return false;
            }

            SourceEntry launcher = FindLauncher(entries);
            if (launcher == null)
            {
                error = $"'{Path.GetFileName(source)}' holds neither {string.Join(" nor ", Launchers)}.";
                return false;
            }

            // The folder holding the launcher becomes the root of C:, so the program finds its
            // files where it expects them whatever directory the desktop leaves it in.
            string root = ParentOf(launcher.Path);
            var game = entries
                .Where(e => root.Length == 0 || e.Path.StartsWith(root + "/", StringComparison.Ordinal))
                .Select(e => e with { Path = root.Length == 0 ? e.Path : e.Path.Substring(root.Length + 1) })
                .ToList();

            string launcherName = GemdosHD.HostNameToAtari(FileName(launcher.Path));

            // The desktop file that autostarts it, in the format the loaded TOS reads
            string infName = null;
            byte[] inf = emuTos ? null : BuildDesktopInf(rom, $@"C:\{launcherName}");

            if (inf != null)
                infName = tosVersion >= 0x200 ? "NEWDESK.INF" : "DESKTOP.INF";
            else
                warning = $"{(emuTos ? "EmuTOS" : "This TOS")} cannot be told to start the game on its own: " +
                          $"it boots to the desktop, where {launcherName} on drive C: starts it.";

            byte[] image = null;
            for (int mb = ImageMB; mb <= MaxImageMB && image == null; mb *= 2)
            {
                try
                {
                    image = BuildImage(mb, game, infName, inf);
                }
                catch (HardDiskFullException)
                {
                    // try the next size up
                }
                catch (Exception ex)
                {
                    error = $"Could not build the hard disk for '{title}': {ex.Message}";
                    return false;
                }
            }

            if (image == null)
            {
                error = $"'{title}' does not fit a {MaxImageMB} MB hard disk.";
                return false;
            }

            if (!Attach(title, source, image, out error))
                return false;

            ColoredConsole.WriteLine($"Library: hard-disk game [[green]]{title}[[/green]] — {game.Count(e => !e.IsFolder)} files " +
                $"from '{Path.GetFileName(source)}', {image.Length / (1024 * 1024)} MB image, " +
                $"{(inf != null ? $"{infName} starts C:\\{launcherName}" : "no autostart")}.",
                Config.ConfigOptions.DebugModes.Quiet);

            return true;
        }

        // ==================== Image ====================

        static byte[] BuildImage(int megabytes, List<SourceEntry> game, string infName, byte[] inf)
        {
            var image = new byte[megabytes * 1024 * 1024];
            using var stream = new MemoryStream(image, writable: true);

            // Flagged bootable: ASEHD makes it the boot drive, so the desktop reads its INF
            var layout = AtariHdImage.Format(stream, image.Length / Acsi.SectorSize, bootPartition: true);
            var writer = new FatImageWriter(image, layout);

            // Folders are made on first use, each after its parent (FolderOf recurses), so a game
            // listing a file before its folder, as zips may, still lands where it belongs
            var folders = new Dictionary<string, FatImageWriter.Folder>(StringComparer.Ordinal) { [""] = writer.Root };

            FatImageWriter.Folder FolderOf(string path, DateTime modified)
            {
                if (folders.TryGetValue(path, out var folder))
                    return folder;

                var parent = FolderOf(ParentOf(path), modified);
                folder = writer.AddFolder(parent, UniqueName(parent, GemdosHD.HostNameToAtari(FileName(path))), modified);
                folders[path] = folder;
                return folder;
            }

            if (inf != null)
                writer.AddFile(writer.Root, infName, inf, DateTime.Now);

            foreach (var entry in game.OrderBy(e => e.Path, StringComparer.Ordinal))
            {
                if (entry.IsFolder)
                {
                    FolderOf(entry.Path, entry.Modified);
                    continue;
                }

                var parent = FolderOf(ParentOf(entry.Path), entry.Modified);
                string name = GemdosHD.HostNameToAtari(FileName(entry.Path));

                // Ours must be the one TOS reads; a game shipping its own would have
                // pointed the desktop at nothing
                if (parent == writer.Root && name == infName)
                    continue;

                writer.AddFile(parent, UniqueName(parent, name), entry.Data, entry.Modified);
            }

            // Last, as for every image: until the driver is in, the disk does not boot
            if (!AtariHdDriver.Install(stream, layout.RootSector))
                throw new InvalidOperationException(AtariHdDriver.Unavailable ?? "the hard disk driver could not be installed");

            return image;
        }

        /// <summary>
        /// The 8.3 name an entry gets in its folder. Host names are already ST names in a
        /// PPutnik folder, so this is nearly always <paramref name="name"/> itself; a name that
        /// does not fit 8.3, or collides once uppercased, gets a ~N ordinal the way VFAT does.
        /// </summary>
        static string UniqueName(FatImageWriter.Folder folder, string name)
        {
            if (!folder.Contains(name))
                return name;

            int dot = name.LastIndexOf('.');
            string stem = dot > 0 ? name[..dot] : name;
            string ext = dot > 0 ? name[dot..] : "";

            for (int n = 1; ; n++)
            {
                string tail = $"~{n}";
                string candidate = stem[..Math.Min(stem.Length, 8 - tail.Length)] + tail + ext;
                if (!folder.Contains(candidate))
                    return candidate;
            }
        }

        /// <summary>
        /// The desktop's INF file with an autostart line for <paramref name="program"/>, built
        /// from the default the ROM carries — the file the desktop would write itself, in the
        /// format and language of this TOS. Null when the ROM has none (EmuTOS).
        /// The #Z line goes before #E, where TOS expects it ("#Z 00" a TOS program, "01" GEM:
        /// Hatari's inffile.c); TOS reads these fields within a limited distance of the start
        /// of the file, which is why the line is inserted rather than appended.
        /// </summary>
        static byte[] BuildDesktopInf(byte[] rom, string program)
        {
            for (int i = 0; i + 16 < rom.Length; i++)
            {
                // "#a" followed by six hex digits and the next line: the start of the template
                if (rom[i] != '#' || rom[i + 1] != 'a' || !IsHex(rom, i + 2, 6) ||
                    rom[i + 8] != '\r' || rom[i + 9] != '\n' || rom[i + 10] != '#' || rom[i + 11] != 'b')
                    continue;

                int end = Array.IndexOf(rom, (byte)0, i);
                if (end < 0)
                    return null;

                string template = Encoding.Latin1.GetString(rom, i, end - i);
                var lines = template.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
                while (lines.Count > 0 && lines[^1].Trim().Length == 0)
                    lines.RemoveAt(lines.Count - 1);

                int e = lines.FindIndex(l => l.StartsWith("#E"));
                if (e < 0)
                    return null;

                lines.Insert(e, $"#Z 00 {program}@ ");
                return Encoding.Latin1.GetBytes(string.Join("\r\n", lines) + "\r\n");
            }

            return null;
        }

        static bool IsHex(byte[] b, int offset, int count)
        {
            for (int i = 0; i < count; i++)
                if (!Uri.IsHexDigit((char)b[offset + i]))
                    return false;
            return true;
        }

        /// <summary>
        /// Writes <paramref name="image"/> to a temporary file and makes it what the next
        /// power-on boots, retiring whatever was pending before.
        /// </summary>
        static bool Attach(string title, string source, byte[] image, out string error)
        {
            error = null;
            string path;

            try
            {
                string dir = ImagesFolder;
                Directory.CreateDirectory(dir);
                DeleteOrphans(dir);

                // The process id leads the name, which is how DeleteOrphans tells whose it is
                path = Path.Combine(dir, $"ASE{Environment.ProcessId}-{SafeFileName(title)}-{Guid.NewGuid():N}"[..^24] + ".img");
                File.WriteAllBytes(path, image);
            }
            catch (Exception ex)
            {
                error = $"Could not write the hard disk image: {ex.Message}";
                return false;
            }

            End();
            _pending = new Session { ImagePath = path, Title = title, Source = source };
            return true;
        }

        // ==================== Snapshots ====================

        /// <summary>
        /// The "HDG " snapshot section, written while a game is attached: its title and source,
        /// then the disk as it stands, compressed — PPutnik's data is packed, the free space is
        /// not. A snapshot is how a hard-disk game is kept: the image file goes with the session.
        /// </summary>
        public static void SaveState(Snapshot.Writer w, byte[] disk, bool sourceNameOnly = false)
        {
            w.Str(_attached.Title);
            w.Str(sourceNameOnly ? Path.GetFileName(_attached.Source ?? "") : _attached.Source);
            w.U32((uint)disk.Length);

            using var packed = new MemoryStream();
            using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(disk, 0, disk.Length);

            w.Bytes(packed.ToArray());
        }

        /// <summary>
        /// Before a snapshot powers the machine on (ASEMain.StartWithSnapshot): the disk it was
        /// taken with becomes the session again, exactly as it was, so the restored RAM finds its
        /// buffers still true. A snapshot without one (<paramref name="section"/> null) ends any
        /// session — it is a machine with its own disks. Throws on a corrupt section, which the
        /// caller turns into a clean boot.
        /// </summary>
        public static void LoadState(byte[] section)
        {
            if (section == null)
            {
                End();
                return;
            }

            var r = new Snapshot.Reader(section);
            string title = r.Str();
            string source = r.Str();
            var disk = new byte[r.U32()];

            using (var z = new ZLibStream(new MemoryStream(r.Bytes(r.Remaining)), CompressionMode.Decompress))
                z.ReadExactly(disk);

            if (!Attach(title, source, disk, out string error))
                throw new IOException(error);

            ColoredConsole.WriteLine($"Snapshot: hard-disk game [[green]]{title}[[/green]] ({disk.Length / (1024 * 1024)} MB disk)",
                Config.ConfigOptions.DebugModes.Quiet);
        }

        // ==================== Reading the game ====================

        /// <summary>One file or folder of the game, with '/' separators.</summary>
        sealed record SourceEntry(string Path, bool IsFolder, byte[] Data, DateTime Modified);

        /// <summary>The folder's or zip's contents. Metadata only when <paramref name="withData"/>
        /// is false. Host clutter (dot files, __MACOSX) is left out: no ST program reads it.</summary>
        static List<SourceEntry> ReadSource(string source, bool withData)
        {
            var list = new List<SourceEntry>();

            if (Directory.Exists(source))
            {
                foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
                {
                    string rel = Relative(source, dir);
                    if (!IsClutter(rel))
                        list.Add(new SourceEntry(rel, true, null, Directory.GetLastWriteTime(dir)));
                }

                foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    string rel = Relative(source, file);
                    if (!IsClutter(rel))
                        list.Add(new SourceEntry(rel, false, withData ? File.ReadAllBytes(file) : null,
                                                 File.GetLastWriteTime(file)));
                }

                return list;
            }

            using var zip = ZipFile.OpenRead(source);

            foreach (var entry in zip.Entries)
            {
                string rel = entry.FullName.Replace('\\', '/').Trim('/');
                bool folder = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');

                if (rel.Length == 0 || IsClutter(rel))
                    continue;

                byte[] data = null;
                if (withData && !folder)
                {
                    using var s = entry.Open();
                    using var ms = new MemoryStream((int)entry.Length);
                    s.CopyTo(ms);
                    data = ms.ToArray();
                }

                list.Add(new SourceEntry(rel, folder, data, entry.LastWriteTime.LocalDateTime));
            }

            return list;
        }

        /// <summary>The launcher nearest the top; START.TOS before RUNME.TOS should one folder
        /// ever hold both.</summary>
        static SourceEntry FindLauncher(List<SourceEntry> entries) =>
            entries
                .Where(e => !e.IsFolder && Launchers.Contains(FileName(e.Path), StringComparer.OrdinalIgnoreCase))
                .OrderBy(e => e.Path.Count(c => c == '/'))
                .ThenBy(e => Array.FindIndex(Launchers, l => l.Equals(FileName(e.Path), StringComparison.OrdinalIgnoreCase)))
                .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

        static bool IsClutter(string rel) =>
            rel.Split('/').Any(part => part.StartsWith('.') || part.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase));

        static string Relative(string root, string path) =>
            Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/').Replace('\\', '/');

        static string ParentOf(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash < 0 ? "" : path[..slash];
        }

        static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

        // ==================== Temporary images ====================

        static string ImagesFolder => Path.Combine(Path.GetTempPath(), "ASE", "HardDiskGames");

        static string SafeFileName(string title)
        {
            var sb = new StringBuilder();
            foreach (char c in title.Take(32))
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.Length > 0 ? sb.ToString() : "game";
        }

        /// <summary>
        /// Images an earlier run left behind (a crash skips Shutdown). Each name carries the
        /// process that made it, and only those of processes no longer running go: another ASE
        /// open at the same time keeps its game.
        /// </summary>
        static void DeleteOrphans(string dir)
        {
            foreach (string file in Directory.EnumerateFiles(dir, "ASE*.img"))
            {
                string name = Path.GetFileName(file);
                int dash = name.IndexOf('-');
                if (dash < 4 || !int.TryParse(name[3..dash], out int pid) || pid == Environment.ProcessId)
                    continue;

                if (ProcessRunning(pid))
                    continue;

                TryDelete(file);
            }
        }

        static bool ProcessRunning(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        static bool TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                return true;
            }
            catch (Exception ex)
            {
                ColoredConsole.WriteLine($"Library: could not delete the temporary hard disk {path}: {ex.Message}",
                    Config.ConfigOptions.DebugModes.Information);
                return false;
            }
        }
    }
}
