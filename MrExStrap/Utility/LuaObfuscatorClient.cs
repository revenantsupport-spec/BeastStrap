using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace BeastStrap.Utility
{
    // Second obfuscation engine for the Obfuscator tab: luaobfuscator.com. Two-step API —
    //   1. POST /v1/obfuscator/newscript  (apikey header + raw script body) -> { sessionId }
    //   2. POST /v1/obfuscator/obfuscate  (apikey + sessionId headers + JSON config) -> { code }
    // The user supplies their own API key; it lives only in local settings, never in the repo or binary.
    // Reuses LeakdClient.LeakdResult as the shared success/output/error shape the Obfuscator VM expects.
    public static class LuaObfuscatorClient
    {
        private const string LOG_IDENT = "LuaObfuscatorClient";
        private const string Api = "https://api.luaobfuscator.com/v1/obfuscator";

        private const string BrowserUa =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        // Strong default: minify everything and virtualize.
        private const string ObfuscateConfig = "{\"MinifiyAll\":true,\"Virtualize\":true}";

        public static async Task<LeakdClient.LeakdResult> ObfuscateAsync(string code, string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                return new LeakdClient.LeakdResult { Success = false, Error = "Enter your luaobfuscator.com API key first (field on this tab)." };

            try
            {
                string? sessionId = await NewScriptAsync(code, apiKey);
                if (string.IsNullOrEmpty(sessionId))
                    return new LeakdClient.LeakdResult { Success = false, Error = "luaobfuscator.com couldn't start a session — check that your API key is valid." };

                using var req = new HttpRequestMessage(HttpMethod.Post, $"{Api}/obfuscate");
                req.Headers.TryAddWithoutValidation("User-Agent", BrowserUa);
                req.Headers.TryAddWithoutValidation("apikey", apiKey);
                req.Headers.TryAddWithoutValidation("sessionId", sessionId);
                req.Content = new StringContent(ObfuscateConfig, Encoding.UTF8, "application/json");

                using var resp = await App.HttpClient.SendAsync(req);
                string body = await resp.Content.ReadAsStringAsync();
                return ParseObfuscate(body, resp.IsSuccessStatusCode);
            }
            catch (TaskCanceledException)
            {
                App.Logger.WriteLine(LOG_IDENT, "Request timed out.");
                return new LeakdClient.LeakdResult { Success = false, Error = "luaobfuscator.com took too long to respond (30s timeout). Large scripts can take a while — try again." };
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                return new LeakdClient.LeakdResult { Success = false, Error = "Connection error — luaobfuscator.com is unavailable." };
            }
        }

        private static async Task<string?> NewScriptAsync(string code, string apiKey)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{Api}/newscript");
            req.Headers.TryAddWithoutValidation("User-Agent", BrowserUa);
            req.Headers.TryAddWithoutValidation("apikey", apiKey);
            req.Content = new StringContent(code, Encoding.UTF8, "text/plain");

            using var resp = await App.HttpClient.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                return null;

            string body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("sessionId", out var sid) && sid.ValueKind == JsonValueKind.String
                ? sid.GetString()
                : null;
        }

        private static LeakdClient.LeakdResult ParseObfuscate(string body, bool httpOk)
        {
            var r = new LeakdClient.LeakdResult();
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                string? codeVal = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                string? message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;

                if (!httpOk || string.IsNullOrEmpty(codeVal))
                {
                    r.Success = false;
                    r.Error = !string.IsNullOrWhiteSpace(message) ? message : "luaobfuscator.com returned an error.";
                }
                else
                {
                    r.Success = true;
                    r.Output = codeVal!;
                    r.OutputSizeKb = Encoding.UTF8.GetByteCount(codeVal!) / 1024.0;
                }
            }
            catch (JsonException)
            {
                r.Success = false;
                r.Error = "Unexpected response from luaobfuscator.com.";
            }
            return r;
        }
    }
}
