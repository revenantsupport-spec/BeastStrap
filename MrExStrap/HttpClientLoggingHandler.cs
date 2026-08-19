using System.Net.Http;

namespace BeastStrap
{
    internal class HttpClientLoggingHandler : MessageProcessingHandler
    {
        // Headers that carry account secrets — never written to the log, even in debug mode.
        // The Multi Instance / account-manager flow sends .ROBLOSECURITY cookies and CSRF
        // tokens and gets back one-time auth tickets; logging any of those would leak a full
        // session into the log file (and the crash-export zip). Redact by header NAME.
        private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Cookie", "Set-Cookie", "X-CSRF-TOKEN", "rbx-authentication-ticket",
            "Authorization", "RBXAuthenticationNegotiation",

            // The TOOLS tabs send the user's own paid API keys through this same handler —
            // BypassToolsClient adds "x-api-key", LuaObfuscatorClient adds "apikey" and
            // "sessionId". Without these names here, turning on debug mode wrote a user's key
            // into the log, and the log goes into the diagnostic zip they hand to us.
            "apikey", "api-key", "x-api-key", "sessionId"
        };

        private static string FormatHeader(string key, IEnumerable<string> values)
            => SensitiveHeaders.Contains(key) ? $"  {key}: <redacted>" : $"  {key}: {string.Join(",", values)}";

        public HttpClientLoggingHandler(HttpMessageHandler innerHandler)
            : base(innerHandler)
        {
        }

        protected override HttpRequestMessage ProcessRequest(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Always log the basics. The verbose variant under DebugModeEnabled also dumps
            // request headers so users investigating "why is GitHub 403'ing me" can confirm
            // exactly what was sent over the wire.
            App.Logger.WriteLine("HttpClientLoggingHandler::ProcessRequest", $"{request.Method} {request.RequestUri}");

            if (App.Settings?.Prop?.DebugModeEnabled == true)
            {
                foreach (var header in request.Headers)
                    App.Logger.WriteLine("HttpClientLoggingHandler::RequestHeader", FormatHeader(header.Key, header.Value));
            }

            return request;
        }

        protected override HttpResponseMessage ProcessResponse(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            App.Logger.WriteLine("HttpClientLoggingHandler::ProcessResponse", $"{(int)response.StatusCode} {response.ReasonPhrase} {response.RequestMessage!.RequestUri}");

            if (App.Settings?.Prop?.DebugModeEnabled == true)
            {
                foreach (var header in response.Headers)
                    App.Logger.WriteLine("HttpClientLoggingHandler::ResponseHeader", FormatHeader(header.Key, header.Value));
                if (response.Content?.Headers != null)
                {
                    foreach (var header in response.Content.Headers)
                        App.Logger.WriteLine("HttpClientLoggingHandler::ResponseHeader", FormatHeader(header.Key, header.Value));
                }
            }

            return response;
        }
    }
}
