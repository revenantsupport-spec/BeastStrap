using System.Text.RegularExpressions;

namespace BeastStrap.Utility
{
    // Strips personal identifiers out of anything that leaves the machine — right now that's the
    // diagnostic export zip, plus any log line that quotes a raw launch command.
    //
    // The Windows account name is the awkward one: it shows up as a bare value in environment.txt,
    // inside every "C:\Users\<name>\..." path in our logs, doubled-up as "C:\\Users\\<name>\\..."
    // in the JSON dumps, and again in Roblox's own client logs. Rather than chase each shape, we
    // replace the username TOKEN wherever it appears, which covers all of them at once.
    public static class LogScrubber
    {
        public const string UserNamePlaceholder = "%USERNAME%";
        public const string ProfilePlaceholder = "%UserProfile%";

        // Built once. Null when there's no name worth matching (see BuildUserNameRegex).
        private static readonly Regex? UserNameRegex = BuildUserNameRegex();

        private static Regex? BuildUserNameRegex()
        {
            string name;
            try { name = Environment.UserName; }
            catch { return null; }

            // Names shorter than three characters are too collision-prone to blind-replace — a
            // user called "Al" would shred every unrelated "Al" in a Roblox log. Their profile
            // path still gets normalized below, which is the part that actually leaks it.
            if (string.IsNullOrWhiteSpace(name) || name.Length < 3)
                return null;

            // Explicit lookarounds instead of \b: usernames can end in "." or "$", where \b
            // flips meaning and stops matching.
            return new Regex(
                $"(?<![A-Za-z0-9]){Regex.Escape(name)}(?![A-Za-z0-9])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        }

        // Launch URIs carry two secrets we never want in a log: the one-time auth ticket
        // (gameinfo:) and the private-server access code, which is shareable as-is.
        private static readonly Regex LaunchSecretsRegex = new(
            @"(gameinfo:|accesscode[:=])([^+&\s""]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>
        /// Removes the Windows account name from a block of text. Safe on null/empty.
        /// </summary>
        public static string ScrubUserName(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return text ?? "";

            string result = text;

            // Normalize the full profile path first so the common case reads as "%UserProfile%\..."
            // rather than "C:\Users\%USERNAME%\...".
            try
            {
                if (!string.IsNullOrEmpty(Paths.UserProfile))
                    result = result.Replace(Paths.UserProfile, ProfilePlaceholder, StringComparison.InvariantCultureIgnoreCase);
            }
            catch { /* Paths may not be initialized during an early-startup crash export */ }

            if (UserNameRegex is not null)
                result = UserNameRegex.Replace(result, UserNamePlaceholder);

            return result;
        }

        /// <summary>
        /// A launch command line that's safe to write to the log — auth ticket and private-server
        /// access code redacted, username stripped.
        /// </summary>
        public static string ScrubLaunchArgs(string? commandLine)
        {
            if (string.IsNullOrEmpty(commandLine))
                return "(empty)";

            string redacted = LaunchSecretsRegex.Replace(commandLine, "$1<redacted>");
            return ScrubUserName(redacted);
        }
    }
}
