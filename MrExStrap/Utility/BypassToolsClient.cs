using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace BeastStrap.Utility
{
    // Link bypasser via bypass.tools (backs the Link Bypasser tab). Uses the synchronous "direct" endpoint:
    // POST /api/v1/bypass/direct with an x-api-key header and { "url": "..." } -> { "resultUrl": "..." }.
    // The user supplies their own bypass.tools key (kept in local settings only); sign-up is advertised via
    // the referral link in the UI.
    public static class BypassToolsClient
    {
        private const string LOG_IDENT = "BypassToolsClient";
        private const string Api = "https://api.bypass.tools/api/v1";

        private const string BrowserUa =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        public class BypassResult
        {
            public bool Success;
            public string ResultUrl = "";
            public bool Cached;
            public string? Error;
        }

        public static async Task<BypassResult> BypassAsync(string url, string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                return new BypassResult { Error = "Enter your bypass.tools API key first (field on this tab)." };
            if (string.IsNullOrWhiteSpace(url))
                return new BypassResult { Error = "Paste a link to bypass first." };

            try
            {
                string payload = JsonSerializer.Serialize(new { url = url.Trim(), refresh = false });

                using var req = new HttpRequestMessage(HttpMethod.Post, $"{Api}/bypass/direct");
                req.Headers.TryAddWithoutValidation("User-Agent", BrowserUa);
                req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                using var resp = await App.HttpClient.SendAsync(req);
                string body = await resp.Content.ReadAsStringAsync();
                return Parse(body, resp.IsSuccessStatusCode);
            }
            catch (TaskCanceledException)
            {
                App.Logger.WriteLine(LOG_IDENT, "Request timed out.");
                return new BypassResult { Error = "bypass.tools took too long to respond (30s timeout). Some link providers are slow to bypass — try again." };
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                return new BypassResult { Error = "Connection error — bypass.tools is unavailable." };
            }
        }

        private static BypassResult Parse(string body, bool httpOk)
        {
            var r = new BypassResult();
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                string? resultUrl = root.TryGetProperty("resultUrl", out var ru) && ru.ValueKind == JsonValueKind.String ? ru.GetString() : null;
                if (!string.IsNullOrEmpty(resultUrl))
                {
                    r.Success = true;
                    r.ResultUrl = resultUrl!;
                    r.Cached = root.TryGetProperty("cached", out var c) && c.ValueKind == JsonValueKind.True;
                    return r;
                }

                string? message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                string? code = root.TryGetProperty("code", out var cd) && cd.ValueKind == JsonValueKind.String ? cd.GetString() : null;
                r.Success = false;
                r.Error = FriendlyError(message, code) ?? (httpOk ? "bypass.tools couldn't bypass that link." : "bypass.tools returned an error.");
            }
            catch (JsonException)
            {
                r.Success = false;
                r.Error = "Unexpected response from bypass.tools.";
            }
            return r;
        }

        private static string? FriendlyError(string? message, string? code) => code switch
        {
            "QUOTA_EXCEEDED" => "Your bypass.tools quota is used up — it resets each cycle, or upgrade your plan.",
            "RATE_LIMITED" => "Rate limited by bypass.tools — wait a moment and try again (60 requests/min).",
            "UNSUPPORTED" => message ?? "That link type isn't supported by bypass.tools.",
            "INVALID_URL" => "That doesn't look like a valid link.",
            _ => message,
        };
    }
}
