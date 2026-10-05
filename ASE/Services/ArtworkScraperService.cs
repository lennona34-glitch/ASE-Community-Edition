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

public class ArtworkCandidate
{
    public string Title { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ThumbnailUrl { get; set; } = "";
    public string FullUrl { get; set; } = "";
    public string Source { get; set; } = "Internet Archive";
    public string Badge { get; set; } = "Cover";
    public bool IsLocal { get; set; }
    public string LocalFilePath { get; set; }
}

public class ArtworkScraperService
{
    private static readonly HttpClient _httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    static ArtworkScraperService()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AtariSystemEmulator/1.12");
    }

    private static readonly SemaphoreSlim _libretroLock = new SemaphoreSlim(1, 1);
    private static List<string> _libretroBoxarts = null;
    private static List<string> _libretroTitles = null;
    private static List<string> _libretroSnaps = null;

    private static async Task EnsureLibretroIndexAsync(CancellationToken ct = default)
    {
        if (_libretroBoxarts != null) return;
        await _libretroLock.WaitAsync(ct);
        try
        {
            if (_libretroBoxarts != null) return;

            async Task<List<string>> FetchIndexAsync(string url)
            {
                try
                {
                    string html = await _httpClient.GetStringAsync(url, ct);
                    var matches = Regex.Matches(html, @"href=""([^""]+\.png)""");
                    var list = new List<string>(matches.Count);
                    foreach (Match m in matches)
                    {
                        string fn = Uri.UnescapeDataString(m.Groups[1].Value);
                        if (!string.IsNullOrEmpty(fn) && !fn.StartsWith("../", StringComparison.OrdinalIgnoreCase))
                            list.Add(fn);
                    }
                    return list;
                }
                catch
                {
                    return new List<string>();
                }
            }

            var tBox = FetchIndexAsync("https://thumbnails.libretro.com/Atari%20-%20ST/Named_Boxarts/");
            var tTitle = FetchIndexAsync("https://thumbnails.libretro.com/Atari%20-%20ST/Named_Titles/");
            var tSnaps = FetchIndexAsync("https://thumbnails.libretro.com/Atari%20-%20ST/Named_Snaps/");

            await Task.WhenAll(tBox, tTitle, tSnaps);

            _libretroBoxarts = await tBox;
            _libretroTitles = await tTitle;
            _libretroSnaps = await tSnaps;
        }
        finally
        {
            _libretroLock.Release();
        }
    }

    public static string CleanGameTitle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string s = Regex.Replace(raw, @"\.(zip|stx|st|msa|dim|ipf|png|jpg|jpeg|gif|bmp)$", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"^Box[_-]", "", RegexOptions.IgnoreCase);

        // 1. Strip tags in brackets or parentheses: (1990), (Tradewest), [cr ...], [one disk], etc.
        s = Regex.Replace(s, @"\s*[\(\[][^\)\]]*[\)\]]", "");

        // 2. Strip dump/cracker tags if separated by underscores or dashes
        s = Regex.Replace(s, @"[_-](cr|m\d*|t\d*|one_disk|disk[_-]?\d+|v\d+|protected|demo|preview|ste|megaste)[_-].*$", "", RegexOptions.IgnoreCase);

        // 3. Strip trailing year and company if underscore-separated: _1992_Codemasters or _1989_Sega...
        s = Regex.Replace(s, @"[_-](19\d\d|20\d\d)([_-].*)?$", "", RegexOptions.IgnoreCase);

        // 4. Replace underscores, hyphens, dots with spaces
        s = Regex.Replace(s, @"[_\.\-]+", " ");

        // 5. Strip non-alphanumeric except space
        s = Regex.Replace(s, @"[^a-zA-Z0-9\s]", "");

        // 6. Collapse whitespace
        s = Regex.Replace(s, @"\s+", " ").Trim();

        // 7. Handle trailing articles: "Maddog Williams The" -> "The Maddog Williams"
        if (s.EndsWith(" The", StringComparison.OrdinalIgnoreCase))
            s = "The " + s.Substring(0, s.Length - 4).Trim();
        else if (s.EndsWith(" A", StringComparison.OrdinalIgnoreCase))
            s = "A " + s.Substring(0, s.Length - 2).Trim();

        return s;
    }

    public async Task<List<ArtworkCandidate>> SearchCandidatesAsync(string gameTitle, string gameId, string currentMediaDir, CancellationToken ct = default)
    {
        var candidates = new List<ArtworkCandidate>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string cleanTitle = CleanGameTitle(gameTitle);
        if (string.IsNullOrEmpty(cleanTitle))
            cleanTitle = CleanGameTitle(gameId);

        // 1. Search local Media folders first
        var localDirs = new List<string>();
        if (!string.IsNullOrEmpty(currentMediaDir) && Directory.Exists(currentMediaDir))
            localDirs.Add(currentMediaDir);

        string tosecStxMedia = Path.Combine(Config.GetDefaultTosecPath(), "Atari ST - Games - [STX] (TOSEC-v2011-03-20_CM)", "Media");
        if (Directory.Exists(tosecStxMedia) && !localDirs.Contains(tosecStxMedia, StringComparer.OrdinalIgnoreCase))
            localDirs.Add(tosecStxMedia);

        string searchWord = cleanTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

        foreach (var dir in localDirs)
        {
            try
            {
                var files = Directory.EnumerateFiles(dir, "*.*")
                    .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase));

                foreach (var f in files)
                {
                    string fname = Path.GetFileName(f);
                    string fnameClean = CleanGameTitle(fname);

                    bool matches = (!string.IsNullOrEmpty(searchWord) && fname.Contains(searchWord, StringComparison.OrdinalIgnoreCase)) ||
                                   (!string.IsNullOrEmpty(cleanTitle) && (fnameClean.Equals(cleanTitle, StringComparison.OrdinalIgnoreCase) || fnameClean.Contains(cleanTitle, StringComparison.OrdinalIgnoreCase)));

                    if (matches && !seenUrls.Contains(f))
                    {
                        seenUrls.Add(f);
                        string badge = fname.StartsWith("Box-", StringComparison.OrdinalIgnoreCase) ? "Box Art (Local)" : "Screenshot (Local)";
                        candidates.Add(new ArtworkCandidate
                        {
                            Title = Path.GetFileNameWithoutExtension(fname).Replace("Box-", "").Replace('_', ' '),
                            FileName = fname,
                            ThumbnailUrl = f,
                            FullUrl = f,
                            Source = "Local Collection",
                            Badge = badge,
                            IsLocal = true,
                            LocalFilePath = f
                        });
                    }
                }
            }
            catch { }
        }

        // 2. Search Libretro Database (Direct CDN, 1000+ authentic box arts, 1800+ title screens, 4000+ snaps)
        try
        {
            await EnsureLibretroIndexAsync(ct);

            void MatchLibretro(List<string> index, string category, string badge)
            {
                if (index == null || index.Count == 0) return;

                string rawTarget = Path.GetFileNameWithoutExtension(gameId ?? gameTitle);
                string cleanTarget = cleanTitle;

                int matchesAdded = 0;
                foreach (var entry in index)
                {
                    string entryWithoutExt = Path.GetFileNameWithoutExtension(entry);
                    string entryClean = CleanGameTitle(entryWithoutExt);

                    bool isMatch = false;
                    if (!string.IsNullOrEmpty(rawTarget) && (entryWithoutExt.Equals(rawTarget, StringComparison.OrdinalIgnoreCase) || entryWithoutExt.StartsWith(rawTarget, StringComparison.OrdinalIgnoreCase)))
                    {
                        isMatch = true;
                    }
                    else if (!string.IsNullOrEmpty(cleanTarget))
                    {
                        if (entryClean.Equals(cleanTarget, StringComparison.OrdinalIgnoreCase))
                        {
                            isMatch = true;
                        }
                        else if (cleanTarget.Length >= 5 && (entryClean.StartsWith(cleanTarget, StringComparison.OrdinalIgnoreCase) || cleanTarget.StartsWith(entryClean, StringComparison.OrdinalIgnoreCase)))
                        {
                            isMatch = true;
                        }
                    }

                    if (isMatch)
                    {
                        string fullUrl = $"https://thumbnails.libretro.com/Atari%20-%20ST/{category}/{Uri.EscapeDataString(entry)}";
                        if (!seenUrls.Contains(fullUrl))
                        {
                            seenUrls.Add(fullUrl);
                            candidates.Add(new ArtworkCandidate
                            {
                                Title = entryWithoutExt,
                                FileName = entry,
                                ThumbnailUrl = fullUrl,
                                FullUrl = fullUrl,
                                Source = "Libretro Archive",
                                Badge = badge,
                                IsLocal = false
                            });
                            matchesAdded++;
                            if (matchesAdded >= 2) break;
                        }
                    }
                }
            }

            MatchLibretro(_libretroBoxarts, "Named_Boxarts", "Box / Cover");
            MatchLibretro(_libretroTitles, "Named_Titles", "Title Screen");
            MatchLibretro(_libretroSnaps, "Named_Snaps", "Screenshot");
        }
        catch { }

        // 3. Query Internet Archive Atari ST collection
        if (!string.IsNullOrEmpty(cleanTitle))
        {
            try
            {
                string searchUrl = $"https://archive.org/advancedsearch.php?q=collection:softwarelibrary_atari_st_games+AND+({Uri.EscapeDataString(cleanTitle)})&fl[]=identifier,title,description&rows=5&output=json";
                using var resp = await _httpClient.GetAsync(searchUrl, ct);
                if (resp.IsSuccessStatusCode)
                {
                    using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                    if (doc.RootElement.TryGetProperty("response", out var respElem) &&
                        respElem.TryGetProperty("docs", out var docsElem))
                    {
                        foreach (var docItem in docsElem.EnumerateArray())
                        {
                            string id = docItem.TryGetProperty("identifier", out var idProp) ? idProp.GetString() : "";
                            string title = docItem.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : id;
                            if (string.IsNullOrEmpty(id)) continue;

                            // Fetch files metadata for this item
                            string metaUrl = $"https://archive.org/metadata/{id}/files";
                            try
                            {
                                using var metaResp = await _httpClient.GetAsync(metaUrl, ct);
                                if (metaResp.IsSuccessStatusCode)
                                {
                                    using var metaStream = await metaResp.Content.ReadAsStreamAsync(ct);
                                    using var metaDoc = await JsonDocument.ParseAsync(metaStream, cancellationToken: ct);

                                    if (metaDoc.RootElement.TryGetProperty("result", out var resultElem))
                                    {
                                        foreach (var fileItem in resultElem.EnumerateArray())
                                        {
                                            string fileName = fileItem.TryGetProperty("name", out var fnProp) ? fnProp.GetString() : "";
                                            if (string.IsNullOrEmpty(fileName)) continue;

                                            string ext = Path.GetExtension(fileName).ToLowerInvariant();
                                            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".gif")
                                                continue;

                                            if (fileName.Contains("_meta.") || fileName.Contains("_files.") || fileName.Contains("_sqlite."))
                                                continue;

                                            string fullUrl = $"https://archive.org/download/{id}/{fileName}";
                                            if (seenUrls.Contains(fullUrl)) continue;
                                            seenUrls.Add(fullUrl);

                                            string badge = "Screenshot";
                                            if (fileName.Contains("cover", StringComparison.OrdinalIgnoreCase) || fileName.Contains("box", StringComparison.OrdinalIgnoreCase))
                                                badge = "Box / Cover";
                                            else if (fileName.Contains("title", StringComparison.OrdinalIgnoreCase))
                                                badge = "Title Screen";
                                            else if (fileName.Equals("__ia_thumb.jpg", StringComparison.OrdinalIgnoreCase))
                                                badge = "Emulator Thumb";

                                            candidates.Add(new ArtworkCandidate
                                            {
                                                Title = $"{title} ({fileName})",
                                                FileName = fileName,
                                                ThumbnailUrl = fullUrl,
                                                FullUrl = fullUrl,
                                                Source = "Internet Archive",
                                                Badge = badge,
                                                IsLocal = false
                                            });
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
        }

        // Sort candidates: Box / Cover first, then Title Screen, then Screenshot, then Emulator Thumb
        candidates = candidates.OrderBy(c => c.Badge switch
        {
            "Box Art (Local)" => 1,
            "Box / Cover" => 2,
            "Title Screen" => 3,
            "Screenshot (Local)" => 4,
            "Screenshot" => 5,
            _ => 10
        }).ToList();

        return candidates;
    }

    public async Task<bool> DownloadAndSaveArtworkAsync(ArtworkCandidate candidate, string targetFilePath, CancellationToken ct = default)
    {
        try
        {
            string dir = Path.GetDirectoryName(targetFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (candidate.IsLocal && !string.IsNullOrEmpty(candidate.LocalFilePath) && File.Exists(candidate.LocalFilePath))
            {
                File.Copy(candidate.LocalFilePath, targetFilePath, true);
                return true;
            }

            if (!string.IsNullOrEmpty(candidate.FullUrl))
            {
                byte[] bytes = await _httpClient.GetByteArrayAsync(candidate.FullUrl, ct);
                if (bytes != null && bytes.Length > 0)
                {
                    await File.WriteAllBytesAsync(targetFilePath, bytes, ct);
                    return true;
                }
            }
        }
        catch { }

        return false;
    }
}
