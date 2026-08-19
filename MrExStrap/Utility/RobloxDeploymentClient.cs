using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeastStrap.Utility
{
    public class LiveDeploymentInfo
    {
        public string Hash { get; init; } = "";
        public string Version { get; init; } = "";
        public DateTime? LastModifiedUtc { get; init; }
    }

    public class DeploymentDetails
    {
        public string Hash { get; init; } = "";
        public bool Exists { get; init; }
        public bool NetworkError { get; init; }
        public int PackageCount { get; init; }
        public long TotalCompressedBytes { get; init; }
        public DateTime? LastModifiedUtc { get; init; }
    }

    public static class RobloxDeploymentClient
    {
        private const string CLIENT_SETTINGS_PRIMARY = "https://clientsettingscdn.roblox.com";
        private const string CLIENT_SETTINGS_FALLBACK = "https://clientsettings.roblox.com";
        private const string CDN_DEFAULT = "https://setup.rbxcdn.com";
        private const string CLIENT_VERSION_PATH = "/v2/client-version/WindowsPlayer";

        public static async Task<LiveDeploymentInfo?> GetCurrentLiveAsync(CancellationToken token = default)
        {
            const string LOG_IDENT = "RobloxDeploymentClient::GetCurrentLiveAsync";

            ClientVersionPayload? payload = null;

            foreach (var host in new[] { CLIENT_SETTINGS_PRIMARY, CLIENT_SETTINGS_FALLBACK })
            {
                try
                {
                    using var response = await App.HttpClient.GetAsync(host + CLIENT_VERSION_PATH, token);
                    response.EnsureSuccessStatusCode();

                    await using var stream = await response.Content.ReadAsStreamAsync(token);
                    payload = await JsonSerializer.DeserializeAsync<ClientVersionPayload>(
                        stream,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                        token);

                    if (payload is not null && VersionGuidValidator.IsWellFormed(payload.ClientVersionUpload))
                        break;
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException($"{LOG_IDENT}<{host}>", ex);
                }
            }

            if (payload is null || !VersionGuidValidator.IsWellFormed(payload.ClientVersionUpload))
                return null;

            // Second request: manifest HEAD → Last-Modified tells us when the deploy went live.
            DateTime? lastModified = await GetManifestLastModifiedAsync(payload.ClientVersionUpload, token);

            return new LiveDeploymentInfo
            {
                Hash = payload.ClientVersionUpload,
                Version = payload.Version,
                LastModifiedUtc = lastModified,
            };
        }

        public static async Task<DeploymentDetails> InspectAsync(string hash, CancellationToken token = default)
        {
            const string LOG_IDENT = "RobloxDeploymentClient::InspectAsync";

            if (!VersionGuidValidator.IsWellFormed(hash))
                return new DeploymentDetails { Hash = hash, Exists = false };

            string manifestUrl = $"{GetCdnBaseUrl()}/{hash}-rbxPkgManifest.txt";

            try
            {
                using var response = await App.HttpClient.GetAsync(manifestUrl, token);

                // 404 is a legit "this build doesn't exist" signal. Anything else (500s, 403, etc.)
                // is closer to a network/CDN problem — surface it separately so the UI can tell
                // users "we couldn't reach Roblox" instead of "wrong hash".
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return new DeploymentDetails { Hash = hash, Exists = false };

                if (!response.IsSuccessStatusCode)
                    return new DeploymentDetails { Hash = hash, Exists = false, NetworkError = true };

                string body = await response.Content.ReadAsStringAsync(token);
                DateTime? lastModified = response.Content.Headers.LastModified?.UtcDateTime;
                return ParseManifest(hash, body, lastModified);
            }
            catch (Exception ex)
            {
                // Thrown path = HttpClient error, DNS failure, timeout, cancellation, parse glitch.
                // None of these tell us the hash is wrong; treat as network-level.
                App.Logger.WriteException(LOG_IDENT, ex);
                return new DeploymentDetails { Hash = hash, Exists = false, NetworkError = true };
            }
        }

        private static async Task<DateTime?> GetManifestLastModifiedAsync(string hash, CancellationToken token)
        {
            const string LOG_IDENT = "RobloxDeploymentClient::GetManifestLastModifiedAsync";

            string manifestUrl = $"{GetCdnBaseUrl()}/{hash}-rbxPkgManifest.txt";

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, manifestUrl);
                using var response = await App.HttpClient.SendAsync(request, token);

                if (!response.IsSuccessStatusCode)
                    return null;

                return response.Content.Headers.LastModified?.UtcDateTime;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                return null;
            }
        }

        private static string GetCdnBaseUrl() =>
            !string.IsNullOrEmpty(RobloxInterfaces.Deployment.BaseUrl)
                ? RobloxInterfaces.Deployment.BaseUrl
                : CDN_DEFAULT;

        // rbxPkgManifest.txt layout:
        //   Line 0: "v0" version marker
        //   Then, per package, 4 lines: name, md5, compressed size, uncompressed size
        private static DeploymentDetails ParseManifest(string hash, string body, DateTime? lastModified)
        {
            var lines = body.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 5)
                return new DeploymentDetails { Hash = hash, Exists = true, LastModifiedUtc = lastModified };

            int packageCount = 0;
            long compressed = 0;

            for (int i = 1; i + 3 < lines.Length; i += 4)
            {
                packageCount++;
                if (long.TryParse(lines[i + 2], out long size))
                    compressed += size;
            }

            return new DeploymentDetails
            {
                Hash = hash,
                Exists = true,
                PackageCount = packageCount,
                TotalCompressedBytes = compressed,
                LastModifiedUtc = lastModified,
            };
        }

        private class ClientVersionPayload
        {
            [JsonPropertyName("clientVersionUpload")]
            public string ClientVersionUpload { get; set; } = "";

            [JsonPropertyName("version")]
            public string Version { get; set; } = "";
        }
    }
}
