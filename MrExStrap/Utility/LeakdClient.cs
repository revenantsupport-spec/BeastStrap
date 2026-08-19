using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BeastStrap.Utility
{
    // Client for the LeakD script-tooling API (backs the Obfuscator + Deobfuscator tabs). Every action is a
    // multipart POST with a single "file" field carrying the script (as script.lua). Most endpoints hit the
    // Railway backend directly; LuaObfuscator deobfuscation goes through LeakD's Vercel proxy, which injects
    // the LuaObfuscator.com API key the public backend doesn't carry.
    //
    // Nothing here needs a Roblox login. Responses are usually JSON, but some endpoints return the
    // transformed script as raw text — so we fall back to treating the whole body as the output when the
    // HTTP status is 2xx (mirrors what the LeakD web app does).
    public static class LeakdClient
    {
        private const string LOG_IDENT = "LeakdClient";

        // Railway blocked the old leakd-detector host and LeakD moved to this one on 2026-07-15. The
        // old address now 404s every request, which took the Obfuscator and Deobfuscator tabs down
        // completely. If these tabs break again, check LeakD's Discord for a new backend before
        // assuming the API itself changed — the routes below have been stable across the move.
        private const string Backend = "https://leakd.up.railway.app";
        private const string Proxy = "https://leakd.vercel.app/api-proxy";

        private const string BrowserUa =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        public class LeakdResult
        {
            public bool Success;
            public string Output = "";
            public string? Error;
            public string? DetectedName;   // /detect
            public int? Confidence;        // /detect
            public double? OutputSizeKb;
            public double? Ratio;          // /obfuscate
        }

        public static Task<LeakdResult> ObfuscateAsync(string code, string preset) =>
            PostAsync($"{Backend}/obfuscate?preset={Uri.EscapeDataString(preset)}", code);

        public static Task<LeakdResult> BeautifyAsync(string code) =>
            PostAsync($"{Backend}/beautify", code);

        public static Task<LeakdResult> DetectAsync(string code) =>
            PostAsync($"{Backend}/detect", code);

        // methodId: moonsec | prometheus | ironbrew2 | ironveil | hercules | luaobfuscator
        public static Task<LeakdResult> DeobfuscateAsync(string code, string methodId)
        {
            string url = methodId.Equals("luaobfuscator", StringComparison.OrdinalIgnoreCase)
                ? $"{Proxy}/luaobfuscator"
                : $"{Backend}/{methodId}";
            return PostAsync(url, code);
        }

        private static async Task<LeakdResult> PostAsync(string url, string code)
        {
            try
            {
                using var form = new MultipartFormDataContent();
                var file = new ByteArrayContent(Encoding.UTF8.GetBytes(code));
                file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
                form.Add(file, "file", "script.lua");

                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
                req.Headers.TryAddWithoutValidation("User-Agent", BrowserUa);

                using var resp = await App.HttpClient.SendAsync(req);
                string body = await resp.Content.ReadAsStringAsync();
                return Parse(body, resp.IsSuccessStatusCode);
            }
            catch (TaskCanceledException)
            {
                App.Logger.WriteLine(LOG_IDENT, "Request timed out.");
                return new LeakdResult { Success = false, Error = "The LeakD API took too long to respond (30s timeout). Large scripts can take a while — try again." };
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                return new LeakdResult { Success = false, Error = "Connection error — the LeakD API is unavailable. Try again shortly." };
            }
        }

        private static LeakdResult Parse(string body, bool httpOk)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                var r = new LeakdResult
                {
                    Success = root.TryGetProperty("success", out var s) && (s.ValueKind == JsonValueKind.True || s.ValueKind == JsonValueKind.False)
                        ? s.GetBoolean()
                        : httpOk,
                };

                if (!r.Success)
                {
                    string? err = Str(root, "error");
                    r.Error = err != null ? StripAnsi(err) : "The API rejected the request.";
                }

                r.Output = StripAnsi(Str(root, "deobfuscated_code")
                           ?? Str(root, "obfuscated_code")
                           ?? Str(root, "beautified_code")
                           ?? Str(root, "code")
                           ?? "");

                if (root.TryGetProperty("top_result", out var tr) && tr.ValueKind == JsonValueKind.Object)
                {
                    r.DetectedName = Str(tr, "name");
                    var confidence = Dbl(tr, "confidence"); // the API returns a float, e.g. 57.2
                    r.Confidence = confidence.HasValue ? (int?)Math.Round(confidence.Value) : null;
                }

                if (root.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.Object)
                {
                    r.OutputSizeKb = Dbl(f, "output_size_kb");
                    r.Ratio = Dbl(f, "ratio");
                }

                return r;
            }
            catch (JsonException)
            {
                // Not JSON — some endpoints return the transformed script directly as text.
                return new LeakdResult
                {
                    Success = httpOk,
                    Output = httpOk ? StripAnsi(body) : "",
                    Error = httpOk ? null : "Unexpected response from the API.",
                };
            }
        }

        // LeakD relays the obfuscator's raw terminal output, which carries ANSI colour codes (an ESC char
        // followed by "[31m", "[0m", etc.). Strip them so the UI shows a clean message. The pattern is
        // anchored on the ESC char (\x1b), so it can never touch legitimate code like "flags[element]".
        private static string StripAnsi(string s) =>
            string.IsNullOrEmpty(s) ? s : Regex.Replace(s, @"\x1b\[[0-9;]*[A-Za-z]", "");

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static double? Dbl(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
    }
}
