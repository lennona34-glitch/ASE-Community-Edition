using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;

namespace ASE.Services;

/// <summary>
/// Detects, pairs, and manages multi-disc floppy sets for Atari ST games.
/// Supports standalone disk images (.st, .msa, .stx, .dim, .ipf), .zip archives
/// with multiple disk entries, and multi-file sets across directories.
/// </summary>
public static class DiskSetManager
{
    private static readonly HashSet<string> DiskExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".st", ".msa", ".stx", ".dim", ".ipf", ".zip"
    };

    /// <summary>
    /// Searches for a matching companion disk (e.g. Disk 2 / Side B) for a given disk path.
    /// Supports standalone disk images, disk images inside .zip archives, and sets of .zip files.
    /// </summary>
    public static string FindCompanionDisk(string diskPath, int targetDisk = 2)
    {
        if (string.IsNullOrWhiteSpace(diskPath))
            return null;

        string zipPath = null;
        string entryName = null;

        // Check if path is inside a zip: "volume.zip|entry.st"
        if (diskPath.Contains(".zip|", StringComparison.OrdinalIgnoreCase))
        {
            var parts = diskPath.Split('|');
            zipPath = parts[0];
            entryName = parts[1];

            if (File.Exists(zipPath))
            {
                try
                {
                    using var archive = ZipFile.OpenRead(zipPath);
                    var validEntries = archive.Entries
                        .Where(e => DiskExtensions.Contains(Path.GetExtension(e.FullName)))
                        .Select(e => e.FullName)
                        .ToList();

                    // If targetDisk is inside the same zip, return it
                    string companionEntry = FindMatchingCompanion(entryName, validEntries, targetDisk);
                    if (companionEntry != null)
                        return $"{zipPath}|{companionEntry}";
                }
                catch { }
            }

            // If not found inside the zip (or zip only has 1 disk), fall through
            // and search the parent directory of zipPath for sibling zip/disk files!
        }

        string fileToMatch = zipPath ?? diskPath;

        // Check if path is a standalone .zip file with multiple entries
        if (zipPath == null && fileToMatch.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && File.Exists(fileToMatch))
        {
            try
            {
                using var archive = ZipFile.OpenRead(fileToMatch);
                var validEntries = archive.Entries
                    .Where(e => DiskExtensions.Contains(Path.GetExtension(e.FullName)))
                    .Select(e => e.FullName)
                    .ToList();

                if (validEntries.Count > 1)
                {
                    string firstEntry = validEntries.OrderBy(e => e, StringComparer.OrdinalIgnoreCase).First();
                    string companionEntry = FindMatchingCompanion(firstEntry, validEntries, targetDisk);
                    if (companionEntry != null)
                        return $"{fileToMatch}|{companionEntry}";
                }
            }
            catch { }
        }

        // Standalone disk image or zip file in directory
        if (File.Exists(fileToMatch))
        {
            string dir = Path.GetDirectoryName(fileToMatch);
            string fileName = Path.GetFileName(fileToMatch);

            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                try
                {
                    var filesInDir = Directory.EnumerateFiles(dir)
                        .Where(f => DiskExtensions.Contains(Path.GetExtension(f)))
                        .Select(Path.GetFileName)
                        .ToList();

                    string match = FindMatchingCompanion(fileName, filesInDir, targetDisk);
                    if (match != null)
                    {
                        string fullMatchPath = Path.Combine(dir, match);

                        // If companion is a zip file, resolve its internal image entry
                        if (fullMatchPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                using var archive = ZipFile.OpenRead(fullMatchPath);
                                var validEntries = archive.Entries
                                    .Where(e => DiskExtensions.Contains(Path.GetExtension(e.FullName)))
                                    .Select(e => e.FullName)
                                    .ToList();

                                if (validEntries.Count == 1)
                                {
                                    return $"{fullMatchPath}|{validEntries[0]}";
                                }
                                else if (validEntries.Count > 1)
                                {
                                    string bestEntry = validEntries.FirstOrDefault(e => ExtractDiskNumber(Path.GetFileNameWithoutExtension(e)) == targetDisk)
                                        ?? validEntries.OrderBy(e => e, StringComparer.OrdinalIgnoreCase).First();
                                    return $"{fullMatchPath}|{bestEntry}";
                                }
                            }
                            catch { }
                        }

                        return fullMatchPath;
                    }
                }
                catch { }
            }
        }

        return null;
    }

    /// <summary>
    /// Given a current disk file and a list of candidates, finds the file that corresponds to targetDisk.
    /// Strictly guarantees the companion matches targetDisk and is not the current disk.
    /// </summary>
    public static string FindMatchingCompanion(string currentFile, IEnumerable<string> candidates, int targetDisk)
    {
        if (string.IsNullOrWhiteSpace(currentFile) || candidates == null)
            return null;

        string currentBase = Path.GetFileNameWithoutExtension(currentFile);
        char targetChar = (char)('A' + (targetDisk - 1)); // 1 -> 'A', 2 -> 'B', etc.

        // Pattern 1: (1 of 3), [1 of 3], (Disk 1 of 5)
        var pOf = new Regex(@"([(\[\s\-_])(\d+)(\s*of\s*\d+[)\]\s\-_])", RegexOptions.IgnoreCase);
        if (pOf.IsMatch(currentBase))
        {
            string searchTarget = pOf.Replace(currentBase, m => $"{m.Groups[1].Value}{targetDisk}{m.Groups[3].Value}", 1);
            var match = candidates.FirstOrDefault(c => string.Equals(Path.GetFileNameWithoutExtension(c), searchTarget, StringComparison.OrdinalIgnoreCase));
            if (match != null && !IsExcludedCompanion(match, currentFile, targetDisk)) return match;
        }

        // Pattern 2: (Disk 1), Disk 2, Disc 3, _Disk1, -Disk1, .Disk1, _D1, D1
        var pDisk = new Regex(@"(?i)(?<=^|[\s_.\-\(\[])(disk|disc|d)([\s_.\-]*)(\d+)(?=$|[\s_.\-\)\]])", RegexOptions.IgnoreCase);
        if (pDisk.IsMatch(currentBase))
        {
            string searchTarget = pDisk.Replace(currentBase, m => $"{m.Groups[1].Value}{m.Groups[2].Value}{targetDisk}", 1);
            var match = candidates.FirstOrDefault(c => string.Equals(Path.GetFileNameWithoutExtension(c), searchTarget, StringComparison.OrdinalIgnoreCase));
            if (match != null && !IsExcludedCompanion(match, currentFile, targetDisk)) return match;
        }

        // Pattern 3: (Side A), Side 1, Side A, Side B
        var pSide = new Regex(@"(?i)\b(side)([\s_.\-]*)([0-9a-zA-Z])\b", RegexOptions.IgnoreCase);
        if (pSide.IsMatch(currentBase))
        {
            var m = pSide.Match(currentBase);
            string rep = char.IsDigit(m.Groups[3].Value[0]) ? targetDisk.ToString() : targetChar.ToString();
            string searchTarget = pSide.Replace(currentBase, $"$1$2{rep}", 1);
            var match = candidates.FirstOrDefault(c => string.Equals(Path.GetFileNameWithoutExtension(c), searchTarget, StringComparison.OrdinalIgnoreCase));
            if (match != null && !IsExcludedCompanion(match, currentFile, targetDisk)) return match;
        }

        // Pattern 4: Ending in _A, -A, (A), _B, (B)
        var pLetter = new Regex(@"([(\[\-_])([a-zA-Z])([)\]\-_]|$)");
        if (pLetter.IsMatch(currentBase))
        {
            string searchTarget = pLetter.Replace(currentBase, m => $"{m.Groups[1].Value}{targetChar}{m.Groups[3].Value}", 1);
            var match = candidates.FirstOrDefault(c => string.Equals(Path.GetFileNameWithoutExtension(c), searchTarget, StringComparison.OrdinalIgnoreCase));
            if (match != null && !IsExcludedCompanion(match, currentFile, targetDisk)) return match;
        }

        // Pattern 5: _1, -1, (1), _2, (2) at the end or before brackets
        var pNum = new Regex(@"([(\[\-_])(\d+)([)\]\-_]|$)");
        if (pNum.IsMatch(currentBase))
        {
            string searchTarget = pNum.Replace(currentBase, m => $"{m.Groups[1].Value}{targetDisk}{m.Groups[3].Value}", 1);
            var match = candidates.FirstOrDefault(c => string.Equals(Path.GetFileNameWithoutExtension(c), searchTarget, StringComparison.OrdinalIgnoreCase));
            if (match != null && !IsExcludedCompanion(match, currentFile, targetDisk)) return match;
        }

        // Fallback: search for candidate with common base prefix and matching disk number
        string prefix = currentBase.Length > 5 ? currentBase.Substring(0, currentBase.Length - 4) : currentBase;
        var prefixMatch = candidates.FirstOrDefault(c =>
            !IsExcludedCompanion(c, currentFile, targetDisk) &&
            Path.GetFileNameWithoutExtension(c).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            ExtractDiskNumber(Path.GetFileNameWithoutExtension(c)) == targetDisk);

        return prefixMatch;
    }

    /// <summary>
    /// Extracts the 1-based disk number from a file name without extension, or null if undetermined.
    /// </summary>
    public static int? ExtractDiskNumber(string fileNameWithoutExt)
    {
        if (string.IsNullOrWhiteSpace(fileNameWithoutExt))
            return null;

        var mOf = Regex.Match(fileNameWithoutExt, @"([(\[\s\-_])(\d+)\s*of\s*\d+[)\]\s\-_]", RegexOptions.IgnoreCase);
        if (mOf.Success && int.TryParse(mOf.Groups[2].Value, out int ofNum))
            return ofNum;

        var mDisk = Regex.Match(fileNameWithoutExt, @"(?i)(?<=^|[\s_.\-\(\[])(?:disk|disc|d)[\s_.\-]*(\d+)(?=$|[\s_.\-\)\]])");
        if (mDisk.Success && int.TryParse(mDisk.Groups[1].Value, out int diskNum))
            return diskNum;

        var mSide = Regex.Match(fileNameWithoutExt, @"(?i)\b(?:side)[\s_.\-]*([0-9a-zA-Z])\b");
        if (mSide.Success)
        {
            char ch = mSide.Groups[1].Value[0];
            if (char.IsDigit(ch)) return ch - '0';
            ch = char.ToUpperInvariant(ch);
            if (ch >= 'A' && ch <= 'Z') return ch - 'A' + 1;
        }

        var mNum = Regex.Match(fileNameWithoutExt, @"([(\[\-_])(\d+)([)\]\-_]|$)");
        if (mNum.Success && int.TryParse(mNum.Groups[2].Value, out int num))
            return num;

        var mLetter = Regex.Match(fileNameWithoutExt, @"([(\[\-_])([a-zA-Z])([)\]\-_]|$)");
        if (mLetter.Success)
        {
            char ch = char.ToUpperInvariant(mLetter.Groups[2].Value[0]);
            if (ch >= 'A' && ch <= 'F') return ch - 'A' + 1;
        }

        return null;
    }

    /// <summary>
    /// Extracts the total number of disks if specified in the pattern (e.g. "Disk 1 of 5" -> 5).
    /// </summary>
    public static int? ExtractTotalDisks(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var mOf = Regex.Match(fileName, @"\b(\d+)\s*of\s*(\d+)\b", RegexOptions.IgnoreCase);
        if (mOf.Success && int.TryParse(mOf.Groups[2].Value, out int total))
            return total;

        return null;
    }

    /// <summary>
    /// Checks whether a candidate should be excluded as a companion for targetDisk.
    /// </summary>
    private static bool IsExcludedCompanion(string candidate, string currentFile, int targetDisk)
    {
        if (string.Equals(candidate, currentFile, StringComparison.OrdinalIgnoreCase))
            return true;

        string name = Path.GetFileNameWithoutExtension(candidate);
        string currentName = Path.GetFileNameWithoutExtension(currentFile);
        if (string.Equals(name, currentName, StringComparison.OrdinalIgnoreCase))
            return true;

        // If candidate denotes a disk number and it is NOT targetDisk, exclude it
        int? candNum = ExtractDiskNumber(name);
        if (candNum.HasValue && candNum.Value != targetDisk)
            return true;

        // If candidate denotes Disk 1 / Side A but targetDisk != 1, exclude it
        if (targetDisk != 1)
        {
            if (Regex.IsMatch(name, @"(?i)(?<=^|[\s_.\-\(\[])(?:disk|disc|d)[\s_.\-]*1(?=$|[\s_.\-\)\]])") ||
                Regex.IsMatch(name, @"(?i)\b(?:side)[\s_.\-]*[1aA]\b") ||
                Regex.IsMatch(name, @"([(\[\s\-_])1(\s*of\s*\d+[)\]\s\-_])", RegexOptions.IgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets all disks in the same sequence, ordered from Disk 1 upwards.
    /// Works regardless of whether currentDiskPath is Disk 1, Disk 2, or Disk 5.
    /// </summary>
    public static List<string> GetAllDisksInSet(string currentDiskPath)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(currentDiskPath))
            return list;

        int? currentDiskNum = ExtractDiskNumber(Path.GetFileNameWithoutExtension(currentDiskPath.Split('|').Last()));
        int? totalDisks = ExtractTotalDisks(currentDiskPath);

        // First, locate Disk 1
        string disk1 = null;
        if (currentDiskNum == 1)
        {
            disk1 = currentDiskPath;
        }
        else
        {
            disk1 = FindCompanionDisk(currentDiskPath, 1) ?? currentDiskPath;
        }

        list.Add(disk1);

        int maxSearch = totalDisks.HasValue ? Math.Max(totalDisks.Value, 2) : 10;

        for (int i = 2; i <= maxSearch; i++)
        {
            string companion = FindCompanionDisk(disk1, i);
            if (companion != null && !list.Contains(companion, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(companion);
            }
            else if (!totalDisks.HasValue)
            {
                // If total disks wasn't explicitly stated (e.g. not "of 5"), stop when missing
                break;
            }
        }

        // If currentDiskPath wasn't Disk 1 and wasn't found in list, ensure it is included
        if (!list.Contains(currentDiskPath, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(currentDiskPath);
        }

        return list;
    }
}
