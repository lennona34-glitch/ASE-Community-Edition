using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using static ASE.Config;

namespace ASE.Services
{
    /// <summary>
    /// Automatically detects whether a disk image or game requires an Original Atari ST
    /// or Atari STE machine model, and resolves the matching TOS BIOS ROM image.
    /// Handles TOSEC folder conventions ([ST], [STX], [STE], Public Domain) as well
    /// as game title / archive metadata tags.
    /// </summary>
    public static class MachineModelSelector
    {
        public class ModelDecision
        {
            public ConfigOptions.STModels TargetModel { get; set; } = ConfigOptions.STModels.ST;
            public string TosPath { get; set; } = string.Empty;
            public string Reason { get; set; } = string.Empty;
            public bool Changed { get; set; }
        }

        /// <summary>
        /// Detects model and TOS for the specified image, and applies them to RunninConfig if changed.
        /// </summary>
        public static ModelDecision DetectAndApply(string imagePath, Models.LibraryItem libraryItem = null)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
                return new ModelDecision { TargetModel = ConfigOptions.RunninConfig.STModel, TosPath = ConfigOptions.RunninConfig.TOSPath };

            if (!ConfigOptions.RunninConfig.AutoDetectModelAndTos)
            {
                return new ModelDecision
                {
                    TargetModel = ConfigOptions.RunninConfig.STModel,
                    TosPath = ConfigOptions.RunninConfig.TOSPath,
                    Reason = "Auto-detection disabled"
                };
            }

            var decision = Detect(imagePath, libraryItem);
            if (decision == null || string.IsNullOrEmpty(decision.TosPath))
                return decision ?? new ModelDecision();

            bool modelChanged = ConfigOptions.RunninConfig.STModel != decision.TargetModel;
            bool tosChanged = !string.Equals(ConfigOptions.RunninConfig.TOSPath, decision.TosPath, StringComparison.OrdinalIgnoreCase);

            if (modelChanged || tosChanged)
            {
                ConfigOptions.RunninConfig.STModel = decision.TargetModel;
                ConfigOptions.RunninConfig.TOSPath = decision.TosPath;
                decision.Changed = true;

                ColoredConsole.WriteLine($"[MachineModelSelector] Switched machine to [[green]]{decision.TargetModel}[[/green]] with BIOS [[yellow]]{Path.GetFileName(decision.TosPath)}[[/yellow]] ({decision.Reason})", ConfigOptions.DebugModes.Quiet);

                try
                {
                    var cfg = new Config();
                    cfg.DumpJsonConfig();
                }
                catch
                {
                    // Non-fatal if config save fails
                }
            }

            return decision;
        }

        /// <summary>
        /// Analyzes path, filename, and library item to determine the best ST model and BIOS.
        /// </summary>
        public static ModelDecision Detect(string imagePath, Models.LibraryItem libraryItem = null)
        {
            bool isSte = false;
            string reason = "";

            // 1. Check directory path segments
            string normalizedPath = imagePath.Replace('/', '\\');
            string[] segments = normalizedPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);

            // Check if any folder segment indicates STE
            foreach (var seg in segments)
            {
                string s = seg.Trim();
                if (s.Contains("[STE]", StringComparison.OrdinalIgnoreCase) ||
                    s.Contains("(STE)", StringComparison.OrdinalIgnoreCase) ||
                    s.Contains("Atari STE", StringComparison.OrdinalIgnoreCase) ||
                    s.Contains("extra ST", StringComparison.OrdinalIgnoreCase) ||
                    s.Contains("extra-ST", StringComparison.OrdinalIgnoreCase) ||
                    s.Contains("extraST", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s, "STE", StringComparison.OrdinalIgnoreCase))
                {
                    isSte = true;
                    reason = $"Folder '{s}' specifies Atari STE";
                    break;
                }
            }

            // 2. Check filename and inner zip filename
            string fileName = Path.GetFileName(imagePath);
            string innerFileName = "";
            if (imagePath.Contains('|'))
            {
                innerFileName = imagePath.Substring(imagePath.IndexOf('|') + 1);
            }

            if (!isSte)
            {
                if (IsSteTagged(fileName, out string tag))
                {
                    isSte = true;
                    reason = $"File tag '{tag}' indicates Atari STE";
                }
                else if (!string.IsNullOrEmpty(innerFileName) && IsSteTagged(innerFileName, out string innerTag))
                {
                    isSte = true;
                    reason = $"Inner file tag '{innerTag}' indicates Atari STE";
                }
            }

            // 3. Check library item metadata if available
            if (!isSte && libraryItem != null)
            {
                if (!string.IsNullOrEmpty(libraryItem.Filename) && IsSteTagged(libraryItem.Filename, out string libTag))
                {
                    isSte = true;
                    reason = $"Library tag '{libTag}' indicates Atari STE";
                }
                else if (libraryItem.Synopsis != null)
                {
                    foreach (var syn in libraryItem.Synopsis)
                    {
                        if (!string.IsNullOrEmpty(syn.Text) &&
                            (syn.Text.Contains("STE only", StringComparison.OrdinalIgnoreCase) ||
                             syn.Text.Contains("STE Enhanced", StringComparison.OrdinalIgnoreCase) ||
                             syn.Text.Contains("Atari STE only", StringComparison.OrdinalIgnoreCase)))
                        {
                            isSte = true;
                            reason = "Library metadata specifies Atari STE";
                            break;
                        }
                    }
                }
            }

            // 4. If not STE, determine ST reason
            if (!isSte)
            {
                foreach (var seg in segments)
                {
                    string s = seg.Trim();
                    if (s.Contains("[ST]", StringComparison.OrdinalIgnoreCase))
                    {
                        reason = "TOSEC [ST] folder (Original Atari ST)";
                        break;
                    }
                    if (s.Contains("[STX]", StringComparison.OrdinalIgnoreCase))
                    {
                        reason = "TOSEC [STX] retail dump (Original Atari ST)";
                        break;
                    }
                    if (s.Contains("Public Domain", StringComparison.OrdinalIgnoreCase))
                    {
                        reason = "Public Domain (Original Atari ST)";
                        break;
                    }
                }

                if (string.IsNullOrEmpty(reason))
                    reason = "Standard Atari ST floppy";
            }

            var targetModel = isSte ? ConfigOptions.STModels.STE : ConfigOptions.STModels.ST;
            string bestTos = FindBestTosRom(targetModel);

            return new ModelDecision
            {
                TargetModel = targetModel,
                TosPath = bestTos,
                Reason = reason
            };
        }

        private static bool IsSteTagged(string text, out string matchedTag)
        {
            matchedTag = "";
            if (string.IsNullOrWhiteSpace(text)) return false;

            if (text.Contains("(STE)", StringComparison.OrdinalIgnoreCase))
            {
                matchedTag = "(STE)";
                return true;
            }
            if (text.Contains("[STE]", StringComparison.OrdinalIgnoreCase))
            {
                matchedTag = "[STE]";
                return true;
            }
            if (text.Contains("(Atari STE)", StringComparison.OrdinalIgnoreCase))
            {
                matchedTag = "(Atari STE)";
                return true;
            }
            if (text.Contains("STE only", StringComparison.OrdinalIgnoreCase))
            {
                matchedTag = "STE only";
                return true;
            }
            if (text.Contains("STE Enhanced", StringComparison.OrdinalIgnoreCase))
            {
                matchedTag = "STE Enhanced";
                return true;
            }
            if (Regex.IsMatch(text, @"\b_STE\b", RegexOptions.IgnoreCase))
            {
                matchedTag = "_STE";
                return true;
            }
            return false;
        }

        /// <summary>
        /// Finds the best matching TOS ROM file for the target model.
        /// ST requires a 192 KB image (e.g. TOS 1.04 UK).
        /// STE requires a 256 KB image (e.g. TOS 1.62 UK or TOS 2.06 UK).
        /// </summary>
        public static string FindBestTosRom(ConfigOptions.STModels model)
        {
            int targetSize = (model == ConfigOptions.STModels.STE) ? (256 * 1024) : (192 * 1024);

            // Check if current configured TOS matches the target size and exists
            string currentTos = ConfigOptions.RunninConfig.TOSPath;
            if (!string.IsNullOrEmpty(currentTos) && File.Exists(currentTos))
            {
                long len = new FileInfo(currentTos).Length;
                if (len == targetSize)
                {
                    // Current TOS matches the required size!
                    // If switching to ST, verify whether current is 1.04 or valid 192KB
                    return currentTos;
                }
            }

            var candidateDirs = GetCandidateTosDirectories();

            string[] preferredFilenames;
            if (model == ConfigOptions.STModels.STE)
            {
                preferredFilenames = new[]
                {
                    "tos162uk.rom", "tos162uk.img", "tos162.rom", "tos162.img",
                    "TOS206UK.IMG", "tos206uk.img", "tos206uk.rom", "tos206.img", "tos206.rom",
                    "tos106uk.rom", "tos106.rom", "tos106.img"
                };
            }
            else
            {
                preferredFilenames = new[]
                {
                    "tos104uk.img", "tos104uk.rom", "tos104.img", "tos104.rom",
                    "tos102uk.img", "tos102uk.rom", "tos102.img", "tos102.rom",
                    "tos100uk.img", "tos100.img"
                };
            }

            // 1. Check for preferred exact filenames
            foreach (var dir in candidateDirs)
            {
                foreach (var fn in preferredFilenames)
                {
                    string candidate = Path.Combine(dir, fn);
                    if (File.Exists(candidate))
                    {
                        long len = new FileInfo(candidate).Length;
                        if (len == targetSize)
                            return candidate;
                    }
                }
            }

            // 2. Scan directories for any file with matching size and .img or .rom extension
            foreach (var dir in candidateDirs)
            {
                try
                {
                    var files = Directory.GetFiles(dir, "*.*");
                    foreach (var file in files)
                    {
                        string ext = Path.GetExtension(file).ToLowerInvariant();
                        if (ext == ".img" || ext == ".rom" || ext == ".tos")
                        {
                            long len = new FileInfo(file).Length;
                            if (len == targetSize)
                                return file;
                        }
                    }
                }
                catch
                {
                    // Ignore inaccessible directory
                }
            }

            // Fallback to whatever currentTos is
            return currentTos;
        }

        private static List<string> GetCandidateTosDirectories()
        {
            var dirs = new List<string>();

            void TryAdd(string dir)
            {
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) && !dirs.Contains(dir, StringComparer.OrdinalIgnoreCase))
                {
                    dirs.Add(dir);
                }
            }

            // 1. Configured TOSRomsPath
            TryAdd(ConfigOptions.RunninConfig.TOSRomsPath);

            // 2. Base directory TOS folder
            TryAdd(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TOS"));

            // 3. Base directory itself
            TryAdd(AppDomain.CurrentDomain.BaseDirectory);

            // 4. AppData TOS folder
            TryAdd(Path.Combine(Config.GetAppDefaultConfigsFilePath(), "TOS"));

            // 5. User system build output / source TOS folders
            TryAdd(@"C:\Users\adria\Desktop\Atari Emulator\ase-1.12\ASE\bin\Release\net9.0\TOS");
            TryAdd(@"C:\Users\adria\Desktop\Atari Emulator\ase-1.12\ASE\TOS");

            // 6. Current TOSPath folder
            if (!string.IsNullOrEmpty(ConfigOptions.RunninConfig.TOSPath))
            {
                try
                {
                    string parent = Path.GetDirectoryName(ConfigOptions.RunninConfig.TOSPath);
                    TryAdd(parent);
                }
                catch { }
            }

            // 7. TOSEC TOS folders if present
            TryAdd(@"C:\Users\adria\Desktop\Atari Emulator\Atari ST [TOSEC]\TOS\Atari ST - TOS - [IMG] (TOSEC-v2009-09-01_CM)");
            TryAdd(@"C:\Users\adria\Desktop\Atari Emulator\Atari ST [TOSEC]\TOS");

            return dirs;
        }
    }
}
