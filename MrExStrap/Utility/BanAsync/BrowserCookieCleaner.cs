using Microsoft.Data.Sqlite;

namespace BeastStrap.Utility.BanAsync
{
    // Targeted cookie cleaner. Deletes ONLY the cookies whose host matches roblox.com
    // / rbxcdn.com (and a few related Roblox hostnames) from each installed browser's
    // cookie SQLite database. The user's other site cookies are untouched.
    //
    // Browsers lock their cookies DB while running, so we surface a friendly
    // "close the browser and try again" message instead of failing silently.
    public static class BrowserCookieCleaner
    {
        private const string LOG_IDENT = "BrowserCookieCleaner";

        // SQLite error codes for "database is busy/locked" — usually because the browser
        // has the cookies DB open with an exclusive lock.
        private const int SQLITE_BUSY = 5;
        private const int SQLITE_LOCKED = 6;

        // Chromium-family browsers store cookies under either
        //   %LocalAppData%\<Vendor>\<Product>\User Data\<Profile>\Network\Cookies   (Chrome-style)
        //   %AppData%\<Vendor>\<Product>\Network\Cookies                            (Opera-style)
        // The Root column picks the right AppData root and the discovery loop later checks
        // for cookies directly under the configured folder AND under profile subdirs, so both
        // layouts work.
        private enum AppDataRoot { Local, Roaming }

        private static readonly (string Name, AppDataRoot Root, string SubPath)[] ChromiumBrowsers =
        {
            ("Google Chrome",  AppDataRoot.Local,   @"Google\Chrome\User Data"),
            ("Microsoft Edge", AppDataRoot.Local,   @"Microsoft\Edge\User Data"),
            ("Brave",          AppDataRoot.Local,   @"BraveSoftware\Brave-Browser\User Data"),
            ("Opera",          AppDataRoot.Roaming, @"Opera Software\Opera Stable"),
            ("Opera GX",       AppDataRoot.Roaming, @"Opera Software\Opera GX Stable"),
            ("Vivaldi",        AppDataRoot.Local,   @"Vivaldi\User Data"),
            ("Chromium",       AppDataRoot.Local,   @"Chromium\User Data"),
        };

        public class Result
        {
            public int CookiesDeleted { get; set; }
            public int BrowsersScanned { get; set; }
            public int FilesScanned { get; set; }
            public List<string> Skipped { get; } = new();
        }

        public static Result ClearRobloxCookies(Action<string> log)
        {
            var result = new Result();
            string localAppData = Paths.LocalAppData;
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            foreach (var (name, root, subPath) in ChromiumBrowsers)
            {
                string rootDir = root == AppDataRoot.Local ? localAppData : appData;
                string userDataDir = Path.Combine(rootDir, subPath);
                if (!Directory.Exists(userDataDir))
                    continue;

                result.BrowsersScanned++;
                int browserTotal = 0;

                foreach (string cookieFile in EnumerateChromiumCookieFiles(userDataDir))
                {
                    result.FilesScanned++;
                    var (deleted, hosts, error) = TryDeleteFromDb(cookieFile, ChromiumSelectSql, ChromiumDeleteSql);
                    if (error != null)
                    {
                        string msg = $"{name}: skipped {Path.GetFileName(Path.GetDirectoryName(cookieFile) ?? cookieFile)} — {error}";
                        result.Skipped.Add(msg);
                        log(msg);
                        continue;
                    }
                    if (deleted > 0)
                        log($"{name}: cleared {deleted} cookie(s) for {hosts.Count} host(s) — {string.Join(", ", hosts)}");
                    browserTotal += deleted;
                }

                if (browserTotal == 0 && !result.Skipped.Any(s => s.StartsWith(name)))
                    log($"{name}: no Roblox cookies to clear");

                result.CookiesDeleted += browserTotal;
            }

            // Firefox uses a different schema (moz_cookies, host column) and lives under
            // %AppData%\Mozilla\Firefox\Profiles\<id>\cookies.sqlite.
            string firefoxProfiles = Path.Combine(appData, @"Mozilla\Firefox\Profiles");
            if (Directory.Exists(firefoxProfiles))
            {
                result.BrowsersScanned++;
                int ffTotal = 0;

                foreach (string profileDir in Directory.GetDirectories(firefoxProfiles))
                {
                    string cookieFile = Path.Combine(profileDir, "cookies.sqlite");
                    if (!File.Exists(cookieFile))
                        continue;

                    result.FilesScanned++;
                    var (deleted, hosts, error) = TryDeleteFromDb(cookieFile, FirefoxSelectSql, FirefoxDeleteSql);
                    if (error != null)
                    {
                        string msg = $"Firefox: skipped {Path.GetFileName(profileDir)} — {error}";
                        result.Skipped.Add(msg);
                        log(msg);
                        continue;
                    }
                    if (deleted > 0)
                        log($"Firefox: cleared {deleted} cookie(s) for {hosts.Count} host(s) — {string.Join(", ", hosts)}");
                    ffTotal += deleted;
                }

                if (ffTotal == 0 && !result.Skipped.Any(s => s.StartsWith("Firefox")))
                    log("Firefox: no Roblox cookies to clear");

                result.CookiesDeleted += ffTotal;
            }

            if (result.BrowsersScanned == 0)
                log("No supported browsers were found on this user account.");

            return result;
        }

        private static IEnumerable<string> EnumerateChromiumCookieFiles(string userDataDir)
        {
            // Opera-style layout: cookies sit directly under the configured folder, no
            // "Default" subdir. Check this first.
            string rootNew = Path.Combine(userDataDir, "Network", "Cookies");
            if (File.Exists(rootNew))
                yield return rootNew;
            else
            {
                string rootOld = Path.Combine(userDataDir, "Cookies");
                if (File.Exists(rootOld))
                    yield return rootOld;
            }

            // Chrome-style layout: cookies under each profile subdir (Default, Profile 1, …).
            string[] profileDirs;
            try
            {
                profileDirs = Directory.GetDirectories(userDataDir);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::EnumProfiles", ex);
                yield break;
            }

            foreach (string subdir in profileDirs)
            {
                string name = Path.GetFileName(subdir);
                bool looksLikeProfile =
                    name.Equals("Default", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Guest Profile", StringComparison.OrdinalIgnoreCase);
                if (!looksLikeProfile)
                    continue;

                // Newer Chromium puts cookies under Network\Cookies. Fall back to the
                // older flat layout if that file isn't there.
                string networkCookies = Path.Combine(subdir, "Network", "Cookies");
                if (File.Exists(networkCookies))
                {
                    yield return networkCookies;
                    continue;
                }

                string flatCookies = Path.Combine(subdir, "Cookies");
                if (File.Exists(flatCookies))
                    yield return flatCookies;
            }
        }

        // Patterns are anchored on purpose — the previous version used LIKE '%roblox.com%'
        // on both sides which would also match unrelated hosts like notroblox.com,
        // myroblox.com, or a phishing subdomain such as roblox.com.evil.net. The new
        // shape requires either exact equality with the apex OR ending in
        // ".roblox.com" — which is the actual cookie-host convention browsers use for
        // subdomains and Domain-attribute cookies. False positives are not possible.
        //
        // Chromium-family schema: table is `cookies`, hostname column is `host_key`.
        // host_key values look like "roblox.com" (host-only on apex) or ".roblox.com"
        // (Domain= cookie) or "www.roblox.com" (host-only on a subdomain). All three
        // shapes are covered by "= apex OR LIKE '%.apex'".
        private const string ChromiumMatchClause =
            "host_key = 'roblox.com'     OR host_key LIKE '%.roblox.com'     OR " +
            "host_key = 'rbxcdn.com'     OR host_key LIKE '%.rbxcdn.com'     OR " +
            "host_key = 'robloxlabs.com' OR host_key LIKE '%.robloxlabs.com'";

        private static readonly string ChromiumSelectSql =
            $"SELECT DISTINCT host_key FROM cookies WHERE {ChromiumMatchClause};";
        private static readonly string ChromiumDeleteSql =
            $"DELETE FROM cookies WHERE {ChromiumMatchClause};";

        // Firefox schema: table is `moz_cookies`, hostname column is `host`. Same
        // host-string conventions as Chromium so we apply the same anchored pattern.
        private const string FirefoxMatchClause =
            "host = 'roblox.com'     OR host LIKE '%.roblox.com'     OR " +
            "host = 'rbxcdn.com'     OR host LIKE '%.rbxcdn.com'     OR " +
            "host = 'robloxlabs.com' OR host LIKE '%.robloxlabs.com'";

        private static readonly string FirefoxSelectSql =
            $"SELECT DISTINCT host FROM moz_cookies WHERE {FirefoxMatchClause};";
        private static readonly string FirefoxDeleteSql =
            $"DELETE FROM moz_cookies WHERE {FirefoxMatchClause};";

        private static (int Deleted, IReadOnlyList<string> Hosts, string? Error) TryDeleteFromDb(string dbPath, string selectSql, string deleteSql)
        {
            try
            {
                var csb = new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadWrite,
                    Cache = SqliteCacheMode.Private
                };

                using var connection = new SqliteConnection(csb.ConnectionString);
                connection.Open();

                // 1. Pre-flight: gather the distinct hosts that the WHERE clause would match.
                //    This is the "show me exactly what you're about to delete" pass so we can
                //    log it before the destructive step. Same WHERE shape as the DELETE so
                //    there's no chance of the two queries disagreeing on what matches.
                var hosts = new List<string>();
                using (var selectCmd = connection.CreateCommand())
                {
                    selectCmd.CommandText = selectSql;
                    using var reader = selectCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        if (!reader.IsDBNull(0))
                            hosts.Add(reader.GetString(0));
                    }
                }

                if (hosts.Count == 0)
                    return (0, hosts, null);

                // 2. Destructive pass.
                using var deleteCmd = connection.CreateCommand();
                deleteCmd.CommandText = deleteSql;
                int deleted = deleteCmd.ExecuteNonQuery();
                return (deleted, hosts, null);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == SQLITE_BUSY || ex.SqliteErrorCode == SQLITE_LOCKED)
            {
                return (0, Array.Empty<string>(), "browser is open, cookie file is locked. Close the browser and run again.");
            }
            catch (SqliteException ex) when (ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
            {
                // Profile dir exists but cookies table isn't there — likely a fresh/empty profile.
                return (0, Array.Empty<string>(), null);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Delete", ex);
                return (0, Array.Empty<string>(), ex.Message);
            }
        }
    }
}
