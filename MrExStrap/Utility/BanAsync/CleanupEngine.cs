using Microsoft.Win32;

namespace BeastStrap.Utility.BanAsync
{
    // Sweeps Roblox-related files, registry, and prefetch entries off the machine.
    // Modeled on focat69/rblxswap's bypass:run handler but reimplemented in C# and scoped
    // so it never touches BeastStrap's own settings directory.
    public static class CleanupEngine
    {
        private const string LOG_IDENT = "CleanupEngine";

        // Roblox processes we'll terminate before deleting their files. We intentionally do NOT
        // kill third-party launchers (BeastStrap, Fishstrap, Voidstrap, Wbloxstrap) — those are
        // separate apps the user installed deliberately and may be mid-flight.
        private static readonly string[] RobloxProcessNames =
        {
            "RobloxPlayerBeta",
            "RobloxStudioBeta",
            "RobloxCrashHandler",
            "RobloxPlayerLauncher",
            "RobloxPlayerInstaller",
            "Roblox"
        };

        public class CleanupOptions
        {
            public bool PreserveInGameSettings { get; set; } = true;
            public bool PreserveFastFlags { get; set; } = true;
            public bool IncludeStudioFolders { get; set; } = false;

            // Also wipe BeastStrap's own Versions folder (the downloaded Roblox
            // installs under %LocalAppData%\BeastStrap\Versions). Forces a fresh
            // download on next launch. Off unless the user opts in.
            public bool CleanMrExVersions { get; set; } = false;
        }

        public class CleanupResult
        {
            public int DeletedDirectories { get; set; }
            public int DeletedFiles { get; set; }
            public int RegistryKeysRemoved { get; set; }
            public int PreservedFiles { get; set; }
            public List<string> Skipped { get; } = new();
        }

        public static CleanupResult RunCleanup(CleanupOptions options, Action<string> log)
        {
            var result = new CleanupResult();

            log("Closing Roblox processes…");
            int killed = KillProcesses(log);
            log(killed == 0 ? "No Roblox processes were running." : $"Closed {killed} Roblox process(es).");

            // Preserve step: copy out any files the user wants kept, before we wipe their parent dirs.
            var preserveBackup = new Dictionary<string, byte[]>();
            if (options.PreserveInGameSettings || options.PreserveFastFlags)
            {
                log("Backing up files to preserve…");
                preserveBackup = SnapshotPreservedFiles(options, log);
                result.PreservedFiles = preserveBackup.Count;
                log($"Held {preserveBackup.Count} file(s) in memory for restore.");
            }

            // Directory wipes.
            foreach (string path in BuildDirectoryTargets(options))
            {
                if (!Directory.Exists(path))
                    continue;

                try
                {
                    Directory.Delete(path, recursive: true);
                    result.DeletedDirectories++;
                    log($"Deleted directory {path}");
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException(LOG_IDENT + "::DeleteDir", ex);
                    result.Skipped.Add(path);
                    log($"Skipped {path}: {ex.Message}");
                }
            }

            // Optional: wipe BeastStrap's own downloaded Roblox installs (Versions).
            if (options.CleanMrExVersions)
                CleanMrExVersionsFolder(result, log);

            // Glob targets: %TEMP%\Roblox*
            string tempRoot = Path.GetTempPath();
            try
            {
                foreach (string dir in Directory.EnumerateDirectories(tempRoot, "Roblox*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        Directory.Delete(dir, recursive: true);
                        result.DeletedDirectories++;
                        log($"Deleted {dir}");
                    }
                    catch (Exception ex)
                    {
                        result.Skipped.Add(dir);
                        log($"Skipped {dir}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::TempGlob", ex);
            }

            // Prefetch — needs admin (system protected). We try anyway; if it fails we log.
            int prefetchDeleted = DeletePrefetch(log);
            result.DeletedFiles += prefetchDeleted;

            // Registry: HKCU\Software\ROBLOX Corporation.
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\ROBLOX Corporation", throwOnMissingSubKey: false);
                result.RegistryKeysRemoved++;
                log(@"Removed HKCU\Software\ROBLOX Corporation");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::RegistryHKCU", ex);
                log($"Couldn't remove HKCU\\Software\\ROBLOX Corporation: {ex.Message}");
            }

            // Restore preserved files.
            if (preserveBackup.Count > 0)
            {
                log("Restoring preserved files…");
                foreach (var (path, bytes) in preserveBackup)
                {
                    try
                    {
                        string? dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir))
                            Directory.CreateDirectory(dir);
                        File.WriteAllBytes(path, bytes);
                        log($"Restored {path}");
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteException(LOG_IDENT + "::Restore", ex);
                        log($"Failed to restore {path}: {ex.Message}");
                    }
                }
            }

            return result;
        }

        // Wipe BeastStrap's own Versions folder (the downloaded Roblox installs).
        // Junction-aware: unlink reparse points without recursing into their targets so a
        // recursive delete can never clobber a per-profile dir through a junction
        // (the v420.25 lesson). Real per-profile dirs are deleted recursively.
        private static void CleanMrExVersionsFolder(CleanupResult result, Action<string> log)
        {
            // Both roots: parked installs live OUTSIDE Versions in ParkedVersions. Wiping only
            // Versions would leave one whole Roblox install per profile behind, which for a
            // "remove every trace" action is the wrong answer.
            CleanVersionsRoot(Paths.Versions, "Versions", result, log);
            CleanVersionsRoot(Paths.ParkedVersions, "parked versions", result, log);
        }

        private static void CleanVersionsRoot(string root, string label, CleanupResult result, Action<string> log)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                log($"No BeastStrap {label} folder to clean.");
                return;
            }

            log($"Wiping BeastStrap {label} folder {root}…");

            foreach (string child in Directory.EnumerateDirectories(root))
            {
                try
                {
                    if (VersionJunctionManager.IsJunction(child))
                        Directory.Delete(child, recursive: false);
                    else
                        Directory.Delete(child, recursive: true);

                    result.DeletedDirectories++;
                    log($"Deleted {child}");
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException(LOG_IDENT + "::CleanVersions", ex);
                    result.Skipped.Add(child);
                    log($"Skipped {child}: {ex.Message}");
                }
            }

            foreach (string file in Directory.EnumerateFiles(root))
            {
                try { File.Delete(file); result.DeletedFiles++; }
                catch (Exception ex) { App.Logger.WriteException(LOG_IDENT + "::CleanVersionsFile", ex); }
            }
        }

        private static int KillProcesses(Action<string> log)
        {
            int killed = 0;
            foreach (string name in RobloxProcessNames)
            {
                Process[] procs;
                try { procs = Process.GetProcessesByName(name); }
                catch { continue; }

                foreach (var p in procs)
                {
                    try
                    {
                        p.Kill();
                        p.WaitForExit(2000);
                        killed++;
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteException(LOG_IDENT + "::Kill", ex);
                        log($"Couldn't kill {name} (pid {p.Id}): {ex.Message}");
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }
            return killed;
        }

        private static IEnumerable<string> BuildDirectoryTargets(CleanupOptions options)
        {
            string localAppData = Paths.LocalAppData;
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

            // Player install + cache + logs in the default location.
            yield return Path.Combine(localAppData, "Roblox");

            // The "logs" and "http" caches sometimes live under Roaming AppData.
            yield return Path.Combine(appData, "Roblox", "logs");
            yield return Path.Combine(appData, "Roblox", "http");

            // Machine-wide Roblox state.
            yield return Path.Combine(programData, "Roblox");

            if (options.IncludeStudioFolders)
            {
                yield return Path.Combine(localAppData, "Roblox", "Studio");
                yield return Path.Combine(appData, "Roblox", "Studio");
            }
        }

        private static Dictionary<string, byte[]> SnapshotPreservedFiles(CleanupOptions options, Action<string> log)
        {
            var snapshot = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            string localAppData = Paths.LocalAppData;
            string robloxRoot = Path.Combine(localAppData, "Roblox");

            if (!Directory.Exists(robloxRoot))
                return snapshot;

            if (options.PreserveInGameSettings)
            {
                // GlobalBasicSettings_*.xml typically lives at the Roblox root.
                TryCaptureMatching(robloxRoot, "GlobalBasicSettings_*.xml", snapshot, log);
            }

            if (options.PreserveFastFlags)
            {
                // Roblox stores ClientAppSettings.json inside ClientSettings under each version dir.
                TryCaptureMatching(robloxRoot, "ClientAppSettings.json", snapshot, log);
                TryCaptureMatching(robloxRoot, "ClientSettings.json", snapshot, log);
            }

            return snapshot;
        }

        // Cap per-file size to avoid OOM on weird/corrupt files. The real config files we want
        // to preserve (GlobalBasicSettings_*.xml, ClientAppSettings.json) are typically tens of KB
        // and never legitimately above a few MB.
        private const long MaxPreserveFileBytes = 16L * 1024 * 1024;

        private static void TryCaptureMatching(string root, string pattern, Dictionary<string, byte[]> snapshot, Action<string> log)
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.Length > MaxPreserveFileBytes)
                        {
                            log($"Skipping preserve for {file} — file is {info.Length / (1024 * 1024)} MB (cap: {MaxPreserveFileBytes / (1024 * 1024)} MB)");
                            continue;
                        }

                        snapshot[file] = File.ReadAllBytes(file);
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteException(LOG_IDENT + "::Capture", ex);
                        log($"Couldn't read {file} for preserve: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::CaptureEnum", ex);
            }
        }

        private static int DeletePrefetch(Action<string> log)
        {
            int count = 0;
            string prefetchDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
            if (!Directory.Exists(prefetchDir))
                return 0;

            string[] patterns = { "ROBLOXCRASHHANDLER.EXE-*.pf", "ROBLOXPLAYERBETA.EXE-*.pf" };

            foreach (string pattern in patterns)
            {
                try
                {
                    foreach (string file in Directory.EnumerateFiles(prefetchDir, pattern))
                    {
                        try
                        {
                            File.Delete(file);
                            count++;
                            log($"Deleted prefetch {Path.GetFileName(file)}");
                        }
                        catch (Exception ex)
                        {
                            // Prefetch is protected — admin usually required.
                            App.Logger.WriteException(LOG_IDENT + "::Prefetch", ex);
                            log($"Couldn't delete prefetch {Path.GetFileName(file)} (admin required): {ex.Message}");
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    log("Skipped prefetch — needs administrator privileges.");
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException(LOG_IDENT + "::PrefetchEnum", ex);
                }
            }
            return count;
        }
    }
}
