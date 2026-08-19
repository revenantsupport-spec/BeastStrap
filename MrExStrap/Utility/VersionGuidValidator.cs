using System.Net.Http;
using System.Text.RegularExpressions;

namespace BeastStrap.Utility
{
    public static class VersionGuidValidator
    {
        // Roblox version GUIDs are of the form "version-" followed by 16 hex chars.
        private static readonly Regex GuidPattern = new(
            @"^version-[a-f0-9]{16}$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool IsWellFormed(string? guid) =>
            !string.IsNullOrWhiteSpace(guid) && GuidPattern.IsMatch(guid);

        // HEAD-request the package manifest for this GUID; if the CDN returns 200, the build
        // still exists. Returns null on network error, bool otherwise.
        public static async Task<bool?> ExistsOnCdnAsync(string guid, CancellationToken token = default)
        {
            const string LOG_IDENT = "VersionGuidValidator::ExistsOnCdnAsync";

            if (!IsWellFormed(guid))
                return false;

            string baseUrl = !string.IsNullOrEmpty(RobloxInterfaces.Deployment.BaseUrl)
                ? RobloxInterfaces.Deployment.BaseUrl
                : "https://setup.rbxcdn.com";

            string manifestUrl = $"{baseUrl}/{guid}-rbxPkgManifest.txt";

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, manifestUrl);
                using var response = await App.HttpClient.SendAsync(request, token);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                return null;
            }
        }
    }
}
