using Newtonsoft.Json;
using SysBot.Base;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SysBot.Pokemon.WinForms
{
    public class PokeBotUpdateChecker
    {
        private const string RepositoryOwner = "Taku1991";
        private const string RepositoryName = "PokeBot";

        public static async Task<(bool UpdateAvailable, bool UpdateRequired, string NewVersion)> CheckForUpdatesAsync(bool forceShow = false)
        {
            ReleaseInfo? latestRelease = await FetchLatestReleaseAsync();
            if (latestRelease == null)
            {
                if (forceShow)
                {
                    MessageBox.Show("Failed to fetch release information. Please check your internet connection.",
                        "Update Check Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return (false, false, string.Empty);
            }

            bool updateAvailable = latestRelease.TagName != GetCurrentPokeBotVersion();
            bool updateRequired = latestRelease.Prerelease == false && IsUpdateRequired(latestRelease.Body);
            string? newVersion = latestRelease.TagName;

            // Only show update dialog if explicitly forced (manual button click)
            // Automatic updates via web interface handle updates without dialogs
            if (forceShow && updateAvailable)
            {
                UpdateForm updateForm = new(updateRequired, newVersion ?? "", updateAvailable);
                updateForm.ShowDialog();
            }
            else if (updateAvailable && !forceShow)
            {
                // Log automatic update detection without showing dialog
                Console.WriteLine($"PokeBot update available: {newVersion}. Use web interface or manual update button to upgrade.");
            }

            return (updateAvailable, updateRequired, newVersion ?? string.Empty);
        }

        public static async Task<string> FetchChangelogAsync()
        {
            ReleaseInfo? latestRelease = await FetchLatestReleaseAsync();
            return latestRelease?.Body ?? "Failed to fetch the latest PokeBot release information.";
        }

        public static async Task<string?> FetchDownloadUrlAsync()
        {
            ReleaseInfo? latestRelease = await FetchLatestReleaseAsync();
            if (latestRelease?.Assets == null || !latestRelease.Assets.Any())
            {
                Console.WriteLine("No assets found in the release");
                return null;
            }

            return latestRelease.Assets
                .FirstOrDefault(a => a.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true)
                ?.BrowserDownloadUrl;
        }

        private static async Task<ReleaseInfo?> FetchLatestReleaseAsync()
        {
            using var client = new HttpClient();
            try
            {
                client.DefaultRequestHeaders.Add("User-Agent", "SVRaidBot");

                string releasesUrl = $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest";
                Console.WriteLine($"Fetching PokeBot from URL: {releasesUrl}");

                HttpResponseMessage response = await client.GetAsync(releasesUrl);
                string responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"GitHub API Error: {response.StatusCode} - {responseContent}");
                    return null;
                }

                var releaseInfo = JsonConvert.DeserializeObject<ReleaseInfo>(responseContent);
                if (releaseInfo == null)
                {
                    Console.WriteLine("Failed to deserialize release info");
                    return null;
                }

                Console.WriteLine($"Successfully fetched PokeBot release info. Tag: {releaseInfo.TagName}");
                return releaseInfo;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in FetchLatestReleaseAsync: {ex.Message}");
                return null;
            }
        }

        private static string GetCurrentPokeBotVersion()
        {
            try
            {
                // Try to get PokeBot version via reflection
                var pokeBotType = Type.GetType("SysBot.Pokemon.Helpers.PokeBot, SysBot.Pokemon");
                if (pokeBotType != null)
                {
                    var versionField = pokeBotType.GetField("Version",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (versionField != null)
                    {
                        return versionField.GetValue(null)?.ToString() ?? "Unknown";
                    }
                }
            }
            catch (Exception ex)
            {
                LogUtil.LogError($"Error getting PokeBot version: {ex.Message}", "PokeBotUpdateChecker");
            }
            
            // Fallback to unknown version
            return "Unknown";
        }

        private static bool IsUpdateRequired(string? changelogBody)
        {
            return !string.IsNullOrWhiteSpace(changelogBody) &&
                   changelogBody.Contains("Required = Yes", StringComparison.OrdinalIgnoreCase);
        }

        private class ReleaseInfo
        {
            [JsonProperty("tag_name")]
            public string? TagName { get; set; }

            [JsonProperty("prerelease")]
            public bool Prerelease { get; set; }

            [JsonProperty("assets")]
            public List<AssetInfo>? Assets { get; set; }

            [JsonProperty("body")]
            public string? Body { get; set; }
        }

        private class AssetInfo
        {
            [JsonProperty("name")]
            public string? Name { get; set; }

            [JsonProperty("browser_download_url")]
            public string? BrowserDownloadUrl { get; set; }
        }
    }
} 