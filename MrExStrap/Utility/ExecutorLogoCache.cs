using System.Net.Http;
using System.Security.Cryptography;

namespace BeastStrap.Utility
{
    // On-disk cache for remote images. Originally just executor logos from WEAO's CDN
    // (https://cdn.weao.gg/slug/<slug>/logo.png) for the Versions Manager tiles; the Scripts tab
    // now uses it for script thumbnails too, under its own subfolder.
    //
    // Cache miss → download once via App.HttpClient → reuse from disk for every subsequent
    // display. On download failure the caller falls back to its own placeholder.
    public static class ExecutorLogoCache
    {
        private const string LOG_IDENT = "ExecutorLogoCache";

        public const string ExecutorLogosFolder = "ExecutorLogos";
        public const string ScriptThumbnailsFolder = "ScriptThumbnails";

        // A remote host could serve us anything. Cap what we're willing to hold in memory while
        // transcoding rather than trusting Content-Length (which chunked responses omit anyway).
        private const int MaxImageBytes = 8 * 1024 * 1024;

        // Thumbnails render at 44px and decode at 88px. Storing anything much larger is waste:
        // re-encoding a 512px WebP straight to PNG produces several times the bytes it replaced,
        // and this folder holds up to maxFiles of them.
        private const int MaxStoredPixels = 128;

        public static string CacheDirectory => Path.Combine(Paths.Base, "Cache", ExecutorLogosFolder);

        // Returns the local cached path for the given URL, downloading on first use.
        // Returns null on any failure (network down, HTTP error, etc.) so the caller
        // can fall back without crashing.
        /// <param name="folder">Subfolder under Cache\. Keeps unrelated image sets apart.</param>
        /// <param name="maxFiles">
        /// When &gt; 0, prune the folder to this many files (oldest-written first) after a download.
        /// Executor logos are naturally bounded — there are only so many executors — but script
        /// thumbnails are not: the catalogue is ~8,000 entries and browsing it would otherwise
        /// grow this folder without limit.
        /// </param>
        public static async Task<string?> GetLogoAsync(
            string? url,
            CancellationToken token = default,
            string folder = ExecutorLogosFolder,
            int maxFiles = 0)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            string cachePath = "";
            try
            {
                string directory = Path.Combine(Paths.Base, "Cache", folder);
                Directory.CreateDirectory(directory);

                string fileName = ComputeFileName(url);
                cachePath = Path.Combine(directory, fileName);

                // A cached file is only usable if WPF can actually decode it. Checking the length
                // alone let a captive-portal HTML body (HTTP 200, non-empty, not an image) poison an
                // entry permanently — every later launch took this branch and every later decode
                // threw.
                if (File.Exists(cachePath))
                {
                    if (HasDecodableHeader(cachePath))
                        return cachePath;

                    // Zero-byte, truncated, or not an image at all — drop it and fetch again.
                    try { File.Delete(cachePath); } catch { /* best effort */ }
                }

                // ConfigureAwait(false) throughout: callers start this from the UI thread, and the
                // download, the ImageSharp decode and the prune must not run there. ScriptRow marshals
                // back with its own Dispatcher.Invoke once the path comes back.
                using var response = await App.HttpClient
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"HTTP {(int)response.StatusCode} fetching {url} — falling back to placeholder");
                    return null;
                }

                byte[]? bytes;
                await using (var contentStream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                    bytes = await ReadCappedAsync(contentStream, token).ConfigureAwait(false);

                if (bytes is null)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"{url} exceeded {MaxImageBytes / 1024 / 1024} MB — refusing to cache it");
                    return null;
                }

                if (bytes.Length == 0)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"{url} returned an empty body — falling back to placeholder");
                    return null;
                }

                bytes = Normalise(bytes, url);
                if (bytes is null)
                    return null;

                // Write to a private temp file and move it into place. Several rows routinely share
                // one image URL (a game icon), and letting them all open the same path for writing
                // threw IOException on every loser, which showed as an intermittent placeholder.
                string temp = cachePath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
                try
                {
                    await File.WriteAllBytesAsync(temp, bytes, token).ConfigureAwait(false);
                    File.Move(temp, cachePath, overwrite: true);
                }
                catch (IOException)
                {
                    // Someone else won the race, or is mid-read on their copy. Same URL means the
                    // same bytes, so theirs is just as good.
                    try { File.Delete(temp); } catch { /* best effort */ }
                    return File.Exists(cachePath) ? cachePath : null;
                }

                if (maxFiles > 0)
                    Prune(Path.GetDirectoryName(cachePath)!, maxFiles);

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

        // Reads the whole body, giving up (null) rather than growing past MaxImageBytes.
        private static async Task<byte[]?> ReadCappedAsync(Stream source, CancellationToken token)
        {
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[81920];

            int read;
            while ((read = await source.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxImageBytes)
                    return null;

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }

        /// <summary>
        /// Turns a downloaded body into something WPF can render at display size, or null if it
        /// isn't an image at all.
        /// </summary>
        /// <remarks>
        /// Applies to EVERY download, not just WebP. Two reasons: the site serves full-size PNG and
        /// JPEG banners as well (a measured 767x432 / 487 KB entry for a 44px slot), which would blow
        /// past the folder cap in megabytes rather than the ~3 MB the 128px assumption implies; and a
        /// body that decodes as nothing — a captive-portal HTML page, say — must be rejected here
        /// rather than cached and served forever.
        /// </remarks>
        private static byte[]? Normalise(byte[] data, string url)
        {
            // WebP always has to be converted — WPF has no decoder for it at all.
            if (!IsWebP(data) && IsWpfDecodable(data))
            {
                try
                {
                    // Identify parses only the header, so a correctly-sized image costs no decode.
                    var info = SixLabors.ImageSharp.Image.Identify(data);
                    if (info is not null && info.Width <= MaxStoredPixels && info.Height <= MaxStoredPixels)
                        return data;
                }
                catch
                {
                    // Unreadable header — fall through and let the full decode make the call.
                }
            }

            byte[]? png = TranscodeToPng(data);

            if (png is null)
                App.Logger.WriteLine(LOG_IDENT, $"{url} did not decode as an image — not caching it");

            return png;
        }

        /// <summary>Magic bytes for the formats WPF's imaging stack can decode unaided.</summary>
        /// <remarks>Notably absent: WebP. That's the whole reason Normalise exists.</remarks>
        private static bool IsWpfDecodable(byte[] d) =>
            (d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47)                // PNG
            || (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF)                             // JPEG
            || (d.Length >= 4 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F' && d[3] == '8')                 // GIF
            || (d.Length >= 2 && d[0] == 'B' && d[1] == 'M')                                               // BMP
            || (d.Length >= 4 && d[0] == 0x49 && d[1] == 0x49 && d[2] == 0x2A && d[3] == 0x00)             // TIFF (LE)
            || (d.Length >= 4 && d[0] == 0x4D && d[1] == 0x4D && d[2] == 0x00 && d[3] == 0x2A)             // TIFF (BE)
            || (d.Length >= 4 && d[0] == 0x00 && d[1] == 0x00 && d[2] == 0x01 && d[3] == 0x00);            // ICO

        // Cheap sanity check on an existing cache entry: read just enough to recognise the format.
        private static bool HasDecodableHeader(string path)
        {
            try
            {
                byte[] head = new byte[12];
                using var stream = File.OpenRead(path);
                int read = stream.Read(head, 0, head.Length);
                if (read < head.Length)
                    return false;

                return IsWpfDecodable(head);
            }
            catch
            {
                // Locked by another reader right now — leave it alone and use it.
                return true;
            }
        }

        // RIFF container with a WEBP fourcc: "RIFF" <4-byte size> "WEBP".
        private static bool IsWebP(byte[] data) =>
            data.Length >= 12
            && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F'
            && data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P';

        /// <summary>Decodes an image WPF can't read and re-encodes it as a display-sized PNG.</summary>
        /// <returns>PNG bytes, or null if the image was corrupt — the caller shows its placeholder.</returns>
        private static byte[]? TranscodeToPng(byte[] data)
        {
            try
            {
                using var image = SixLabors.ImageSharp.Image.Load(data);

                // Don't upscale — a thumbnail smaller than the box stays as it is.
                if (image.Width > MaxStoredPixels || image.Height > MaxStoredPixels)
                {
                    SixLabors.ImageSharp.Processing.ProcessingExtensions.Mutate(image, ctx =>
                        SixLabors.ImageSharp.Processing.ResizeExtensions.Resize(ctx,
                            new SixLabors.ImageSharp.Processing.ResizeOptions
                            {
                                Mode = SixLabors.ImageSharp.Processing.ResizeMode.Max,
                                Size = new SixLabors.ImageSharp.Size(MaxStoredPixels, MaxStoredPixels)
                            }));
                }

                using var output = new MemoryStream();
                image.Save(output, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
                return output.ToArray();
            }
            catch (Exception ex)
            {
                // Malformed or truncated image. Not worth failing the row over.
                App.Logger.WriteException(LOG_IDENT + "::TranscodeToPng", ex);
                return null;
            }
        }

        // Oldest-written first. Best-effort: a file another thread is mid-write on just survives
        // to the next prune.
        private static void Prune(string directory, int maxFiles)
        {
            try
            {
                var files = new DirectoryInfo(directory).GetFiles();
                if (files.Length <= maxFiles)
                    return;

                foreach (var file in files.OrderByDescending(f => f.LastWriteTimeUtc).Skip(maxFiles))
                {
                    try { file.Delete(); } catch { /* in use, catch it next time */ }
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Prune", ex);
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

                // WebP is always transcoded before it hits disk, so the cached file is a PNG.
                if (ext == ".webp")
                    ext = ".png";
            }
            catch { /* default to .png */ }

            return hex + ext;
        }
    }
}
