using System.Net.Http;
using System.Security.Cryptography;

namespace BeastStrap.Utility
{
    // On-disk cache for executor logos pulled from WEAO's CDN
    // (https://cdn.weao.gg/slug/<slug>/logo.png). The Versions Manager tab renders
    // these inside each tile. Cache miss → download once via App.HttpClient → reuse
    // from disk for every subsequent display. On download failure the caller falls
    // back to the letter-glyph placeholder.
    public static class ExecutorLogoCache
    {
        private const string LOG_IDENT = "ExecutorLogoCache";

        public static string CacheDirectory => Path.Combine(Paths.Base, "Cache", "ExecutorLogos");

        // Returns the local cached path for the given URL, downloading on first use.
        // Returns null on any failure (network down, HTTP error, etc.) so the caller
        // can fall back without crashing.
        public static async Task<string?> GetLogoAsync(string? url, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            string cachePath = "";
            try
            {
                Directory.CreateDirectory(CacheDirectory);

                string fileName = ComputeFileName(url);
                cachePath = Path.Combine(CacheDirectory, fileName);

                if (File.Exists(cachePath))
                {
                    var info = new FileInfo(cachePath);
                    if (info.Length > 0)
                        return cachePath;
                    // Zero-byte file from a previous failed download — delete and re-try.
                    try { File.Delete(cachePath); } catch { /* best effort */ }
                }

                using var response = await App.HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                if (!response.IsSuccessStatusCode)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"HTTP {(int)response.StatusCode} fetching {url} — falling back to placeholder");
                    return null;
                }

                await using (var contentStream = await response.Content.ReadAsStreamAsync(token))
                await using (var fileStream = new FileStream(cachePath, FileMode.Create, FileAccess.Write))
                {
                    await contentStream.CopyToAsync(fileStream, token);
                }

                App.Logger.WriteLine(LOG_IDENT, $"Cached logo from {url} to {cachePath}");
                return cachePath;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::GetLogoAsync", ex);
                // Clean up a partial file if one got created.
                try { if (!string.IsNullOrEmpty(cachePath) && File.Exists(cachePath)) File.Delete(cachePath); }
                catch { /* best effort */ }
                return null;
            }
        }

        private static string ComputeFileName(string url)
        {
            byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(url));
            string hex = Convert.ToHexString(hash).ToLowerInvariant();

            string ext = ".png";
            try
            {
                string urlPath = new Uri(url).AbsolutePath;
                string urlExt = Path.GetExtension(urlPath);
                if (!string.IsNullOrEmpty(urlExt) && urlExt.Length <= 5)
                    ext = urlExt.ToLowerInvariant();
            }
            catch { /* default to .png */ }

            return hex + ext;
        }
    }
}
