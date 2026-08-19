// Adapted from robloxmanager by sasha / centerepic (MIT)
// https://gitlab.com/centerepic/robloxmanager
// Original behavior: truncate %LocalAppData%\Roblox\LocalStorage\RobloxCookies.dat
// to 0 bytes before launch so the client starts without the previous session's
// tracking cookies. This file is a C# port of that behavior with an extra
// best-effort sweep over BeastStrap's versioned install dirs.

namespace BeastStrap.Utility
{
    // Cookie-level "privacy mode" helper. Truncates Roblox's RobloxCookies.dat so the
    // next launch starts without any cached session-tracking cookies from the previous run.
    //
    // Important caveat (kept in sync with the UI copy on BeastStrapPage):
    //   This is NOT hardware-level privacy. It does NOT spoof MAC / HWID / machine GUID,
    //   and does NOT interfere with BanAsync fingerprinting. It only wipes the one cookie
    //   file Roblox uses to link browser and client sessions.
    public static class PrivacyMode
    {
        private const string LOG_IDENT = "PrivacyMode";
        private const string CookieFileName = "RobloxCookies.dat";

        // Best-effort: we truncate wherever Roblox might have written the cookie file on this
        // machine. That's the default-location install plus any BeastStrap-managed version dir
        // that happens to have a LocalStorage alongside it.
        public static void TruncateRobloxCookies()
        {
            try
            {
                foreach (var path in EnumerateCandidatePaths())
                {
                    try
                    {
                        if (!File.Exists(path))
                            continue;

                        File.WriteAllBytes(path, Array.Empty<byte>());
                        App.Logger.WriteLine(LOG_IDENT, $"Truncated {path}");
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteException(LOG_IDENT + "::Truncate", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                // Never let Privacy Mode block a launch — log and move on.
                App.Logger.WriteException(LOG_IDENT + "::Enumerate", ex);
            }
        }

        private static IEnumerable<string> EnumerateCandidatePaths()
        {
            // 1. Default-location Roblox client: %LocalAppData%\Roblox\LocalStorage
            string defaultLocalStorage = Path.Combine(Paths.LocalAppData, "Roblox", "LocalStorage");
            yield return Path.Combine(defaultLocalStorage, CookieFileName);

            // 2. Anything that happens to live under this BeastStrap install (Versions\<guid>\LocalStorage).
            //    We don't pin a single path — we recurse under Paths.Versions if it exists. Tolerant of
            //    multiple versioned installs coexisting.
            //
            //    Both roots, because inactive profiles' installs live OUTSIDE Versions in
            //    ParkedVersions. Sweeping only Versions would quietly stop clearing cookies for
            //    every profile the user isn't currently on — the opposite of what a privacy toggle
            //    is for.
            foreach (string root in new[] { Paths.Versions, Paths.ParkedVersions })
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                    continue;

                IEnumerable<string> matches = Array.Empty<string>();
                try
                {
                    matches = Directory.EnumerateFiles(root, CookieFileName, SearchOption.AllDirectories);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException(LOG_IDENT + "::EnumerateVersions", ex);
                }

                foreach (var m in matches)
                    yield return m;
            }
        }
    }
}
