using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;

namespace BeastStrap.Utility
{
    public enum CheckStatus { Ok, Warn, Fail }

    public record CheckResult(string Category, string Name, CheckStatus Status, string Detail);

    // Non-destructive self-test. Every check is wrapped so a single failure never breaks the
    // run. Network checks are short-timeout so the whole pass finishes in under ~15 seconds
    // on a slow connection.
    public static class HealthCheck
    {
        private const string LOG_IDENT = "HealthCheck";

        public static async Task<List<CheckResult>> RunAllAsync()
        {
            var results = new List<CheckResult>();

            // Environment
            AddEnvironmentChecks(results);

            // Fork internals (no side effects — nothing gets truncated, closed, moved)
            AddForkInternalChecks(results);

            // Settings state
            AddSettingsChecks(results);

            // Network last — can stall longest
            await AddNetworkChecksAsync(results);

            return results;
        }

        public static string Render(IEnumerable<CheckResult> results)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"BeastStrap v{App.Version}{(App.IsPortableMode ? " (portable)" : "")} — health check");
            sb.AppendLine($"Run at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            string? currentCategory = null;
            int ok = 0, warn = 0, fail = 0;
            foreach (var r in results)
            {
                if (r.Category != currentCategory)
                {
                    currentCategory = r.Category;
                    sb.AppendLine();
                    sb.AppendLine($"== {currentCategory} ==");
                }

                string glyph = r.Status switch
                {
                    CheckStatus.Ok => "[ OK ]",
                    CheckStatus.Warn => "[WARN]",
                    CheckStatus.Fail => "[FAIL]",
                    _ => "[ ?  ]"
                };

                if (r.Status == CheckStatus.Ok) ok++;
                else if (r.Status == CheckStatus.Warn) warn++;
                else fail++;

                sb.AppendLine($"{glyph}  {r.Name}");
                if (!string.IsNullOrWhiteSpace(r.Detail))
                    sb.AppendLine($"        {r.Detail}");
            }

            sb.AppendLine();
            sb.AppendLine($"Summary: {ok} OK, {warn} warn, {fail} fail, {ok + warn + fail} total.");
            return sb.ToString();
        }

        private static void AddEnvironmentChecks(List<CheckResult> results)
        {
            const string cat = "Environment";

            results.Add(Safe(cat, "App version string parses",
                () =>
                {
                    Utilities.GetVersionFromString(App.Version);
                    return (CheckStatus.Ok, $"parsed '{App.Version}' successfully");
                }));

            results.Add(Safe(cat, "Install mode",
                () => App.IsPortableMode
                    ? (CheckStatus.Ok, $"portable, base = {Paths.Base}")
                    : (CheckStatus.Ok, $"normal install, base = {Paths.Base}")));

            results.Add(Safe(cat, "Base directory is writable", () => TestWritable(Paths.Base)));
            results.Add(Safe(cat, "Logs directory is writable", () => TestWritable(Paths.Logs)));
            results.Add(Safe(cat, "Versions directory exists",
                () => Directory.Exists(Paths.Versions)
                    ? (CheckStatus.Ok, Paths.Versions)
                    : (CheckStatus.Warn, "not yet created — will be created on first launch")));
            results.Add(Safe(cat, "Downloads directory exists",
                () => Directory.Exists(Paths.Downloads)
                    ? (CheckStatus.Ok, Paths.Downloads)
                    : (CheckStatus.Warn, "not yet created — will be created on first launch")));

            results.Add(Safe(cat, "Settings.json loaded",
                () => App.Settings.IsSaved
                    ? (CheckStatus.Ok, App.Settings.FileLocation)
                    : (CheckStatus.Warn, "in-memory only — save at least once")));

            results.Add(Safe(cat, "State.json loaded",
                () => App.State.IsSaved
                    ? (CheckStatus.Ok, App.State.FileLocation)
                    : (CheckStatus.Warn, "in-memory only — will persist on next save")));

            // Roblox's deepest content files sit ~170 characters below the version folder
            // (ExtraContent\LuaPackages\Packages\_Index\... measured against a real install), and
            // Win32 caps a path at 260 unless long paths are enabled. Custom Versions Manager
            // profiles use a 36-character GUID in the folder name, which is enough on its own to
            // push those files over the limit — extraction then fails file by file, silently.
            // Report the real headroom so it's visible in a diagnostics bundle instead of showing
            // up as an unexplained broken install.
            results.Add(Safe(cat, "Install path length headroom", () =>
            {
                const int RobloxDeepestTail = 170;
                const int MaxPath = 260;

                int longestProfileSegment = App.Settings.Prop.VersionProfiles.Count == 0
                    ? "profile-live-builtin".Length
                    : App.Settings.Prop.VersionProfiles.Max(p => ("profile-" + p.Id).Length);

                // Both roots. Roblox normally extracts into Versions\version-<hash>\, but when the
                // layout has to back off (another profile's install is locked by a running client)
                // EnsureActive hands back the profile's PARKED path and the bootstrapper extracts
                // there instead — so a parked path can carry the deep tree too. That matters MORE
                // now that ParkedVersions is a sibling of Versions: it is a longer string than
                // Versions, so the parked case is now the worse of the two.
                const int VersionFolderSegment = 8 + 16; // "version-" + the hash
                // +1 each for the separators either side of the folder segment.
                int activeWorst = Paths.Versions.Length + 1 + VersionFolderSegment + 1 + RobloxDeepestTail;
                int parkedWorst = Paths.ParkedVersions.Length + 1 + longestProfileSegment + 1 + RobloxDeepestTail;
                int worst = Math.Max(activeWorst, parkedWorst);
                int headroom = MaxPath - worst;

                string detail = $"deepest expected path ≈ {worst} chars vs the {MaxPath} limit "
                              + $"(active {activeWorst}, parked {parkedWorst}, longest profile folder {longestProfileSegment})";

                return headroom >= 0
                    ? (CheckStatus.Ok, $"{headroom} chars spare — {detail}")
                    : (CheckStatus.Warn, $"OVER by {-headroom} chars — {detail}. Deep Roblox files may fail to extract; "
                                       + "use a shorter install path or fewer custom version profiles.");
            }));

            results.Add(Safe(cat, ".NET runtime",
                () => (CheckStatus.Ok, RuntimeInformation.FrameworkDescription)));

            results.Add(Safe(cat, "OS version",
                () => (CheckStatus.Ok, RuntimeInformation.OSDescription)));
        }

        private static void AddForkInternalChecks(List<CheckResult> results)
        {
            const string cat = "Fork internals";

            results.Add(Safe(cat, "Version compare (self == self)",
                () =>
                {
                    var r = Utilities.CompareVersions(App.Version, "v" + App.Version);
                    return r == VersionComparison.Equal
                        ? (CheckStatus.Ok, $"420 vs v420 → Equal")
                        : (CheckStatus.Warn, $"self-compare returned {r}");
                }));

            results.Add(Safe(cat, "Version compare (future tag newer)",
                () =>
                {
                    var r = Utilities.CompareVersions(App.Version, "v999");
                    return r == VersionComparison.LessThan
                        ? (CheckStatus.Ok, $"{App.Version} < v999 → LessThan")
                        : (CheckStatus.Fail, $"expected LessThan, got {r}");
                }));

            results.Add(Safe(cat, "PrivacyMode cookie candidates enumerable",
                () =>
                {
                    string defaultPath = Path.Combine(Paths.LocalAppData, "Roblox", "LocalStorage", "RobloxCookies.dat");
                    bool exists = File.Exists(defaultPath);
                    return (CheckStatus.Ok,
                        exists
                            ? $"default path present at {defaultPath}"
                            : "default cookie path not present — fine if Roblox has never launched on this user");
                }));

            results.Add(Safe(cat, "MultiInstance P/Invoke reachable (non-destructive)",
                () =>
                {
                    // Only test that the ntdll import resolves and returns on a fake/invalid handle.
                    // We don't actually close anything.
                    IntPtr buf = Marshal.AllocHGlobal(0x1000);
                    try
                    {
                        uint status = NtQuerySystemInformation_Probe(64, buf, 0x1000, out int _);
                        // STATUS_INFO_LENGTH_MISMATCH is expected with a tiny buffer and means the API works.
                        return (status == 0 || status == 0xC0000004)
                            ? (CheckStatus.Ok, $"ntdll reachable (status 0x{status:X})")
                            : (CheckStatus.Warn, $"ntdll returned 0x{status:X}");
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buf);
                    }
                }));

            results.Add(Safe(cat, "WindowTiler can enumerate top-level windows",
                () =>
                {
                    int count = 0;
                    EnumWindows_Probe((_, _) => { count++; return true; }, IntPtr.Zero);
                    return count > 0
                        ? (CheckStatus.Ok, $"saw {count} top-level windows")
                        : (CheckStatus.Warn, "EnumWindows returned zero");
                }));

            results.Add(Safe(cat, "Roblox running instances",
                () =>
                {
                    int n = Process.GetProcessesByName("RobloxPlayerBeta").Length;
                    return (CheckStatus.Ok, $"{n} RobloxPlayerBeta process(es) running");
                }));
        }

        private static void AddSettingsChecks(List<CheckResult> results)
        {
            const string cat = "Settings";

            var s = App.Settings.Prop;
            results.Add(Safe(cat, "Pinned version",
                () =>
                {
                    if (!s.UseCustomVersion)
                        return (CheckStatus.Ok, "not pinned (following LIVE)");
                    if (string.IsNullOrWhiteSpace(s.CustomVersionGuid))
                        return (CheckStatus.Warn, "toggle on but no hash set");
                    if (!VersionGuidValidator.IsWellFormed(s.CustomVersionGuid))
                        return (CheckStatus.Fail, $"hash '{s.CustomVersionGuid}' isn't well-formed");
                    return (CheckStatus.Ok, s.CustomVersionGuid);
                }));

            results.Add(Safe(cat, "Privacy mode toggle",
                () => (CheckStatus.Ok, s.EnablePrivacyMode ? "on" : "off")));
            results.Add(Safe(cat, "Multi-instance toggle",
                () => (CheckStatus.Ok, s.MultiInstanceEnabled ? "on" : "off")));
            results.Add(Safe(cat, "Auto window tiling",
                () => (CheckStatus.Ok, s.WindowTilingEnabled ? $"on ({s.WindowTilingLayout})" : "off")));
            results.Add(Safe(cat, "LIVE channel toast",
                () => (CheckStatus.Ok, s.ShowLiveChannelToast ? "on" : "off")));
        }

        private static async Task AddNetworkChecksAsync(List<CheckResult> results)
        {
            const string cat = "Network";

            await ProbeAsync(results, cat, "Roblox client settings CDN",
                "https://clientsettingscdn.roblox.com/v2/client-version/WindowsPlayer");
            await ProbeAsync(results, cat, "Roblox setup CDN",
                "https://setup.rbxcdn.com/version");
            await ProbeAsync(results, cat, "Update server (release check)",
                $"{App.ProjectApiBase}/repos/{App.ProjectRepository}/releases/latest");
            await ProbeAsync(results, cat, "WEAO (executor match)",
                "https://weao.xyz/api/status/exploits");

            AddUpdateHistoryCheck(results, cat);
        }

        // The probe above says whether the update server answers right now. This says whether it
        // has EVER answered for this install — which is the question that matters when someone is
        // stuck on an old build. A copy whose endpoint died months ago looks perfectly healthy on
        // a one-shot probe run from a newer build.
        private static void AddUpdateHistoryCheck(List<CheckResult> results, string cat)
        {
            try
            {
                int streak = App.State.Prop.UpdateCheckFailureStreak;
                DateTime? last = App.State.Prop.LastSuccessfulUpdateCheckUtc;

                string detail = last is null
                    ? $"no update check has ever succeeded on this install ({streak} failed in a row)"
                    : $"last successful check {last:u} ({streak} failed since)";

                if (streak == 0)
                    results.Add(new CheckResult(cat, "Update check history", CheckStatus.Ok, detail));
                else if (streak < 5)
                    results.Add(new CheckResult(cat, "Update check history", CheckStatus.Warn, detail));
                else
                    results.Add(new CheckResult(cat, "Update check history", CheckStatus.Fail,
                        detail + $". This copy can't update itself — download the latest build from {App.ProjectDownloadLink}."));
            }
            catch (Exception ex)
            {
                results.Add(new CheckResult(cat, "Update check history", CheckStatus.Warn, ex.Message));
            }
        }

        private static async Task ProbeAsync(List<CheckResult> results, string cat, string name, string url)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await App.HttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                if ((int)resp.StatusCode >= 200 && (int)resp.StatusCode < 500)
                    results.Add(new CheckResult(cat, name, CheckStatus.Ok, $"HTTP {(int)resp.StatusCode} {resp.StatusCode}"));
                else
                    results.Add(new CheckResult(cat, name, CheckStatus.Warn, $"HTTP {(int)resp.StatusCode} {resp.StatusCode}"));
            }
            catch (TaskCanceledException)
            {
                results.Add(new CheckResult(cat, name, CheckStatus.Fail, "timed out after 3s"));
            }
            catch (Exception ex)
            {
                results.Add(new CheckResult(cat, name, CheckStatus.Fail, ex.Message));
            }
        }

        private static CheckResult Safe(string cat, string name, Func<(CheckStatus, string)> body)
        {
            try
            {
                var (status, detail) = body();
                return new CheckResult(cat, name, status, detail);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::" + name, ex);
                return new CheckResult(cat, name, CheckStatus.Fail, ex.Message);
            }
        }

        private static (CheckStatus, string) TestWritable(string dir)
        {
            if (string.IsNullOrEmpty(dir))
                return (CheckStatus.Fail, "path is empty");

            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, $".healthcheck-{Guid.NewGuid():N}.tmp");
                File.WriteAllText(probe, "probe");
                File.Delete(probe);
                return (CheckStatus.Ok, dir);
            }
            catch (Exception ex)
            {
                return (CheckStatus.Fail, ex.Message);
            }
        }

        #region P/Invoke (non-destructive probes)

        [DllImport("ntdll.dll", EntryPoint = "NtQuerySystemInformation")]
        private static extern uint NtQuerySystemInformation_Probe(int infoClass, IntPtr buffer, int length, out int returnLength);

        private delegate bool EnumWindowsProc_Probe(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "EnumWindows")]
        private static extern bool EnumWindows_Probe(EnumWindowsProc_Probe lpEnumFunc, IntPtr lParam);

        #endregion
    }
}
