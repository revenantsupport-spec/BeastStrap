using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;

using BeastStrap.Models.APIs;

namespace BeastStrap.Utility
{
    // Where the executor list actually came from, so the UI can note when the backup was used.
    public enum WeaoSource { None, Weao, Mirror }

    // Client for the WEAO exploit-status API (https://weao.xyz/api), with a transparent failover
    // to the robloxscripts.com mirror of the same data.
    //
    // weao.xyz is frequently unreachable on end-user machines — not because the app is broken, but
    // because the domain is blocked at the network/ISP layer (SNI filtering surfaces as a browser
    // ERR_SSL_PROTOCOL_ERROR / a corrupted TLS frame in .NET) or intercepted by antivirus HTTPS
    // scanning. robloxscripts.com serves the identical data from a DIFFERENT domain, so a
    // weao.xyz-specific block doesn't reach it. We try weao.xyz first (canonical, freshest) and
    // fall back to the mirror on any failure (order flips with the PreferRobloxScriptsApi setting).
    // The mirror's endpoint path is resolved from its API index at call time — see below. Shapes
    // and mapping: docs.robloxscripts.com.
    //
    // Per docs.weao.xyz the User-Agent "WEAO-3PService" is required for weao.xyz. The mirror just
    // needs any non-bot User-Agent (a bare "curl/x" UA is challenged) — App.HttpClient's default
    // "BeastStrap/<version>" already satisfies that, so the mirror request sends no extra header.
    public static class WeaoClient
    {
        private const string EXPLOITS_ENDPOINT = "https://weao.xyz/api/status/exploits";
        private const string USER_AGENT = "WEAO-3PService";

        // robloxscripts.com mirror. Its exploit path has been renamed before (/api/v1/weao/... →
        // /api/v1/backup/... on 2026-06-17), so instead of hard-pinning it we ask the API index
        // (/api/v1, which lists endpoints.exploits) for the current path at call time, and only fall
        // back to MIRROR_EXPLOITS_DEFAULT if that lookup fails. A future rename then self-heals.
        private const string MIRROR_BASE = "https://robloxscripts.com";
        private const string MIRROR_INDEX_ENDPOINT = "https://robloxscripts.com/api/v1";
        private const string MIRROR_EXPLOITS_DEFAULT = "https://robloxscripts.com/api/v1/backup/status/exploits";

        public readonly record struct WeaoResult(IReadOnlyList<WeaoExploit> Exploits, string? Error, WeaoSource Source = WeaoSource.None)
        {
            public bool Success => Error is null;
        }

        public static async Task<WeaoResult> GetWindowsExploitsAsync(CancellationToken token = default)
        {
            const string LOG_IDENT = "WeaoClient::GetWindowsExploitsAsync";

            // Default order is weao.xyz (canonical, freshest) then the robloxscripts.com mirror. The
            // "prefer robloxscripts.com" setting flips it for users whose network/ISP blocks weao.xyz,
            // so they skip the dead weao.xyz attempt and hit the working mirror first. Either way the
            // other source is the fallback, and both are tried before giving up.
            bool preferMirror = App.Settings.Prop.PreferRobloxScriptsApi;
            string primaryHost = preferMirror ? "robloxscripts.com" : "weao.xyz";
            string fallbackHost = preferMirror ? "weao.xyz" : "robloxscripts.com";

            // Each source gets its OWN budget rather than sharing the caller's.
            //
            // Callers pass a wall-clock token (the executor refresher gives 3 seconds), and that
            // single token used to cover the primary fetch AND the whole fallback — including the
            // mirror's endpoint-discovery request, so up to three sequential round trips on one
            // 3-second clock. The mirror exists precisely for users whose ISP SNI-filters
            // weao.xyz, and filtering STALLS the TLS handshake rather than failing fast, so the
            // primary reliably ate the entire budget and the fallback was cancelled before it sent
            // a byte. The one path that had to work never ran.
            var primary = await WithOwnBudget(token, t => preferMirror ? FetchMirrorAsync(t) : FetchWeaoAsync(t));
            if (primary.Success)
                return primary;

            App.Logger.WriteLine(LOG_IDENT, $"{primaryHost} failed ({primary.Error}) — falling back to {fallbackHost}.");

            var fallback = await WithOwnBudget(token, t => preferMirror ? FetchWeaoAsync(t) : FetchMirrorAsync(t));
            if (fallback.Success)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Loaded {fallback.Exploits.Count} executors from {fallbackHost} (fallback).");
                return fallback;
            }

            // Both sources are unreachable — almost always a broad block on the user's PC or network
            // (antivirus HTTPS scanning, or an ISP/router SSL filter) rather than anything app-side.
            // Lead with the preferred-source reason and make clear the backup failed too.
            App.Logger.WriteLine(LOG_IDENT, $"{fallbackHost} also failed ({fallback.Error}).");
            return new WeaoResult(
                Array.Empty<WeaoExploit>(),
                "Couldn't load the executor list from weao.xyz or the robloxscripts.com backup.\n\n" +
                $"{primary.Error}\n\n" +
                "Both sources being down at once usually means something on your PC or network is blocking this kind " +
                "of traffic — antivirus HTTPS/SSL scanning, or an ISP/router-level filter. You can still paste a " +
                "version hash manually below.",
                WeaoSource.None);
        }

        // Per-attempt budget. The caller's token still cancels everything (so a real cancellation
        // propagates), but each source additionally gets its own deadline so one stalled host can't
        // starve the other. When the caller supplied no deadline of its own we just pass it through.
        private static readonly TimeSpan PerSourceBudget = TimeSpan.FromSeconds(6);

        private static async Task<WeaoResult> WithOwnBudget(CancellationToken token, Func<CancellationToken, Task<WeaoResult>> attempt)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(PerSourceBudget);

            try
            {
                return await attempt(cts.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // Our per-source deadline fired, not the caller's — report it as a failed source
                // so the fallback still gets its turn.
                return new WeaoResult(Array.Empty<WeaoExploit>(), $"The request timed out after {PerSourceBudget.TotalSeconds:F0}s.", WeaoSource.None);
            }
        }

        private static Task<WeaoResult> FetchWeaoAsync(CancellationToken token)
            => FetchExploitsAsync(EXPLOITS_ENDPOINT, "weao.xyz", WeaoSource.Weao, sendWeaoUserAgent: true, token);

        // Resolved mirror endpoint, cached for the process lifetime. Without this the discovery GET
        // was paid on every single fallback, doubling the mirror's round trips on exactly the
        // networks where round trips are expensive.
        private static string? _cachedMirrorUrl;

        private static async Task<WeaoResult> FetchMirrorAsync(CancellationToken token)
        {
            string url = _cachedMirrorUrl ??= await DiscoverMirrorExploitsUrlAsync(token);
            return await FetchExploitsAsync(url, "robloxscripts.com", WeaoSource.Mirror, sendWeaoUserAgent: false, token);
        }

        // Ask the robloxscripts.com API index (/api/v1) for the current exploits endpoint path so a
        // future path rename self-heals without an app update. Falls back to MIRROR_EXPLOITS_DEFAULT
        // on any problem (index blocked, shape changed, missing field).
        private static async Task<string> DiscoverMirrorExploitsUrlAsync(CancellationToken token)
        {
            const string LOG_IDENT = "WeaoClient::DiscoverMirrorUrl";
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, MIRROR_INDEX_ENDPOINT);
                using var response = await App.HttpClient.SendAsync(request, token);

                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(token);
                    using var doc = await JsonDocument.ParseAsync(stream, default, token);

                    if (doc.RootElement.TryGetProperty("endpoints", out var endpoints)
                        && endpoints.TryGetProperty("exploits", out var exploits)
                        && exploits.ValueKind == JsonValueKind.String)
                    {
                        string path = (exploits.GetString() ?? "").Trim();
                        if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                            return path;
                        if (path.StartsWith("/"))
                            return MIRROR_BASE + path;
                    }
                    App.Logger.WriteLine(LOG_IDENT, "Index had no usable endpoints.exploits; using default path.");
                }
                else
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Index returned HTTP {(int)response.StatusCode}; using default path.");
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Index discovery failed ({ex.GetType().Name}); using default path.");
            }
            return MIRROR_EXPLOITS_DEFAULT;
        }

        // Fetch + parse + filter one source. Never throws — every failure path returns a WeaoResult
        // carrying a human-readable Error so the caller can decide whether to fall back.
        private static async Task<WeaoResult> FetchExploitsAsync(string url, string host, WeaoSource source, bool sendWeaoUserAgent, CancellationToken token)
        {
            string LOG_IDENT = $"WeaoClient::FetchExploits({host})";

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (sendWeaoUserAgent)
                    request.Headers.UserAgent.ParseAdd(USER_AGENT);

                using var response = await App.HttpClient.SendAsync(request, token);

                if (!response.IsSuccessStatusCode)
                {
                    int code = (int)response.StatusCode;
                    string reason = code switch
                    {
                        403 => $"{host} returned 403 (forbidden). Cloudflare or your network may be blocking the request from your IP.",
                        429 => $"{host} returned 429 (rate limited). Wait a minute and click Refresh.",
                        503 => $"{host} returned 503 (service unavailable). It may be temporarily down — try again shortly.",
                        >= 500 and < 600 => $"{host} returned {code}. The site is having issues — try again shortly.",
                        _ => $"{host} returned HTTP {code}. Click Refresh to try again."
                    };
                    App.Logger.WriteLine(LOG_IDENT, $"Non-success status {code} from {url}");
                    return new WeaoResult(Array.Empty<WeaoExploit>(), reason, source);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(token);
                var all = await JsonSerializer.DeserializeAsync<List<WeaoExploit>>(
                    stream,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                    token);

                if (all is null)
                    return new WeaoResult(Array.Empty<WeaoExploit>(), $"{host} returned an empty response.", source);

                // Only surface Windows executors that aren't hidden, have a real hash, and are a
                // genuine internal executor (extype == wexecutor). External exploits (wexternal)
                // are standalone tools rather than injectors, and the Mac/Android/iOS entries
                // (mexecutor/aexecutor/iexecutor) don't apply — both are excluded so the list
                // stays a list of real executors. Sort by title for a predictable dropdown.
                var filtered = all
                    .Where(e => !e.Hidden
                                && string.Equals(e.Platform, "Windows", StringComparison.OrdinalIgnoreCase)
                                && string.Equals(e.Extype, "wexecutor", StringComparison.OrdinalIgnoreCase)
                                && VersionGuidValidator.IsWellFormed(e.RbxVersion))
                    .OrderBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                return new WeaoResult(filtered, null, source);
            }
            catch (TaskCanceledException) when (!token.IsCancellationRequested)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Request to {host} timed out (30s).");
                return new WeaoResult(Array.Empty<WeaoExploit>(),
                    $"Request to {host} timed out. Your connection may be slow or the request is being blocked silently.", source);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The caller gave up (its budget elapsed) — expected, not an error worth a stack trace.
                App.Logger.WriteLine(LOG_IDENT, $"Request to {host} cancelled by the caller.");
                return new WeaoResult(Array.Empty<WeaoExploit>(),
                    $"The request to {host} was cancelled before it finished.", source);
            }
            catch (HttpRequestException ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Http", ex);
                return new WeaoResult(Array.Empty<WeaoExploit>(), ClassifyHttpFailure(ex, host), source);
            }
            catch (JsonException ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Json", ex);
                return new WeaoResult(Array.Empty<WeaoExploit>(),
                    $"{host} returned data we couldn't parse. The API may have changed — please report this.", source);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                return new WeaoResult(Array.Empty<WeaoExploit>(),
                    $"Couldn't load the executor list from {host} ({ex.GetType().Name}).", source);
            }
        }

        // Map common transport failures to language that points the user at where to look.
        // Almost every "empty dropdown" report so far has been a network-side block, not a code bug.
        private static string ClassifyHttpFailure(HttpRequestException ex, string host)
        {
            var inner = ex.InnerException;
            if (inner is AuthenticationException)
            {
                return $"TLS handshake with {host} failed. This usually means antivirus HTTPS inspection is breaking the connection, " +
                       "or Windows is missing TLS 1.2/1.3 updates. Try disabling AV HTTPS scanning or running Windows Update.";
            }

            // A TLS stream that corrupts mid-flight — IOException like "Cannot determine the frame
            // size or a corrupted frame was received", usually wrapped as "The SSL connection could
            // not be established". A middlebox rewriting the connection: antivirus HTTPS/SSL scanning,
            // a filtering proxy/VPN, or an ISP/router SSL filter on the domain (which shows up in a
            // browser as ERR_SSL_PROTOCOL_ERROR — confirmed even with AV fully off).
            if (IsTlsStreamCorruption(ex))
            {
                return $"The secure connection to {host} was corrupted before it finished (a TLS frame came back malformed). " +
                       "This is usually antivirus HTTPS/SSL scanning, or an ISP/router-level filter blocking the site. " +
                       "Try turning off AV HTTPS scanning, switching DNS to 1.1.1.1 or 8.8.8.8, or a different network.";
            }

            if (inner is SocketException sock)
            {
                return sock.SocketErrorCode switch
                {
                    SocketError.HostNotFound =>
                        $"Couldn't resolve {host}. Your DNS server may be blocking it (some ISPs, school networks, " +
                        "and family-filter DNS like Cloudflare 1.1.1.3 categorize it). Try switching DNS to 1.1.1.1 or 8.8.8.8.",
                    SocketError.ConnectionRefused or SocketError.NetworkUnreachable or SocketError.HostUnreachable =>
                        $"Couldn't reach {host}. A firewall or VPN may be blocking outbound HTTPS to that host.",
                    SocketError.TimedOut =>
                        $"Connection to {host} timed out. Network is slow or the host is being filtered silently.",
                    _ => $"Network error contacting {host} (socket: {sock.SocketErrorCode}). Check your connection and click Refresh."
                };
            }

            string msg = (inner?.Message ?? ex.Message).Trim();
            if (string.IsNullOrEmpty(msg))
                msg = "unknown error";
            return $"Couldn't reach {host}: {msg}.";
        }

        // Walk the inner-exception chain for the signature of a corrupted TLS stream. SslStream
        // surfaces this as an IOException mentioning the TLS "frame" (e.g. "Cannot determine the
        // frame size or a corrupted frame was received"); HttpClient wraps it as "The SSL
        // connection could not be established". A genuine handshake/cert failure is an
        // AuthenticationException instead and is handled before this is called.
        private static bool IsTlsStreamCorruption(Exception ex)
        {
            for (Exception? e = ex; e is not null; e = e.InnerException)
            {
                if (e is IOException && e.Message.Contains("frame", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (e.Message.Contains("SSL connection could not be established", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
