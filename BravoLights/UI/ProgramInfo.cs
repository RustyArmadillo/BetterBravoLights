using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace BravoLights.UI
{
    public class ProgramInfo
    {
        private const string ReleasesApiUrl = "https://api.github.com/repos/RoystonS/BetterBravoLights/releases";
        private static readonly HttpClient httpClient = new HttpClient();
        private static readonly Lazy<Task<string>> cachedLatestVersionFetch = new Lazy<Task<string>>(FetchLatestVersionStringAsync);

        public static string ProductNameAndVersion
        {
            get
            {
                return $"Better Bravo Lights {VersionString}";
            }
        }

        public static string VersionString
        {
            get {
                var version = Assembly.GetExecutingAssembly().GetName().Version;
                return $"{version.Major}.{version.Minor}.{version.Build}";
            }
        }

        private static async Task<string> FetchLatestVersionStringAsync()
        {
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
            httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RoystonS-BetterBravoLights", VersionString));

            // We're not going to bother paging so we'll assume that the latest version is somewhere in the first page.
            var response = await httpClient.GetStringAsync(ReleasesApiUrl);
            return ExtractLatestVersionFromGitHubReleasesJson(response);
        }

        internal static string ExtractLatestVersionFromGitHubReleasesJson(string json)
        {
            var doc = JsonDocument.Parse(json);

            Version latestVersion = null;
            foreach (var releaseEntry in doc.RootElement.EnumerateArray())
            {
                try
                {
                    if (releaseEntry.GetProperty("draft").GetBoolean())
                    {
                        continue;
                    }

                    // GitHub tags are normally formatted as v0.6.0; accept tags without the prefix too.
                    var versionString = releaseEntry.GetProperty("tag_name").GetString();
                    if (string.IsNullOrWhiteSpace(versionString))
                    {
                        versionString = releaseEntry.GetProperty("name").GetString();
                    }
                    versionString = versionString?.Trim();
                    if (versionString != null && versionString.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                    {
                        versionString = versionString[1..];
                    }
                    if (string.IsNullOrWhiteSpace(versionString))
                    {
                        continue;
                    }
                    var version = new Version(versionString);
                    if (latestVersion == null || version.CompareTo(latestVersion) > 0)
                    {
                        latestVersion = version;
                    }
                }
                catch
                {
                }
            }

            if (latestVersion == null)
            {
                throw new InvalidDataException("GitHub returned no releases with valid version tags.");
            }

            return latestVersion.ToString();
        }

        public static Task<string> GetLatestVersionStringAsync()
        {
            return cachedLatestVersionFetch.Value;
        }

        public static async Task<bool> IsNewVersionAvailableAsync()
        {
            try
            {
                var latestVersion = await GetLatestVersionStringAsync();
                return new Version(latestVersion) > new Version(VersionString);
            } catch
            {
                return false;
            }
        }
    }
}
