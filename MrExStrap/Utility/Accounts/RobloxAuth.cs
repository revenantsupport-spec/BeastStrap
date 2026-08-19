namespace BeastStrap.Utility.Accounts
{
    public record AuthedUser(long Id, string Name, string DisplayName);

    // Thin client for the Roblox endpoints the account manager needs: validate a cookie, fetch a
    // headshot, and mint a one-time launch ("authentication") ticket. Adapted technique from
    // robloxmanager by sasha / centerepic (MIT) — https://gitlab.com/centerepic/robloxmanager.
    //
    // Each call sets the .ROBLOSECURITY cookie per-request. App.HttpClient is built with
    // UseCookies=false on purpose (see App.xaml.cs) so the handler never caches the cookie
    // auth.roblox.com rotates back and re-attaches it to the next account's request — without
    // that, every alt's launch ticket resolves to the same account. The cookie/CSRF/ticket
    // headers are redacted by HttpClientLoggingHandler so they never hit the log.
    public static class RobloxAuth
    {
        private const string LOG_IDENT = "RobloxAuth";
        private const string TicketEndpoint = "https://auth.roblox.com/v1/authentication-ticket/";

        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        private static HttpRequestMessage NewRequest(HttpMethod method, string url, string cookie)
        {
            var req = new HttpRequestMessage(method, url);

            // TryAdd returns false for a value HttpClient won't accept (an embedded newline is the
            // one that happens in practice). Ignoring it sent the request with no auth at all, and
            // the resulting 401 was indistinguishable from an expired cookie.
            if (!req.Headers.TryAddWithoutValidation("Cookie", $".ROBLOSECURITY={cookie}"))
                App.Logger.WriteLine(LOG_IDENT, "Cookie header was rejected as malformed — the request will be unauthenticated.");

            return req;
        }

        // The authenticated user for a cookie, or null if the cookie is invalid/expired.
        public static async Task<AuthedUser?> ValidateAsync(string cookie)
        {
            try
            {
                using var req = NewRequest(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated", cookie);
                using var resp = await App.HttpClient.SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"ValidateAsync: {(int)resp.StatusCode} {resp.ReasonPhrase}");
                    return null;
                }

                string json = await resp.Content.ReadAsStringAsync();
                var dto = JsonSerializer.Deserialize<AuthedUserDto>(json, JsonOpts);
                if (dto is null || dto.Id == 0)
                    return null;

                return new AuthedUser(dto.Id, dto.Name ?? "", dto.DisplayName ?? "");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Validate", ex);
                return null;
            }
        }

        // Public headshot thumbnail URL for a user (no cookie needed).
        public static async Task<string?> GetHeadshotUrlAsync(long userId)
        {
            try
            {
                string url = $"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={userId}&size=150x150&format=Png&isCircular=false";
                using var resp = await App.HttpClient.GetAsync(url);
                if (!resp.IsSuccessStatusCode)
                    return null;

                string json = await resp.Content.ReadAsStringAsync();
                var dto = JsonSerializer.Deserialize<ThumbnailListDto>(json, JsonOpts);
                return dto?.Data?.FirstOrDefault()?.ImageUrl;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Headshot", ex);
                return null;
            }
        }

        // Mint a one-time launch ticket from a cookie. Two steps: an unauthenticated POST returns
        // 403 with the CSRF token, then we retry with it and read the ticket response header.
        public static async Task<string?> GetAuthTicketAsync(string cookie)
        {
            try
            {
                string? csrf = await GetCsrfTokenAsync(cookie);
                if (string.IsNullOrEmpty(csrf))
                {
                    App.Logger.WriteLine(LOG_IDENT, "GetAuthTicketAsync: could not obtain a CSRF token (cookie expired?).");
                    return null;
                }

                using var req = NewRequest(HttpMethod.Post, TicketEndpoint, cookie);
                req.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);
                req.Headers.TryAddWithoutValidation("Referer", "https://www.roblox.com/");
                req.Headers.TryAddWithoutValidation("Origin", "https://www.roblox.com");
                req.Headers.TryAddWithoutValidation("RBXAuthenticationNegotiation", "1");
                req.Content = new StringContent("", Encoding.UTF8, "application/json");

                using var resp = await App.HttpClient.SendAsync(req);
                if (resp.Headers.TryGetValues("rbx-authentication-ticket", out var values))
                {
                    string? ticket = values.FirstOrDefault();
                    if (!string.IsNullOrEmpty(ticket))
                        return ticket;
                }

                App.Logger.WriteLine(LOG_IDENT, $"GetAuthTicketAsync: no ticket header ({(int)resp.StatusCode} {resp.ReasonPhrase}).");
                return null;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Ticket", ex);
                return null;
            }
        }

        private static async Task<string?> GetCsrfTokenAsync(string cookie)
        {
            using var req = NewRequest(HttpMethod.Post, TicketEndpoint, cookie);
            req.Content = new StringContent("", Encoding.UTF8, "application/json");

            using var resp = await App.HttpClient.SendAsync(req);
            if (resp.Headers.TryGetValues("x-csrf-token", out var values))
                return values.FirstOrDefault();

            return null;
        }

        private class AuthedUserDto
        {
            public long Id { get; set; }
            public string? Name { get; set; }
            public string? DisplayName { get; set; }
        }

        private class ThumbnailListDto
        {
            public List<ThumbnailDto>? Data { get; set; }
        }

        private class ThumbnailDto
        {
            public string? ImageUrl { get; set; }
            public string? State { get; set; }
        }
    }
}
