using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ASE.Models;

namespace ASE.Services;

public class GameCatalogService
{
    private static readonly HttpClient _httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    static GameCatalogService()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AtariSystemEmulator/1.12");
    }

    public async Task<List<DownloadableGame>> GetCuratedGamesAsync(string libraryPath)
    {
        var games = new List<DownloadableGame>();

        try
        {
            string curatedPath = Path.Combine(AppContext.BaseDirectory, "Resources", "CuratedGames.json");
            if (!File.Exists(curatedPath))
            {
                // Fallback search in working dir
                curatedPath = Path.Combine(Directory.GetCurrentDirectory(), "Resources", "CuratedGames.json");
            }

            if (File.Exists(curatedPath))
            {
                string json = await File.ReadAllTextAsync(curatedPath);
                var loaded = JsonSerializer.Deserialize<List<DownloadableGame>>(json);
                if (loaded != null)
                    games.AddRange(loaded);
            }
        }
        catch
        {
            // If failed to read local json, fallback is empty list
        }

        UpdateInstalledStatus(games, libraryPath);
        return games;
    }

    public async Task<List<DownloadableGame>> SearchArchiveOrgAsync(string query, string libraryPath, CancellationToken ct = default)
    {
        var games = new List<DownloadableGame>();

        try
        {
            string cleanQuery = string.IsNullOrWhiteSpace(query) ? "" : query.Trim().Replace("\"", "");
            string queryParam;

            if (string.IsNullOrEmpty(cleanQuery))
            {
                queryParam = "collection:softwarelibrary_atari_st_games";
            }
            else
            {
                queryParam = $"collection:softwarelibrary_atari_st_games AND ({Uri.EscapeDataString(cleanQuery)})";
            }

            string searchUrl = $"https://archive.org/advancedsearch.php?q={queryParam}&fl[]=identifier,title,description,publicdate,downloads&sort[]=downloads+desc&rows=30&output=json";

            using var response = await _httpClient.GetAsync(searchUrl, ct);
            if (!response.IsSuccessStatusCode)
                return games;

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (doc.RootElement.TryGetProperty("response", out var responseElem) &&
                responseElem.TryGetProperty("docs", out var docsElem))
            {
                foreach (var docItem in docsElem.EnumerateArray())
                {
                    string id = docItem.TryGetProperty("identifier", out var idProp) ? idProp.GetString() : "";
                    if (string.IsNullOrEmpty(id))
                        continue;

                    string title = docItem.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : id;
                    string desc = docItem.TryGetProperty("description", out var descProp) ? descProp.GetString() : "";
                    string date = docItem.TryGetProperty("publicdate", out var dateProp) ? dateProp.GetString() : "";
                    string year = "";
                    if (!string.IsNullOrEmpty(date) && date.Length >= 4)
                        year = date.Substring(0, 4);

                    // Archive.org direct download link convention
                    string cleanFileName = desc.EndsWith(".st", StringComparison.OrdinalIgnoreCase)
                        ? desc
                        : $"{id}.st";

                    string downloadUrl = $"https://archive.org/download/{id}/{id}.st";
                    string thumbUrl = $"https://archive.org/download/{id}/__ia_thumb.jpg";

                    games.Add(new DownloadableGame
                    {
                        Id = id,
                        Title = title ?? id,
                        Description = desc ?? "",
                        Author = "Archive.org Atari ST Library",
                        Year = year,
                        Category = "Game",
                        DownloadUrl = downloadUrl,
                        FileName = cleanFileName,
                        ThumbnailUrl = thumbUrl,
                        Source = "Internet Archive",
                        License = "Archive Preservation"
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancel
        }
        catch
        {
            // Handle network or JSON errors gracefully
        }

        UpdateInstalledStatus(games, libraryPath);
        return games;
    }

    public async Task<List<DownloadableGame>> SearchTosecArchiveAsync(string query, string libraryPath, CancellationToken ct = default)
    {
        var games = new List<DownloadableGame>();

        try
        {
            string cleanQuery = string.IsNullOrWhiteSpace(query) ? "" : query.Trim().Replace("\"", "");
            string queryParam;

            if (string.IsNullOrEmpty(cleanQuery))
            {
                queryParam = "collection:softwarelibrary_atari_st_games";
            }
            else
            {
                queryParam = $"collection:softwarelibrary_atari_st_games AND ({Uri.EscapeDataString(cleanQuery)})";
            }

            string searchUrl = $"https://archive.org/advancedsearch.php?q={queryParam}&fl[]=identifier,title,description,publicdate,downloads&sort[]=downloads+desc&rows=50&output=json";

            using var response = await _httpClient.GetAsync(searchUrl, ct);
            if (!response.IsSuccessStatusCode)
                return games;

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (doc.RootElement.TryGetProperty("response", out var responseElem) &&
                responseElem.TryGetProperty("docs", out var docsElem))
            {
                foreach (var docItem in docsElem.EnumerateArray())
                {
                    string id = docItem.TryGetProperty("identifier", out var idProp) ? idProp.GetString() : "";
                    if (string.IsNullOrEmpty(id))
                        continue;

                    string title = docItem.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : id;
                    string desc = docItem.TryGetProperty("description", out var descProp) ? descProp.GetString() : "";
                    string date = docItem.TryGetProperty("publicdate", out var dateProp) ? dateProp.GetString() : "";
                    string year = "";
                    if (!string.IsNullOrEmpty(date) && date.Length >= 4)
                        year = date.Substring(0, 4);

                    string cleanFileName = desc.EndsWith(".st", StringComparison.OrdinalIgnoreCase) ||
                                           desc.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                                           desc.EndsWith(".msa", StringComparison.OrdinalIgnoreCase)
                        ? desc
                        : $"{id}.st";

                    string downloadUrl = $"https://archive.org/download/{id}/{id}.st";
                    string thumbUrl = $"https://archive.org/download/{id}/__ia_thumb.jpg";

                    games.Add(new DownloadableGame
                    {
                        Id = id,
                        Title = string.IsNullOrWhiteSpace(title) ? id.Replace('_', ' ') : title,
                        Description = string.IsNullOrWhiteSpace(desc) ? $"TOSEC: {id.Replace('_', ' ')}" : desc,
                        Author = "TOSEC Archive",
                        Year = year,
                        Category = "TOSEC",
                        DownloadUrl = downloadUrl,
                        FileName = cleanFileName,
                        ThumbnailUrl = thumbUrl,
                        Source = "TOSEC Archive",
                        License = "TOSEC Preservation"
                    });
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }

        UpdateInstalledStatus(games, libraryPath);
        return games;
    }

    public async Task<string> ResolveArchiveOrgDownloadUrlAsync(string identifier, CancellationToken ct = default)
    {
        try
        {
            string metaUrl = $"https://archive.org/metadata/{identifier}/files";
            using var response = await _httpClient.GetAsync(metaUrl, ct);
            if (!response.IsSuccessStatusCode)
                return $"https://archive.org/download/{identifier}/{identifier}.st";

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (doc.RootElement.TryGetProperty("result", out var resultElem))
            {
                var extensions = new[] { ".st", ".msa", ".stx", ".zip" };
                foreach (var fileItem in resultElem.EnumerateArray())
                {
                    if (fileItem.TryGetProperty("name", out var nameProp))
                    {
                        string fileName = nameProp.GetString() ?? "";
                        if (extensions.Any(ext => fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                        {
                            return $"https://archive.org/download/{identifier}/{fileName}";
                        }
                    }
                }
            }
        }
        catch
        {
            // Fall back to direct convention
        }

        return $"https://archive.org/download/{identifier}/{identifier}.st";
    }

    public static string NormalizeGameTitle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string s = Path.GetFileNameWithoutExtension(raw);
        // Strip tags in parentheses or brackets: (1990), (Tradewest), [cr ...], [one disk], [!], etc.
        s = Regex.Replace(s, @"\s*[\(\[][^\)\]]*[\)\]]", "");
        // Replace underscores, hyphens, dots with spaces
        s = Regex.Replace(s, @"[_\.\-]+", " ");
        // Strip punctuation and special chars
        s = Regex.Replace(s, @"[^a-zA-Z0-9\s]", "");
        // Collapse whitespace
        s = Regex.Replace(s, @"\s+", " ").Trim().ToLowerInvariant();
        return s;
    }

    private void UpdateInstalledStatus(IEnumerable<DownloadableGame> games, string libraryPath)
    {
        var dirsToSearch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(libraryPath) && Directory.Exists(libraryPath))
            dirsToSearch.Add(libraryPath);

        string defaultTosec = Config.GetDefaultTosecPath();
        if (!string.IsNullOrWhiteSpace(defaultTosec) && Directory.Exists(defaultTosec))
            dirsToSearch.Add(defaultTosec);

        if (!string.IsNullOrWhiteSpace(Config.ConfigOptions.RunninConfig.TosecPath) && Directory.Exists(Config.ConfigOptions.RunninConfig.TosecPath))
            dirsToSearch.Add(Config.ConfigOptions.RunninConfig.TosecPath);

        if (!string.IsNullOrWhiteSpace(Config.ConfigOptions.RunninConfig.DiskImagesPath) && Directory.Exists(Config.ConfigOptions.RunninConfig.DiskImagesPath))
            dirsToSearch.Add(Config.ConfigOptions.RunninConfig.DiskImagesPath);

        if (dirsToSearch.Count == 0)
            return;

        try
        {
            var diskExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".st", ".msa", ".stx", ".dim", ".ipf", ".zip" };

            // Exact filename -> fullPath
            var filenameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // File id (without ext) -> fullPath
            var idMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Normalized title -> fullPath
            var normalizedMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var enumOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 3,
                IgnoreInaccessible = true
            };

            foreach (var dir in dirsToSearch)
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.*", enumOptions))
                {
                    string ext = Path.GetExtension(file);
                    if (diskExtensions.Contains(ext))
                    {
                        string fname = Path.GetFileName(file);
                        string fNoExt = Path.GetFileNameWithoutExtension(file);
                        string norm = NormalizeGameTitle(fname);

                        filenameMap.TryAdd(fname, file);
                        idMap.TryAdd(fNoExt, file);
                        if (!string.IsNullOrEmpty(norm))
                            normalizedMap.TryAdd(norm, file);
                    }
                }
            }

            foreach (var game in games)
            {
                string matchedPath = null;

                // 1. Exact filename match
                if (!string.IsNullOrEmpty(game.FileName) && filenameMap.TryGetValue(game.FileName, out var p1))
                    matchedPath = p1;
                else if (filenameMap.TryGetValue($"{game.Id}.st", out var p2))
                    matchedPath = p2;
                else if (filenameMap.TryGetValue($"{game.Id}.zip", out var p3))
                    matchedPath = p3;
                else if (idMap.TryGetValue(game.Id, out var p4))
                    matchedPath = p4;
                else
                {
                    // 2. Normalized title match
                    string normTitle = NormalizeGameTitle(game.Title);
                    if (!string.IsNullOrEmpty(normTitle) && normalizedMap.TryGetValue(normTitle, out var p5))
                    {
                        matchedPath = p5;
                    }
                    else if (!string.IsNullOrEmpty(normTitle))
                    {
                        // StartsWith / prefix match
                        var cand = normalizedMap.FirstOrDefault(kv => kv.Key.StartsWith(normTitle) || normTitle.StartsWith(kv.Key));
                        if (!string.IsNullOrEmpty(cand.Value))
                            matchedPath = cand.Value;
                    }
                }

                if (!string.IsNullOrEmpty(matchedPath))
                {
                    game.Status = DownloadStatus.Installed;
                    game.Progress = 100;
                    game.StatusMessage = "Installed (TOSEC/Local)";
                    game.LocalPath = matchedPath;
                }
            }
        }
        catch { }
    }
}
