using System.Text.RegularExpressions;

namespace BeastStrap.Utility
{
    public static class LaunchArgsUtility
    {
        // Deep-links surface placeId in many shapes — both as a direct query param and as
        // the hidden value inside the URL-encoded placelauncherurl that BeastStrap-style
        // protocol launches use:
        //   placeId=13700835620            (direct deep link)
        //   placeId%3D13700835620           (URL-encoded inside placelauncherurl)
        //   placeid:13700835620             (rare; some host registrations)
        //
        // We require at least 4 digits because the outer BeastStrap payload can include
        // unrelated short placeholders like "placeid:3" that aren't the real experience id.
        private static readonly Regex PlaceIdRegex = new(
            @"placeid[^0-9]+(\d{4,19})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        // Captures the value of the placelauncherurl key inside a BeastStrap-style launch
        // payload. Stops at "+", "&", "\"", or whitespace because the outer payload uses
        // "+" as the field separator and inner Roblox URLs are quoted.
        private static readonly Regex PlaceLauncherUrlRegex = new(
            @"placelauncherurl[:=]([^+&\s""]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        // Roblox VIP server access codes are GUIDs (36 chars, 8-4-4-4-12 hex with dashes).
        // Mirrors ActivityWatcher.GameJoiningPrivateServerPattern.
        private static readonly Regex AccessCodeRegex = new(
            @"accesscode[^0-9a-fA-F]+([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        // Each server in the rbxservers.xyz embed list links to /embedded/quicklaunch/{GUID}.
        // The page that loads is a click-tracking interstitial with a 7-second countdown
        // before it redirects to the actual Roblox launch URL. The GUID in that URL IS the
        // accessCode rbxservers eventually emits, so capturing it on click lets us skip the
        // countdown entirely and launch immediately.
        private static readonly Regex RbxServersQuickLaunchRegex = new(
            @"^https?://(?:www\.)?rbxservers\.xyz/embedded/quicklaunch/([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static long? TryExtractPlaceId(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine))
                return null;

            // Prefer the placelauncherurl path: it's the canonical source for web launches
            // and avoids small-integer false matches from the outer BeastStrap protocol payload.
            var launcherMatch = PlaceLauncherUrlRegex.Match(commandLine);
            if (launcherMatch.Success)
            {
                string launcherUrl;
                try { launcherUrl = Uri.UnescapeDataString(launcherMatch.Groups[1].Value); }
                catch { launcherUrl = launcherMatch.Groups[1].Value; }

                var inner = PlaceIdRegex.Match(launcherUrl);
                if (inner.Success && long.TryParse(inner.Groups[1].Value, out long innerId))
                    return innerId;
            }

            // Fall back to the general regex (covers direct roblox:// deep links).
            var match = PlaceIdRegex.Match(commandLine);
            if (!match.Success)
                return null;

            return long.TryParse(match.Groups[1].Value, out long placeId) ? placeId : null;
        }

        public static string? TryExtractAccessCode(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine))
                return null;

            var match = AccessCodeRegex.Match(commandLine);
            return match.Success ? match.Groups[1].Value : null;
        }

        public static string? TryExtractRbxServersQuickLaunchCode(string url)
        {
            if (string.IsNullOrEmpty(url))
                return null;

            var match = RbxServersQuickLaunchRegex.Match(url);
            return match.Success ? match.Groups[1].Value : null;
        }

        // Append (or replace) accessCode in launch args. Handles three input shapes:
        //   1. Args with no accessCode at all → append "&accessCode={code}"
        //   2. Args that already carry an accessCode → swap the existing value out
        //   3. Args without any "?" or "&" boundary → fall back to "&accessCode={code}"
        //      (Roblox's launcher tolerates a stray leading & on an otherwise-malformed URL,
        //       and we never produce that shape from a real launch URL anyway.)
        public static string AppendAccessCode(string commandLine, string accessCode)
        {
            if (string.IsNullOrEmpty(accessCode))
                return commandLine;

            if (string.IsNullOrEmpty(commandLine))
                return $"accessCode={accessCode}";

            var existing = AccessCodeRegex.Match(commandLine);
            if (existing.Success)
            {
                int valueStart = existing.Groups[1].Index;
                int valueLength = existing.Groups[1].Length;
                return commandLine.Substring(0, valueStart) + accessCode + commandLine.Substring(valueStart + valueLength);
            }

            return commandLine + "&accessCode=" + accessCode;
        }

        // True only for a plain "join any server of place X" launch — the one case where it's safe to
        // redirect to a specific server. Skips: no place / home screen (no placeId), VIP or private
        // servers (accessCode / RequestPrivateGame), and launches already targeting a specific server
        // (RequestGameJob, or a deeplink gameInstanceId).
        public static bool IsPlainPlaceJoin(string commandLine) => ExplainNotPlainPlaceJoin(commandLine) is null;

        /// <summary>
        /// Why <see cref="IsPlainPlaceJoin"/> said no, in words fit for a log line. Returns null
        /// when it IS a plain place join. Split out so the caller can tell the user which of the
        /// four conditions blocked a server redirect instead of just going quiet.
        /// </summary>
        public static string? ExplainNotPlainPlaceJoin(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine))
                return "no launch arguments were given (launched straight from the app, not from a game page)";

            if (TryExtractPlaceId(commandLine) is null)
                return "no placeId in the launch arguments — this is a home screen / app launch, not a game join";

            if (TryExtractAccessCode(commandLine) is not null)
                return "the launch carries an accessCode, so this is a VIP / private server";

            if (commandLine.Contains("RequestGameJob", StringComparison.OrdinalIgnoreCase)
                || commandLine.Contains("RequestPrivateGame", StringComparison.OrdinalIgnoreCase)
                || commandLine.Contains("gameInstanceId", StringComparison.OrdinalIgnoreCase))
                return "the launch already targets one specific server (RequestGameJob / RequestPrivateGame / gameInstanceId), for example a 'join friend' or a server-browser join";

            return null;
        }

        // Redirect a plain place-join to a specific server (jobId). For the protocol launch shape it
        // rewrites the encoded placelauncherurl's "request=RequestGame" -> "RequestGameJob" and appends
        // "&gameId=<jobId>"; for a roblox:// deeplink it appends "&gameInstanceId=<jobId>". Returns the
        // input unchanged if there's nothing to rewrite. Mirrors AppendAccessCode.
        public static string InjectGameJob(string commandLine, string jobId)
        {
            if (string.IsNullOrEmpty(commandLine) || string.IsNullOrEmpty(jobId))
                return commandLine;

            var launcherMatch = PlaceLauncherUrlRegex.Match(commandLine);
            if (launcherMatch.Success)
            {
                string encoded = launcherMatch.Groups[1].Value;

                string decoded;
                try { decoded = Uri.UnescapeDataString(encoded); }
                catch { decoded = encoded; }

                string rewritten = Regex.Replace(
                    decoded,
                    @"request=RequestGame(?![A-Za-z0-9])",
                    "request=RequestGameJob",
                    RegexOptions.IgnoreCase);

                if (!rewritten.Contains("gameId=", StringComparison.OrdinalIgnoreCase))
                    rewritten += "&gameId=" + jobId;

                string reEncoded = Uri.EscapeDataString(rewritten);
                int start = launcherMatch.Groups[1].Index;
                int len = launcherMatch.Groups[1].Length;
                return commandLine.Substring(0, start) + reEncoded + commandLine.Substring(start + len);
            }

            // roblox:// deeplink shape (rare on the bootstrapper path)
            if (commandLine.Contains("experiences/start", StringComparison.OrdinalIgnoreCase)
                && !commandLine.Contains("gameInstanceId", StringComparison.OrdinalIgnoreCase))
            {
                return commandLine + "&gameInstanceId=" + jobId;
            }

            return commandLine;
        }
    }
}
