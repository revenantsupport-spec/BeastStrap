namespace BeastStrap.Utility.Accounts
{
    // Launch technique adapted from robloxmanager by sasha / centerepic (MIT) —
    // https://gitlab.com/centerepic/robloxmanager. Each launch mints a fresh one-time ticket from
    // the stored cookie (see RobloxAuth) and re-enters our OWN bootstrapper through the
    // roblox-player: protocol, so every alt still gets the LIVE-channel lock, FastFlags, mods,
    // privacy mode, the singleton-close and window tiling.
    public static class AccountLauncher
    {
        private const string LOG_IDENT = "AccountLauncher";

        // Launch one account. With launchToHome the client opens to the Roblox home screen and
        // placeId/jobId are ignored; otherwise it joins the given place (and optional server job).
        // Returns true if a launch process was spawned.
        public static async Task<bool> LaunchAsync(RobloxAccount account, long placeId, string? jobId, bool launchToHome = false)
        {
            string? cookie = SecureStore.Unprotect(account.EncryptedCookieB64);
            if (string.IsNullOrEmpty(cookie))
            {
                App.Logger.WriteLine(LOG_IDENT, $"Could not decrypt the cookie for {account.DisplayLabel}; skipping.");
                return false;
            }

            string? ticket = await RobloxAuth.GetAuthTicketAsync(cookie);
            if (string.IsNullOrEmpty(ticket))
            {
                App.Logger.WriteLine(LOG_IDENT, $"Could not get a launch ticket for {account.DisplayLabel} (cookie expired?).");
                return false;
            }

            string uri = launchToHome ? BuildHomeLaunchUri(ticket) : BuildLaunchUri(ticket, placeId, jobId);

            // Always force multi-instance for account launches. Without it, a launch fired while
            // another client is open gets absorbed by that client and runs as whoever is already
            // logged in there — not this account. The flag makes our bootstrapper neutralise
            // Roblox's single-instance lock so this client starts on its own and redeems this
            // account's ticket. Independent of the user's global toggle.
            string args = $"{uri} -multiinstance";

            // Per-account version: launch under this account's assigned Versions Manager profile,
            // as a per-launch override the bootstrapper reads. Empty = use the global active one.
            if (!string.IsNullOrEmpty(account.VersionProfileId))
                args += $" -versionprofile {account.VersionProfileId}";

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Paths.Application,
                    Arguments = args,
                    UseShellExecute = false,
                    WorkingDirectory = Paths.Base
                });
                App.Logger.WriteLine(LOG_IDENT, $"Launched {account.DisplayLabel} {(launchToHome ? "to home" : $"into place {placeId}{(string.IsNullOrEmpty(jobId) ? "" : $" job {jobId}")}")}.");
                return true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                return false;
            }
        }

        // Sequential bulk launch with a per-account delay. The delay is REQUIRED, not cosmetic:
        //   - the log file is named to the second, so two launches in the same second collide and
        //     the later BeastStrap self-terminates as a "duplicate launch";
        //   - it also throttles the auth-ticket calls for rate-limited IPs.
        // (The singleton lock itself is no longer a timing concern — it's held up front by the
        // bootstrapper/watcher before each client starts, see Utility.MultiInstance.)
        // Minimum is clamped to 2s. Returns how many launched.
        public static async Task<int> BulkLaunchAsync(IReadOnlyList<RobloxAccount> accounts, long placeId, string? jobId, int delaySeconds, bool launchToHome = false, IProgress<string>? progress = null)
        {
            // No need to flip the user's global multi-instance toggle anymore — every account
            // launch passes -multiinstance (see LaunchAsync), so bulk launches start independent
            // clients regardless of the saved setting.
            delaySeconds = Math.Max(2, delaySeconds);
            int launched = 0;

            for (int i = 0; i < accounts.Count; i++)
            {
                var account = accounts[i];
                progress?.Report($"Launching {account.DisplayLabel} ({i + 1}/{accounts.Count})…");

                if (await LaunchAsync(account, placeId, jobId, launchToHome))
                    launched++;

                if (i < accounts.Count - 1)
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            }

            progress?.Report($"Done — launched {launched} of {accounts.Count}.");
            App.Logger.WriteLine(LOG_IDENT, $"Bulk launch finished: {launched}/{accounts.Count}.");
            return launched;
        }

        // Home/app launch: open the Roblox app to the home screen, authenticated as this account,
        // without joining a game. launchmode:app + the auth ticket and NO placelauncherurl is what
        // tells the client to land on home. Useful to pass a login/bot check before joining, and
        // for accounts you just want signed in.
        private static string BuildHomeLaunchUri(string ticket)
        {
            long launchTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long tracker = Random.Shared.NextInt64(10_000_000, 200_000_000);

            return $"roblox-player:1+launchmode:app+gameinfo:{ticket}+launchtime:{launchTime}"
                 + $"+browsertrackerid:{tracker}+robloxLocale:en_us+gameLocale:en_us+channel:";
        }

        private static string BuildLaunchUri(string ticket, long placeId, string? jobId)
        {
            long launchTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // The placelauncherurl is percent-encoded so its inner &/= don't break the +-delimited
            // outer protocol string. RequestGame joins any/new server; RequestGameJob targets a
            // specific server so a group of alts lands together.
            string placeLauncher = string.IsNullOrEmpty(jobId)
                ? $"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestGame&browserTrackerId=0&placeId={placeId}&isPlayTogetherGame=false"
                : $"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestGameJob&browserTrackerId=0&placeId={placeId}&gameId={jobId}&isPlayTogetherGame=false";

            string encoded = Uri.EscapeDataString(placeLauncher);
            long tracker = Random.Shared.NextInt64(10_000_000, 200_000_000);

            return $"roblox-player:1+launchmode:play+gameinfo:{ticket}+launchtime:{launchTime}"
                 + $"+placelauncherurl:{encoded}+browsertrackerid:{tracker}"
                 + "+robloxLocale:en_us+gameLocale:en_us+channel:";
        }
    }
}
