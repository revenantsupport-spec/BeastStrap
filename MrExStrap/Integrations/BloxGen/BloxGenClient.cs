using System.Net.Http;

namespace BeastStrap.Integrations.BloxGen
{
    // Thin client over the BloxGen alt-account generator API (https://bloxgen.net).
    //   POST https://core.bloxgen.net/api/generate
    //   body: {"apiKey":"BLOX-...","type":"alt"}
    //
    // The user supplies their OWN key on the AltGen tab — we never embed one. The public docs
    // sit behind Cloudflare, so this is built against the documented request/response shape and
    // kept deliberately defensive: anything unexpected is surfaced via RawResponse so the UI can
    // show it (and the user can send it to us if the API ever changes).
    //
    // Logging never includes the API key or the .ROBLOSECURITY cookie.
    public static class BloxGenClient
    {
        private const string LOG_IDENT = "BloxGenClient";
        private const string Endpoint = "https://core.bloxgen.net/api/generate";

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

        public class GenerateResult
        {
            public bool Success;
            public string? Username;
            public string? Password;
            public string? Cookie;
            public string? Region;
            public double? Cost;
            public long? Id;
            public string? AvatarUrl;
            public long? TimeRemaining;  // ms until cooldown (429s)
            public string? Error;        // human-readable failure reason
            public string? RawResponse;  // for surfacing unexpected shapes
        }

        public static async Task<GenerateResult> GenerateAsync(string apiKey, string type = "alt")
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                return new GenerateResult { Success = false, Error = "No API key set. Enter your BloxGen key first." };

            try
            {
                string payload = JsonSerializer.Serialize(new { apiKey, type });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");

                App.Logger.WriteLine(LOG_IDENT, $"Requesting a '{type}' from BloxGen");

                using var resp = await _http.PostAsync(Endpoint, content);
                string body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"BloxGen returned HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");

                    // BloxGen puts a human message in "message" and, on 429, ms left in "timeRemaining".
                    string? apiMsg = ExtractMessage(body);
                    long? wait = ExtractNumber(body, "timeRemaining");
                    string waitStr = wait.HasValue ? $" Try again in {Math.Ceiling(wait.Value / 1000.0)}s." : "";

                    return new GenerateResult
                    {
                        Success = false,
                        Error = ($"{apiMsg ?? $"BloxGen returned {(int)resp.StatusCode} {resp.ReasonPhrase}"}{waitStr}").Trim(),
                        TimeRemaining = wait,
                        RawResponse = body
                    };
                }

                return Parse(body);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                return new GenerateResult { Success = false, Error = $"{ex.GetType().Name}: {ex.Message}" };
            }
        }

        private static GenerateResult Parse(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                bool success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;

                // Account fields may be nested under "data" or sit flat at the root — handle both.
                JsonElement data = root.TryGetProperty("data", out var d) ? d : root;

                var result = new GenerateResult
                {
                    Success = success,
                    Username = GetString(data, "username"),
                    Password = GetString(data, "password"),
                    Cookie = GetString(data, "cookie") ?? GetString(data, "robloSecurity") ?? GetString(data, "roblosecurity"),
                    Region = GetString(data, "region"),
                    RawResponse = body
                };

                if (data.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number)
                    result.Cost = c.GetDouble();

                if (data.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                    result.Id = idEl.GetInt64();

                result.AvatarUrl = GetString(data, "avatarUrl") ?? GetString(data, "fullAvatarUrl");

                if (!success)
                {
                    result.Error = ExtractMessage(body) ?? "BloxGen reported failure.";
                }
                else if (string.IsNullOrEmpty(result.Username) && string.IsNullOrEmpty(result.Cookie))
                {
                    // success=true but nothing usable — surface raw so we can see the real shape.
                    result.Success = false;
                    result.Error = "BloxGen returned success but no account fields were found. See the log for the raw response.";
                }

                App.Logger.WriteLine(LOG_IDENT,
                    $"Parsed result: success={result.Success}, gotUsername={!string.IsNullOrEmpty(result.Username)}, gotCookie={!string.IsNullOrEmpty(result.Cookie)}, region={result.Region ?? "-"}");
                return result;
            }
            catch (Exception ex)
            {
                return new GenerateResult { Success = false, Error = $"Couldn't parse BloxGen response: {ex.Message}", RawResponse = body };
            }
        }

        private static string? GetString(JsonElement el, string name)
            => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        private static string? ExtractMessage(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                foreach (var key in new[] { "message", "error", "detail", "reason" })
                    if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                        return v.GetString();
            }
            catch { /* not JSON, or no message field */ }
            return null;
        }

        private static long? ExtractNumber(string body, string name)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number)
                    return v.GetInt64();
            }
            catch { /* not JSON */ }
            return null;
        }
    }
}
