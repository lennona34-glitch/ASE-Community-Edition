using System.Text.Json;
using System.Text.Json.Serialization;

namespace ASE
{
    public static class ReleaseChecker
    {
        public class ReleaseInfoModel
        {
            public bool ExistsNewVersion { get; set; } = false;

            [JsonPropertyName("tag_name")]
            public string TagName { get; set; } = "";

            [JsonPropertyName("name")]
            public string Name { get; set; } = "";

            [JsonPropertyName("html_url")]
            public string HtmlUrl { get; set; } = "";

            [JsonPropertyName("published_at")]
            public DateTime PublishedAt { get; set; }

            [JsonPropertyName("prerelease")]
            public bool IsPreRelease { get; set; }
        }

        public static ReleaseInfoModel ReleaseInfo = null;

        /// <summary>
        /// The startup query, so whoever needs the answer can wait for it without anybody having
        /// to block the launch on it. Completed (with no check having run) when the setting is off
        /// or the query was never started, so awaiting it is always safe — see
        /// <see cref="MainWindow.ShowUpdateWindowIfNeeded"/>.
        /// </summary>
        public static Task Check { get; private set; } = Task.CompletedTask;

        /// <summary>
        /// Starts the check in the background and returns at once. GitHub answering is not worth
        /// a single frame of the emulator's startup: a slow (or captive) network used to hold the
        /// process on a black screen for as long as the request took.
        /// </summary>
        public static void Start()
        {
            Check = IsNewVersionAvailableAsync();
        }

        public static async Task IsNewVersionAvailableAsync()
        {
            try
            {
                // Nothing waits on this anymore, but an answer that arrives minutes into a game
                // would raise the update window over whatever is running: past this the release
                // is simply not news worth interrupting for. (The default is 100 s.)
                using var _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                _httpClient.DefaultRequestHeaders.Add("User-Agent", $"ASE/{Config.Version}");

                var url = $"https://api.github.com/repos/thebitculture/ASE/releases/latest";

                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var release = JsonSerializer.Deserialize<ReleaseInfoModel>(json);

                var latestVersion = release.TagName.TrimStart('v');
                release.ExistsNewVersion = Version.Parse(latestVersion) > Version.Parse(Config.Version);

                // Published only once it is complete: the UI thread reads this field without a
                // lock, and a half-filled model is worse than no answer at all.
                ReleaseInfo = release;

                if (release.ExistsNewVersion)
                    ColoredConsole.WriteLine($"⭐⭐ New release [[yellow]]{release.TagName}[[/yellow]] available!! from [[magenta]]{release.HtmlUrl}[[/magenta]] ⭐⭐", Config.ConfigOptions.DebugModes.Quiet);
            }
            catch (HttpRequestException httpex)
            {
                ColoredConsole.WriteLine($"Error querying Github for new version: {httpex.Message}", Config.ConfigOptions.DebugModes.Information);
            }
            catch (Exception ex)
            {
                ColoredConsole.WriteLine($"Error checking for new version: {ex.Message}", Config.ConfigOptions.DebugModes.Information);
            }
        }
    }
}
