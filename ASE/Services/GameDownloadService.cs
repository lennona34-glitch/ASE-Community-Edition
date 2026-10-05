using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ASE.Models;

namespace ASE.Services;

public class GameDownloadService
{
    private static readonly HttpClient _httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    static GameDownloadService()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AtariSystemEmulator/1.12");
    }

    public static event Action<string> GameInstalled;
    public static event Action<string> GameUninstalled;

    public async Task<bool> DownloadGameAsync(
        DownloadableGame game,
        string libraryPath,
        IProgress<double> progress = null,
        CancellationToken ct = default)
    {
        if (game == null || string.IsNullOrWhiteSpace(game.DownloadUrl))
            return false;

        if (string.IsNullOrWhiteSpace(libraryPath))
        {
            game.Status = DownloadStatus.Failed;
            game.StatusMessage = "Library folder is not configured.";
            return false;
        }

        try
        {
            Directory.CreateDirectory(libraryPath);
            string mediaDir = Path.Combine(libraryPath, "Media");
            Directory.CreateDirectory(mediaDir);

            game.Status = DownloadStatus.Downloading;
            game.StatusMessage = "Starting download...";
            game.Progress = 0;

            using var response = await _httpClient.GetAsync(game.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            string tempFilePath = Path.Combine(Path.GetTempPath(), $"ase_{Guid.NewGuid():N}.tmp");

            try
            {
                await using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
                await using (var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920];
                    long totalRead = 0;
                    int bytesRead;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                        totalRead += bytesRead;

                        if (totalBytes > 0)
                        {
                            double percentage = Math.Min(100.0, (double)totalRead / totalBytes * 100.0);
                            game.Progress = percentage;
                            progress?.Report(percentage);
                            game.StatusMessage = $"Downloading: {percentage:0}%";
                        }
                        else
                        {
                            game.StatusMessage = $"Downloaded: {totalRead / 1024:N0} KB";
                        }
                    }
                }

                // Determine final file name and extract if zip
                string finalDiskFileName = string.IsNullOrWhiteSpace(game.FileName)
                    ? Path.GetFileName(new Uri(game.DownloadUrl).LocalPath)
                    : game.FileName;

                bool isZip = finalDiskFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                             game.DownloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

                if (isZip)
                {
                    game.StatusMessage = "Extracting archive...";
                    finalDiskFileName = ExtractDiskImageFromZip(tempFilePath, libraryPath, finalDiskFileName);
                }
                else
                {
                    string destinationPath = Path.Combine(libraryPath, finalDiskFileName);
                    File.Copy(tempFilePath, destinationPath, true);
                }

                // Download thumbnail if available
                if (!string.IsNullOrWhiteSpace(game.ThumbnailUrl))
                {
                    try
                    {
                        string thumbPath = Path.Combine(mediaDir, $"Box-{game.Id}.png");
                        if (!File.Exists(thumbPath))
                        {
                            byte[] thumbData = await _httpClient.GetByteArrayAsync(game.ThumbnailUrl, ct);
                            await File.WriteAllBytesAsync(thumbPath, thumbData, ct);
                        }
                    }
                    catch
                    {
                        // Ignore thumbnail download errors non-fatally
                    }
                }

                // Register game in Library.json
                RegisterInLibraryJson(libraryPath, game, finalDiskFileName);

                game.Progress = 100;
                game.Status = DownloadStatus.Installed;
                game.StatusMessage = "Installed";

                GameInstalled?.Invoke(finalDiskFileName);
                return true;
            }
            finally
            {
                if (File.Exists(tempFilePath))
                {
                    try { File.Delete(tempFilePath); } catch { }
                }
            }
        }
        catch (OperationCanceledException)
        {
            game.Status = DownloadStatus.NotDownloaded;
            game.StatusMessage = "Download canceled.";
            return false;
        }
        catch (Exception ex)
        {
            game.Status = DownloadStatus.Failed;
            game.StatusMessage = $"Error: {ex.Message}";
            return false;
        }
    }

    private string ExtractDiskImageFromZip(string zipFilePath, string libraryPath, string defaultName)
    {
        using var archive = ZipFile.OpenRead(zipFilePath);
        var diskExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".st", ".msa", ".stx", ".dim", ".ipf" };

        var diskEntries = archive.Entries
            .Where(e => diskExtensions.Contains(Path.GetExtension(e.FullName)))
            .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (diskEntries.Count > 0)
        {
            string firstDiskFileName = null;
            foreach (var entry in diskEntries)
            {
                string outFileName = Path.GetFileName(entry.FullName);
                if (string.IsNullOrWhiteSpace(outFileName))
                    continue;

                string destination = Path.Combine(libraryPath, outFileName);
                entry.ExtractToFile(destination, true);

                if (firstDiskFileName == null)
                    firstDiskFileName = outFileName;
            }

            return firstDiskFileName ?? defaultName;
        }

        // Fallback: extract first non-empty file
        var firstFile = archive.Entries.FirstOrDefault(e => !string.IsNullOrEmpty(e.Name));
        if (firstFile != null)
        {
            string outFileName = firstFile.Name;
            string destination = Path.Combine(libraryPath, outFileName);
            firstFile.ExtractToFile(destination, true);
            return outFileName;
        }

        // If nothing extracted, copy original
        string fallbackName = Path.GetFileNameWithoutExtension(defaultName) + ".st";
        File.Copy(zipFilePath, Path.Combine(libraryPath, fallbackName), true);
        return fallbackName;
    }

    private void RegisterInLibraryJson(string libraryPath, DownloadableGame game, string diskFileName)
    {
        try
        {
            string libraryJsonPath = Path.Combine(libraryPath, "Library.json");
            LibraryCollection libraryCollection = null;

            if (File.Exists(libraryJsonPath))
            {
                try
                {
                    string json = File.ReadAllText(libraryJsonPath);
                    libraryCollection = JsonSerializer.Deserialize<LibraryCollection>(json);
                }
                catch { }
            }

            libraryCollection ??= new LibraryCollection();
            libraryCollection.Collection ??= new List<LibraryItem>();

            var existing = libraryCollection.Collection.FirstOrDefault(i =>
                string.Equals(i.Filename, diskFileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(i.Id, game.Id, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.Filename = diskFileName;
            }
            else
            {
                var newItem = new LibraryItem
                {
                    Id = string.IsNullOrWhiteSpace(game.Id) ? Path.GetFileNameWithoutExtension(diskFileName) : game.Id,
                    Filename = diskFileName,
                    Developer = game.Author,
                    Publisher = game.Author,
                    Name = new List<LibraryItem.RegionText>
                    {
                        new() { Region = "ss", Text = game.Title },
                        new() { Region = "eu", Text = game.Title },
                        new() { Region = "us", Text = game.Title }
                    },
                    Synopsis = string.IsNullOrWhiteSpace(game.Description) ? null : new List<LibraryItem.RegionText>
                    {
                        new() { Region = "ss", Text = game.Description },
                        new() { Region = "eu", Text = game.Description },
                        new() { Region = "us", Text = game.Description }
                    },
                    Genre = string.IsNullOrWhiteSpace(game.Category) ? null : new List<LibraryItem.RegionText>
                    {
                        new() { Region = "ss", Text = game.Category }
                    },
                    Date = string.IsNullOrWhiteSpace(game.Year) ? null : new List<LibraryItem.RegionText>
                    {
                        new() { Region = "ss", Text = game.Year }
                    }
                };

                libraryCollection.Collection.Add(newItem);
            }

            libraryCollection.LastUpdate = DateTime.Now;
            libraryCollection.ASEVersion = Config.Version;

            string serialized = JsonSerializer.Serialize(libraryCollection, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(libraryJsonPath, serialized);
        }
        catch
        {
            // Non-fatal if Library.json update fails; file still exists in folder
        }
    }

    public static bool UninstallGame(string fileName, string gameId, string libraryPath)
    {
        if (string.IsNullOrWhiteSpace(libraryPath) || !Directory.Exists(libraryPath))
            return false;

        bool deletedDisk = false;

        // 1. Delete disk file
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            string diskPath = Path.Combine(libraryPath, fileName);
            if (File.Exists(diskPath))
            {
                try { File.Delete(diskPath); deletedDisk = true; } catch { }
            }
        }

        // Check fallback disk files matching gameId if not found
        if (!deletedDisk && !string.IsNullOrWhiteSpace(gameId))
        {
            try
            {
                var candidates = Directory.EnumerateFiles(libraryPath, $"{gameId}.*").ToList();
                foreach (var f in candidates)
                {
                    try { File.Delete(f); deletedDisk = true; } catch { }
                }
            }
            catch { }
        }

        // 2. Delete media files (box art, title screen, video previews)
        string mediaDir = Path.Combine(libraryPath, "Media");
        if (Directory.Exists(mediaDir) && !string.IsNullOrWhiteSpace(gameId))
        {
            try
            {
                var mediaFiles = Directory.EnumerateFiles(mediaDir, $"*{gameId}*").ToList();
                foreach (var mf in mediaFiles)
                {
                    try { File.Delete(mf); } catch { }
                }
            }
            catch { }
        }

        // 3. Remove entry from Library.json
        try
        {
            string libraryJsonPath = Path.Combine(libraryPath, "Library.json");
            if (File.Exists(libraryJsonPath))
            {
                string json = File.ReadAllText(libraryJsonPath);
                var libraryCollection = JsonSerializer.Deserialize<LibraryCollection>(json);
                if (libraryCollection?.Collection != null)
                {
                    int removed = libraryCollection.Collection.RemoveAll(i =>
                        (!string.IsNullOrEmpty(fileName) && string.Equals(i.Filename, fileName, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(gameId) && string.Equals(i.Id, gameId, StringComparison.OrdinalIgnoreCase)));

                    if (removed > 0)
                    {
                        libraryCollection.LastUpdate = DateTime.Now;
                        string serialized = JsonSerializer.Serialize(libraryCollection, new JsonSerializerOptions { WriteIndented = true });
                        File.WriteAllText(libraryJsonPath, serialized);
                    }
                }
            }
        }
        catch { }

        GameUninstalled?.Invoke(fileName ?? gameId);
        return true;
    }
}
