using System.Windows;
using Microsoft.Win32;

namespace BeastStrap
{
    internal class Installer
    {
        /// <summary>
        /// Should the release notes open when updating to this version?
        /// </summary>
        private const bool OpenReleaseNotes = true;
        /// <summary>
        /// Which version's release notes to open
        /// Leave blank to use the current version
        /// </summary>
        private const string ForcedReleaseNotesVersion = "2.11.2";

        private static string DesktopShortcut => Path.Combine(Paths.Desktop, $"{App.ProjectName}.lnk");

        private static string StartMenuShortcut => Path.Combine(Paths.WindowsStartMenu, $"{App.ProjectName}.lnk");

        public string InstallLocation = Path.Combine(Paths.LocalAppData, App.ProjectName);

        public bool ExistingDataPresent => File.Exists(Path.Combine(InstallLocation, "Settings.json"));

        public bool CreateDesktopShortcuts = true;

        public bool CreateStartMenuShortcuts = true;

        public bool EnableAnalytics = true;

        public bool IsImplicitInstall = false;

        public string InstallLocationError { get; set; } = "";

        public void DoInstall()
        {
            const string LOG_IDENT = "Installer::DoInstall";

            App.Logger.WriteLine(LOG_IDENT, "Beginning installation");

            // should've been created earlier from the write test anyway
            Directory.CreateDirectory(InstallLocation);

            Paths.Initialize(InstallLocation);

            if (!IsImplicitInstall)
            {
                Filesystem.AssertReadOnly(Paths.Application);

                try
                {
                    // Skip the copy when the user pointed the installer at the folder the running
                    // exe already lives in. Source and destination are then the same file, and
                    // File.Copy fails with a sharing violation — which the catch below turns into
                    // "close BeastStrap fully and try again" plus a hard Terminate, advice the
                    // user cannot possibly act on. App.xaml.cs and HandleUpgrade already guard
                    // their own copies this way.
                    if (!string.Equals(Path.GetFullPath(Paths.Process), Path.GetFullPath(Paths.Application), StringComparison.OrdinalIgnoreCase))
                        DeployExecutable();
                    else
                        App.Logger.WriteLine(LOG_IDENT, "Install location already holds the running executable — nothing to copy.");
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Could not overwrite executable at {Paths.Application}");
                    App.Logger.WriteException(LOG_IDENT, ex);

                    // Surface the actual reason — "Couldn't overwrite the installed exe" alone
                    // leaves the user guessing between permission denied, disk full, or path
                    // issues. The original ex.Message is the most useful single line we can show.
                    string hint = ex switch
                    {
                        UnauthorizedAccessException => "Permission denied. Try running BeastStrap as administrator, or check that no antivirus is locking the install folder.",
                        IOException io when io.HResult == unchecked((int)0x80070070) => "The target drive is out of space. Free some up and retry.",
                        IOException => "The installed exe may be in use. Close BeastStrap fully (including any background-updater process) and retry.",
                        _ => "See the log file for the full stack trace."
                    };

                    string body = $"{Strings.Installer_Install_CannotOverwrite}\n\n" +
                                  $"Reason: {ex.GetType().Name}: {ex.Message}\n\n{hint}";

                    Frontend.ShowMessageBox(body, MessageBoxImage.Error);
                    App.Terminate(ErrorCode.ERROR_INSTALL_FAILURE);
                }
            }

            using (var uninstallKey = Registry.CurrentUser.CreateSubKey(App.UninstallKey))
            {
                uninstallKey.SetValueSafe("DisplayIcon", $"{Paths.Application},0");
                uninstallKey.SetValueSafe("DisplayName", App.ProjectName);

                uninstallKey.SetValueSafe("DisplayVersion", App.Version);

                if (uninstallKey.GetValue("InstallDate") is null)
                    uninstallKey.SetValueSafe("InstallDate", DateTime.Now.ToString("yyyyMMdd"));

                uninstallKey.SetValueSafe("InstallLocation", Paths.Base);
                uninstallKey.SetValueSafe("NoRepair", 1);
                uninstallKey.SetValueSafe("Publisher", App.ProjectOwner);
                uninstallKey.SetValueSafe("ModifyPath", $"\"{Paths.Application}\" -settings");
                uninstallKey.SetValueSafe("QuietUninstallString", $"\"{Paths.Application}\" -uninstall -quiet");
                uninstallKey.SetValueSafe("UninstallString", $"\"{Paths.Application}\" -uninstall");
                uninstallKey.SetValueSafe("HelpLink", App.ProjectHelpLink);
                uninstallKey.SetValueSafe("URLInfoAbout", App.ProjectSupportLink);
                uninstallKey.SetValueSafe("URLUpdateInfo", App.ProjectDownloadLink);
            }

            // only register player, for the scenario where the user installs bloxstrap, closes it,
            // and then launches from the website expecting it to work
            // studio can be implicitly registered when it's first launched manually or if its configuration files are present
            WindowsRegistry.RegisterPlayer();

            if (App.IsStudioInstalled)
                WindowsRegistry.RegisterStudio();

            if (CreateDesktopShortcuts)
                Shortcut.Create(Paths.Application, "", DesktopShortcut);

            if (CreateStartMenuShortcuts)
                Shortcut.Create(Paths.Application, "", StartMenuShortcut);

            // existing configuration persisting from an earlier install
            App.Settings.Load(false);
            App.State.Load(false);
            App.FastFlags.Load(false);

            App.Settings.Prop.EnableAnalytics = EnableAnalytics;

            App.Settings.Save();

            App.Logger.WriteLine(LOG_IDENT, "Installation finished");

            if (!IsImplicitInstall)
                App.SendStat("installAction", "install");
        }

        // BeastStrap is released as a single self-contained executable, but local and debug
        // builds are framework-dependent: the apphost exe needs BeastStrap.dll and its whole
        // dependency set beside it. Deploying only the exe then leaves the installed copy
        // unstartable ("The application to execute does not exist"), which silently kills every
        // process spawned from Paths.Application — account launches, watchers, the
        // roblox-player: protocol handler, shortcuts. So deploy the runnable set: the exe plus,
        // for framework-dependent builds, the dlls/json/runtimes/culture folders it loads.
        public static void DeployExecutable()
        {
            string processDir = Path.GetDirectoryName(Paths.Process)!;
            string assemblyName = Path.GetFileNameWithoutExtension(Paths.Application);
            string assemblyPath = Path.Combine(processDir, assemblyName + ".dll");

            if (!File.Exists(assemblyPath))
            {
                File.Copy(Paths.Process, Paths.Application, true);
                return;
            }

            // Framework-dependent build: copy the exe and the runnable set beside it. The
            // win-x64 subfolder only exists in bin output (it holds the single-file publish
            // result) and is not part of the framework-dependent runnable set.
            File.Copy(Paths.Process, Paths.Application, true);

            foreach (string file in Directory.GetFiles(processDir))
            {
                if (string.Equals(Path.GetFileName(file), Path.GetFileName(Paths.Application), StringComparison.OrdinalIgnoreCase))
                    continue;

                File.Copy(file, Path.Combine(Paths.Base, Path.GetFileName(file)), true);
            }

            foreach (string folder in Directory.GetDirectories(processDir))
            {
                if (string.Equals(Path.GetFileName(folder), "win-x64", StringComparison.OrdinalIgnoreCase))
                    continue;

                string targetRoot = Path.Combine(Paths.Base, Path.GetFileName(folder));
                Directory.CreateDirectory(targetRoot);

                foreach (string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(folder, file);
                    string target = Path.Combine(targetRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, true);
                }
            }
        }

        private bool ValidateLocation()
        {
            // prevent from installing to the root of a drive
            if (InstallLocation.Length <= 3)
                return false;

            // unc path, just to be safe
            if (InstallLocation.StartsWith("\\\\"))
                return false;

            if (InstallLocation.StartsWith(Path.GetTempPath(), StringComparison.InvariantCultureIgnoreCase)
                || InstallLocation.Contains("\\Temp\\", StringComparison.InvariantCultureIgnoreCase))
                return false;

            // prevent from installing to a onedrive folder
            if (InstallLocation.Contains("OneDrive", StringComparison.InvariantCultureIgnoreCase))
                return false;

            // prevent from installing to an essential user profile folder (e.g. Documents, Downloads, Contacts idk)
            if (String.Compare(Directory.GetParent(InstallLocation)?.FullName, Paths.UserProfile, StringComparison.InvariantCultureIgnoreCase) == 0)
                return false;

            // prevent from installing into the program files folder
            if (InstallLocation.Contains("Program Files"))
                return false;

            return true;
        }

        public bool CheckInstallLocation()
        {
            if (string.IsNullOrEmpty(InstallLocation))
            {
                InstallLocationError = Strings.Menu_InstallLocation_NotSet;
            }
            else if (!ValidateLocation())
            {
                InstallLocationError = Strings.Menu_InstallLocation_CantInstall;
            }
            else
            {
                if (!IsImplicitInstall 
                    && !InstallLocation.EndsWith(App.ProjectName, StringComparison.InvariantCultureIgnoreCase)
                    && Directory.Exists(InstallLocation)
                    && Directory.EnumerateFileSystemEntries(InstallLocation).Any())
                {
                    string suggestedChange = Path.Combine(InstallLocation, App.ProjectName);

                    MessageBoxResult result = Frontend.ShowMessageBox(
                        String.Format(Strings.Menu_InstallLocation_NotEmpty, suggestedChange),
                        MessageBoxImage.Warning,
                        MessageBoxButton.YesNoCancel,
                        MessageBoxResult.Yes
                    );

                    if (result == MessageBoxResult.Yes)
                        InstallLocation = suggestedChange;
                    else if (result == MessageBoxResult.Cancel || result == MessageBoxResult.None)
                        return false;
                }

                try
                {
                    // check if we can write to the directory (a bit hacky but eh)
                    string testFile = Path.Combine(InstallLocation, $"{App.ProjectName}WriteTest.txt");

                    Directory.CreateDirectory(InstallLocation);
                    File.WriteAllText(testFile, "");
                    File.Delete(testFile);
                }
                catch (UnauthorizedAccessException)
                {
                    InstallLocationError = Strings.Menu_InstallLocation_NoWritePerms;
                }
                catch (Exception ex)
                {
                    InstallLocationError = ex.Message;
                }
            }

            return String.IsNullOrEmpty(InstallLocationError);
        }

        public static void DoUninstall(bool keepData)
        {
            const string LOG_IDENT = "Installer::DoUninstall";

            var processes = new List<Process>();
            
            if (!String.IsNullOrEmpty(App.PlayerState.Prop.VersionGuid))
                processes.AddRange(Process.GetProcessesByName(App.RobloxPlayerAppName));

            if (App.IsStudioInstalled)
                processes.AddRange(Process.GetProcessesByName(App.RobloxStudioAppName));

            // prompt to shutdown roblox if its currently running
            if (processes.Any())
            {
                var result = Frontend.ShowMessageBox(
                    Strings.Bootstrapper_Uninstall_RobloxRunning,
                    MessageBoxImage.Information,
                    MessageBoxButton.OKCancel,
                    MessageBoxResult.OK
                );

                if (result != MessageBoxResult.OK)
                {
                    App.Terminate(ErrorCode.ERROR_CANCELLED);
                    return;
                }

                try
                {
                    foreach (var process in processes)
                    {
                        process.Kill();
                        process.Close();
                    }
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Failed to close process! {ex}");
                }
            }

            string robloxFolder = Path.Combine(Paths.LocalAppData, "Roblox");
            bool playerStillInstalled = true;
            bool studioStillInstalled = true;

            // check if stock bootstrapper is still installed
            using var playerKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\roblox-player");
            var playerFolder = playerKey?.GetValue("InstallLocation");

            if (playerKey is null || playerFolder is not string)
            {
                playerStillInstalled = false;

                WindowsRegistry.Unregister("roblox");
                WindowsRegistry.Unregister("roblox-player");
            }
            else
            {
                string playerPath = Path.Combine((string)playerFolder, "RobloxPlayerBeta.exe");

                WindowsRegistry.RegisterPlayer(playerPath, "%1");
            }

            using var studioKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\roblox-studio");
            var studioFolder = studioKey?.GetValue("InstallLocation");

            if (studioKey is null || studioFolder is not string)
            {
                studioStillInstalled = false;

                WindowsRegistry.Unregister("roblox-studio");
                WindowsRegistry.Unregister("roblox-studio-auth");

                WindowsRegistry.Unregister("Roblox.Place");
                WindowsRegistry.Unregister(".rbxl");
                WindowsRegistry.Unregister(".rbxlx");
            }
            else
            {
                string studioPath = Path.Combine((string)studioFolder, "RobloxStudioBeta.exe");
                string studioLauncherPath = Path.Combine((string)studioFolder, "RobloxStudioLauncherBeta.exe");

                WindowsRegistry.RegisterStudioProtocol(studioPath, "%1");
                WindowsRegistry.RegisterStudioFileClass(studioPath, "-ide \"%1\"");
            }

            var cleanupSequence = new List<Action>
            {
                () =>
                {
                    // Per-file try/catch: the .lnk parser throws on a truncated or non-shortcut
                    // file that merely ends in .lnk, and with the catch outside the loop the very
                    // first bad one aborted the whole sweep — leaving BeastStrap's own shortcut
                    // on the desktop after an uninstall.
                    foreach (var file in Directory.GetFiles(Paths.Desktop).Where(x => x.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)))
                    {
                        try
                        {
                            var shortcut = ShellLink.Shortcut.ReadFromFile(file);

                            if (string.Equals(shortcut.ExtraData.EnvironmentVariableDataBlock?.TargetUnicode, Paths.Application, StringComparison.OrdinalIgnoreCase))
                                File.Delete(file);
                        }
                        catch (Exception ex)
                        {
                            App.Logger.WriteException("Installer::Uninstall::DesktopShortcut", ex);
                        }
                    }
                },

                () => File.Delete(StartMenuShortcut),

                () => Directory.Delete(Paths.Versions, true),
                // Parked installs live OUTSIDE Versions. Without this, uninstalling leaves several
                // gigabytes behind — one whole Roblox install per profile the user had.
                () => Directory.Delete(Paths.ParkedVersions, true),
                () => Directory.Delete(Paths.Downloads, true),

                () => File.Delete(App.State.FileLocation)
            };

            if (!keepData)
            {
                cleanupSequence.AddRange(new List<Action>
                {
                    () => Directory.Delete(Paths.Modifications, true),
                    () => Directory.Delete(Paths.Logs, true),

                    () => File.Delete(App.Settings.FileLocation),

                    // Everything below used to survive a full uninstall. Accounts.json is the
                    // serious one: it holds DPAPI-protected .ROBLOSECURITY cookies for every
                    // account the user added (Utility/Accounts/SecureStore.cs). "Remove my data"
                    // has to mean it.
                    () => File.Delete(App.Accounts.FileLocation),
                    () => File.Delete(App.PlayerState.FileLocation),
                    () => File.Delete(App.StudioState.FileLocation),

                    () => Directory.Delete(Paths.FastFlagProfiles, true),
                    () => Directory.Delete(Paths.CustomThemes, true),
                    () => Directory.Delete(Paths.Integrations, true),
                    () => Directory.Delete(Paths.DebugOutput, true)
                });
            }

            // This used to be `Directory.GetFiles(Paths.Base).Length <= 3`, evaluated BEFORE the
            // cleanup loop ran. Any working install has at least BeastStrap.exe + Settings.json
            // + State.json + PlayerState.json in the root, so the heuristic was always false and
            // the install folder was never removed — taking the user's saved cookies with it.
            // The decision is simply whether they asked to keep their data...
            //
            // ...but it still needs a backstop. In portable mode Paths.Base is wherever the exe
            // happens to be sitting, which for a portable copy is very often the Desktop, a
            // Downloads folder or the root of a USB stick. Recursively deleting that would take
            // everything the user keeps there with it. Removing the old (broken) heuristic left
            // nothing at all guarding this call, so refuse when Base does not look like a folder
            // we own exclusively.
            bool deleteFolder = !keepData && IsSafeToDeleteInstallFolder(LOG_IDENT);

            if (deleteFolder)
                cleanupSequence.Add(() => Directory.Delete(Paths.Base, true));

            if (!playerStillInstalled && !studioStillInstalled && Directory.Exists(robloxFolder))
                cleanupSequence.Add(() => Directory.Delete(robloxFolder, true));

            cleanupSequence.Add(() => Registry.CurrentUser.DeleteSubKey(App.UninstallKey));

            foreach (var process in cleanupSequence)
            {
                try
                {
                    process();
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Encountered exception when running cleanup sequence (#{cleanupSequence.IndexOf(process)})");
                    App.Logger.WriteException(LOG_IDENT, ex);
                }
            }

            if (Directory.Exists(Paths.Base))
            {
                // this is definitely one of the workaround hacks of all time

                string deleteCommand;

                if (deleteFolder)
                    deleteCommand = $"del /Q \"{Paths.Base}\\*\" && rmdir \"{Paths.Base}\"";
                else
                    deleteCommand = $"del /Q \"{Paths.Application}\"";

                Process.Start(new ProcessStartInfo()
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c timeout 5 && {deleteCommand}",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }

            App.SendStat("installAction", "uninstall");
        }

        /// <summary>
        /// Whether <see cref="Paths.Base"/> is a directory we can safely delete wholesale.
        /// </summary>
        /// <remarks>
        /// A normal install lives in its own folder under %LocalAppData% and this returns true. A
        /// PORTABLE copy puts Paths.Base wherever the exe was run from, which is regularly a folder
        /// full of the user's own files — so we only remove it when nothing unrecognised is left
        /// after the per-item cleanup above has run. Erring towards leaving an empty-ish folder
        /// behind is the right trade: the alternative is deleting somebody's Desktop.
        /// </remarks>
        private static bool IsSafeToDeleteInstallFolder(string LOG_IDENT)
        {
            try
            {
                if (string.IsNullOrEmpty(Paths.Base) || !Directory.Exists(Paths.Base))
                    return false;

                string basePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Paths.Base));

                // A drive root ("E:\", "C:\") or a UNC share root. Never.
                string? root = Path.GetPathRoot(basePath);
                if (string.IsNullOrEmpty(root) || string.Equals(Path.TrimEndingDirectorySeparator(root), basePath, StringComparison.OrdinalIgnoreCase))
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Refusing to delete install folder: '{basePath}' is a drive root.");
                    return false;
                }

                // Any well-known shell folder. Somebody running the portable build straight out of
                // Downloads or off the Desktop must not lose it.
                foreach (Environment.SpecialFolder special in new[]
                {
                    Environment.SpecialFolder.UserProfile,
                    Environment.SpecialFolder.DesktopDirectory,
                    Environment.SpecialFolder.MyDocuments,
                    Environment.SpecialFolder.MyMusic,
                    Environment.SpecialFolder.MyPictures,
                    Environment.SpecialFolder.MyVideos,
                    Environment.SpecialFolder.LocalApplicationData,
                    Environment.SpecialFolder.ApplicationData,
                    Environment.SpecialFolder.ProgramFiles,
                    Environment.SpecialFolder.ProgramFilesX86,
                    Environment.SpecialFolder.Windows,
                    Environment.SpecialFolder.System
                })
                {
                    string path = Environment.GetFolderPath(special);

                    if (!string.IsNullOrEmpty(path)
                        && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), basePath, StringComparison.OrdinalIgnoreCase))
                    {
                        App.Logger.WriteLine(LOG_IDENT, $"Refusing to delete install folder: '{basePath}' is a well-known shell folder.");
                        return false;
                    }
                }

                // The Downloads folder has no SpecialFolder entry on .NET 6; match it by name under
                // the user profile rather than pulling in the KNOWNFOLDERID interop for one check.
                string downloads = Path.Combine(Paths.UserProfile, "Downloads");
                if (string.Equals(Path.TrimEndingDirectorySeparator(downloads), basePath, StringComparison.OrdinalIgnoreCase))
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Refusing to delete install folder: '{basePath}' is the Downloads folder.");
                    return false;
                }

                // Finally: whatever survived the cleanup sequence must all be ours. Anything else in
                // here means we are sharing the folder with the user's own files.
                var ours = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Settings.json", "State.json", "PlayerState.json", "StudioState.json",
                    "Accounts.json", "portable.txt",
                    Path.GetFileName(Paths.Application)
                };

                var ourDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Logs", "Downloads", "Versions", "ParkedVersions", "Integrations",
                    "Modifications", "CustomThemes", "DebugOutput", "FastFlagProfiles",
                    "FastFlagBackups"
                };

                foreach (string entry in Directory.EnumerateFileSystemEntries(basePath))
                {
                    string name = Path.GetFileName(entry);

                    if (Directory.Exists(entry) ? ourDirs.Contains(name) : ours.Contains(name))
                        continue;

                    // Our own transient leftovers: rotated logs, atomic-save temp files, backups.
                    if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                        continue;

                    App.Logger.WriteLine(LOG_IDENT, $"Refusing to delete install folder: '{basePath}' still contains unrelated item '{name}'.");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }

            // Can't prove it's safe, so it isn't.
            App.Logger.WriteLine(LOG_IDENT, "Refusing to delete install folder: safety check failed.");
            return false;
        }

        // BeastStrap rebrand (formerly "MrExBloxstrap"): an existing install keeps its data folder,
        // registry uninstall key, Run-key value and shortcuts under the OLD name. On the first launch
        // of the rebranded build (typically via auto-update) bridge that install to the new identifier
        // so the user is recognised as installed and keeps their settings, instead of being treated as
        // a fresh install. Idempotent + best-effort. The physical data folder is intentionally NOT
        // moved (invisible to users; moving it during a live self-update would risk the Versions\
        // junctions and a multi-GB copy) — everything user-visible ends up correctly rebranded.
        //
        // The "MrExBloxstrap" literals below are the OLD brand and MUST stay literal: they are how we
        // detect a pre-rebrand install.
        public static void MigrateBranding()
        {
            const string LOG_IDENT = "Installer::MigrateBranding";
            const string OldName = "MrExBloxstrap";

            if (App.IsPortableMode)
                return;

            string oldUninstallKey = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{OldName}";
            string oldLocation = "";

            try
            {
                // Already migrated? (marker lives on the new key)
                using (var existing = Registry.CurrentUser.OpenSubKey(App.UninstallKey))
                {
                    if (existing?.GetValue("MigratedFrom") is not null)
                        return;
                }

                using var oldKey = Registry.CurrentUser.OpenSubKey(oldUninstallKey);
                if (oldKey is null || oldKey.GetValue("InstallLocation") is not string loc)
                    return; // no old install -> genuine fresh install, let the normal flow run

                oldLocation = loc;

                // Tolerate a renamed user-profile folder (mirrors the install-detection logic below).
                if (!Directory.Exists(oldLocation))
                {
                    var match = Regex.Match(oldLocation, @"^[a-zA-Z]:\\Users\\([^\\]+)", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        string candidate = oldLocation.Replace(match.Value, Paths.UserProfile, StringComparison.InvariantCultureIgnoreCase);
                        if (Directory.Exists(candidate))
                            oldLocation = candidate;
                    }
                }

                if (!Directory.Exists(oldLocation) || !File.Exists(Path.Combine(oldLocation, "Settings.json")))
                    return; // nothing meaningful to carry over

                App.Logger.WriteLine(LOG_IDENT, $"Migrating {OldName} install at '{oldLocation}' to {App.ProjectName} (in place).");

                string newExe = Path.Combine(oldLocation, $"{App.ProjectName}.exe");

                // Write the new uninstall key. This is BOTH the bridge (so install-detection finds us)
                // and the idempotency commit point (the MigratedFrom marker).
                using (var newKey = Registry.CurrentUser.CreateSubKey(App.UninstallKey))
                {
                    newKey.SetValueSafe("DisplayIcon", $"{newExe},0");
                    newKey.SetValueSafe("DisplayName", App.ProjectName);
                    newKey.SetValueSafe("DisplayVersion", (oldKey.GetValue("DisplayVersion") as string) ?? App.Version);
                    newKey.SetValueSafe("InstallDate", (oldKey.GetValue("InstallDate") as string) ?? DateTime.Now.ToString("yyyyMMdd"));
                    newKey.SetValueSafe("InstallLocation", oldLocation);
                    newKey.SetValueSafe("NoRepair", 1);
                    newKey.SetValueSafe("Publisher", App.ProjectOwner);
                    newKey.SetValueSafe("ModifyPath", $"\"{newExe}\" -settings");
                    newKey.SetValueSafe("QuietUninstallString", $"\"{newExe}\" -uninstall -quiet");
                    newKey.SetValueSafe("UninstallString", $"\"{newExe}\" -uninstall");
                    newKey.SetValueSafe("HelpLink", App.ProjectHelpLink);
                    newKey.SetValueSafe("URLInfoAbout", App.ProjectSupportLink);
                    newKey.SetValueSafe("URLUpdateInfo", App.ProjectDownloadLink);
                    newKey.SetValueSafe("MigratedFrom", OldName);
                }
            }
            catch (Exception ex)
            {
                // Bridge write failed -> leave the OLD key intact so the old identity still works;
                // retried next launch (marker absent).
                App.Logger.WriteException(LOG_IDENT, ex);
                return;
            }

            string migratedExe = Path.Combine(oldLocation, $"{App.ProjectName}.exe");

            // Migrate the startup (Run) entry, preserving the tray-launcher opt-in. Non-fatal.
            try
            {
                using var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                if (runKey?.GetValue(OldName) is string oldRun)
                {
                    string newRun = oldRun.Replace($"{OldName}.exe", $"{App.ProjectName}.exe", StringComparison.OrdinalIgnoreCase);
                    runKey.SetValue(App.ProjectName, newRun, RegistryValueKind.String);
                    runKey.DeleteValue(OldName, throwOnMissingValue: false);
                }
            }
            catch (Exception ex) { App.Logger.WriteException(LOG_IDENT + "::Run", ex); }

            // Recreate Desktop + Start-Menu shortcuts under the new name; remove the old ones. Non-fatal.
            try
            {
                string oldDesktop = Path.Combine(Paths.Desktop, $"{OldName}.lnk");
                string oldStartMenu = Path.Combine(Paths.WindowsStartMenu, $"{OldName}.lnk");

                Shortcut.Create(migratedExe, "", Path.Combine(Paths.Desktop, $"{App.ProjectName}.lnk"));
                Shortcut.Create(migratedExe, "", Path.Combine(Paths.WindowsStartMenu, $"{App.ProjectName}.lnk"));

                if (File.Exists(oldDesktop)) File.Delete(oldDesktop);
                if (File.Exists(oldStartMenu)) File.Delete(oldStartMenu);
            }
            catch (Exception ex) { App.Logger.WriteException(LOG_IDENT + "::Shortcuts", ex); }

            // Remove the OLD uninstall key last, after the new key + marker are committed.
            try { Registry.CurrentUser.DeleteSubKeyTree(oldUninstallKey, false); }
            catch (Exception ex) { App.Logger.WriteException(LOG_IDENT + "::OldKey", ex); }

            App.Logger.WriteLine(LOG_IDENT, "Brand migration complete (in place).");
        }

        public static void HandleUpgrade()
        {
            const string LOG_IDENT = "Installer::HandleUpgrade";

            // Normalised the same way as the copy guard further up this file. A raw == meant a
            // shortcut carrying a differently-cased or non-canonical path fell through to the MD5
            // comparison below, which reads and hashes both copies of a 169 MB executable on the
            // synchronous startup path — on every single launch, silently.
            if (!File.Exists(Paths.Application)
                || string.Equals(Path.GetFullPath(Paths.Process), Path.GetFullPath(Paths.Application), StringComparison.OrdinalIgnoreCase))
                return;

            // 2.0.0 downloads updates to <BaseFolder>/Updates so lol
            bool isAutoUpgrade = App.LaunchSettings.UpgradeFlag.Active
                || Paths.Process.StartsWith(Path.Combine(Paths.Base, "Updates"))
                || Paths.Process.StartsWith(Path.Combine(Paths.LocalAppData, "Temp"))
                || Paths.Process.StartsWith(Paths.TempUpdates);

            var existingVer = FileVersionInfo.GetVersionInfo(Paths.Application).ProductVersion;
            var currentVer = FileVersionInfo.GetVersionInfo(Paths.Process).ProductVersion;

            if (MD5Hash.FromFile(Paths.Process) == MD5Hash.FromFile(Paths.Application))
                return;

            if (currentVer is not null && existingVer is not null && Utilities.CompareVersions(currentVer, existingVer) == VersionComparison.LessThan)
            {
                var result = Frontend.ShowMessageBox(
                    Strings.InstallChecker_VersionLessThanInstalled,
                    MessageBoxImage.Question,
                    MessageBoxButton.YesNo
                );

                if (result != MessageBoxResult.Yes)
                    return;
            }

            // silently upgrade version if the command line flag is set or if we're launching from an auto update
            if (!isAutoUpgrade)
            {
                var result = Frontend.ShowMessageBox(
                    Strings.InstallChecker_VersionDifferentThanInstalled,
                    MessageBoxImage.Question,
                    MessageBoxButton.YesNo
                );

                if (result != MessageBoxResult.Yes)
                    return;
            }

            App.Logger.WriteLine(LOG_IDENT, "Doing upgrade");

            Filesystem.AssertReadOnly(Paths.Application);

            using (var ipl = new InterProcessLock("AutoUpdater", TimeSpan.FromSeconds(5)))
            {
                if (!ipl.IsAcquired)
                {
                    App.Logger.WriteLine(LOG_IDENT, "Failed to update! (Could not obtain singleton mutex)");
                    return;
                }
            }

            // prior to 2.8.0, auto-updating was handled with this... bruteforce method
            // now it's handled with the system mutex you see above, but we need to keep this logic for <2.8.0 versions
            for (int i = 1; i <= 10; i++)
            {
                try
                {
                    DeployExecutable();
                    break;
                }
                catch (Exception ex)
                {
                    if (i == 1)
                    {
                        App.Logger.WriteLine(LOG_IDENT, "Waiting for write permissions to update version");
                    }
                    else if (i == 10)
                    {
                        App.Logger.WriteLine(LOG_IDENT, "Failed to update! (Could not get write permissions after 10 tries/5 seconds)");
                        App.Logger.WriteException(LOG_IDENT, ex);
                        return;
                    }

                    Thread.Sleep(500);
                }
            }

            using (var uninstallKey = Registry.CurrentUser.CreateSubKey(App.UninstallKey))
            {
                uninstallKey.SetValueSafe("DisplayVersion", App.Version);

                uninstallKey.SetValueSafe("Publisher", App.ProjectOwner);
                uninstallKey.SetValueSafe("HelpLink", App.ProjectHelpLink);
                uninstallKey.SetValueSafe("URLInfoAbout", App.ProjectSupportLink);
                uninstallKey.SetValueSafe("URLUpdateInfo", App.ProjectDownloadLink);
            }

            // update migrations

            if (existingVer is not null)
            {
                if (Utilities.CompareVersions(existingVer, "2.2.0") == VersionComparison.LessThan)
                {
                    string path = Path.Combine(Paths.Integrations, "rbxfpsunlocker");

                    try
                    {
                        if (Directory.Exists(path))
                            Directory.Delete(path, true);
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteException(LOG_IDENT, ex);
                    }
                }

                if (Utilities.CompareVersions(existingVer, "2.3.0") == VersionComparison.LessThan)
                {
                    string injectorLocation = Path.Combine(Paths.Modifications, "dxgi.dll");
                    string configLocation = Path.Combine(Paths.Modifications, "ReShade.ini");

                    if (File.Exists(injectorLocation))
                        File.Delete(injectorLocation);

                    if (File.Exists(configLocation))
                        File.Delete(configLocation);
                }

                if (Utilities.CompareVersions(existingVer, "2.6.0") == VersionComparison.LessThan)
                {
                    if (App.Settings.Prop.UseDisableAppPatch)
                    {
                        try
                        {
                            File.Delete(Path.Combine(Paths.Modifications, "ExtraContent\\places\\Mobile.rbxl"));
                        }
                        catch (Exception ex)
                        {
                            App.Logger.WriteException(LOG_IDENT, ex);
                        }

                        App.Settings.Prop.EnableActivityTracking = true;
                    }

                    if (App.Settings.Prop.BootstrapperStyle == BootstrapperStyle.ClassicFluentDialog)
                        App.Settings.Prop.BootstrapperStyle = BootstrapperStyle.FluentDialog;
                }

                if (Utilities.CompareVersions(existingVer, "2.8.0") == VersionComparison.LessThan)
                {
                    if (isAutoUpgrade)
                    {
                        if (App.LaunchSettings.Args.Length == 0)
                            App.LaunchSettings.RobloxLaunchMode = LaunchMode.Player;

                        string? query = App.LaunchSettings.Args.FirstOrDefault(x => x.Contains("roblox"));

                        if (query is not null)
                        {
                            App.LaunchSettings.RobloxLaunchMode = LaunchMode.Player;
                            App.LaunchSettings.RobloxLaunchArgs = query;
                        }
                    }

                    string oldDesktopPath = Path.Combine(Paths.Desktop, "Play Roblox.lnk");
                    string oldStartPath = Path.Combine(Paths.WindowsStartMenu, "BeastStrap");

                    if (File.Exists(oldDesktopPath))
                        File.Move(oldDesktopPath, DesktopShortcut, true);

                    if (Directory.Exists(oldStartPath))
                    {
                        try
                        {
                            Directory.Delete(oldStartPath, true);
                        }
                        catch (Exception ex)
                        {
                            App.Logger.WriteException(LOG_IDENT, ex);
                        }

                        Shortcut.Create(Paths.Application, "", StartMenuShortcut);
                    }

                    Registry.CurrentUser.DeleteSubKeyTree("Software\\Bloxstrap", false);

                    WindowsRegistry.RegisterPlayer();
                }

                if (Utilities.CompareVersions(existingVer, "2.8.2") == VersionComparison.LessThan)
                {
                    string robloxDirectory = Path.Combine(Paths.Base, "Roblox");

                    if (Directory.Exists(robloxDirectory))
                    {
                        try
                        {
                            Directory.Delete(robloxDirectory, true);
                        }
                        catch (Exception ex)
                        {
                            App.Logger.WriteLine(LOG_IDENT, "Failed to delete the Roblox directory");
                            App.Logger.WriteException(LOG_IDENT, ex);
                        }
                    }
                }

                if (Utilities.CompareVersions(existingVer, "2.11.0") == VersionComparison.LessThan)
                {
                    JsonManager<RobloxState> legacyRobloxState = new();

                    if (legacyRobloxState.IsSaved)
                    {
                        if (legacyRobloxState.Load(false))
                        {
                            App.PlayerState.Prop.VersionGuid = legacyRobloxState.Prop.Player.VersionGuid;
                            App.PlayerState.Prop.PackageHashes = legacyRobloxState.Prop.Player.PackageHashes;
                            App.PlayerState.Prop.Size = legacyRobloxState.Prop.Player.Size;
                            App.PlayerState.Prop.ModManifest = legacyRobloxState.Prop.ModManifest;

                            App.StudioState.Prop.VersionGuid = legacyRobloxState.Prop.Studio.VersionGuid;
                            App.StudioState.Prop.PackageHashes = legacyRobloxState.Prop.Studio.PackageHashes;
                            App.StudioState.Prop.Size = legacyRobloxState.Prop.Studio.Size;
                        }

                        legacyRobloxState.Delete();
                    }
                }

                if (Utilities.CompareVersions(existingVer, "2.11.3") == VersionComparison.LessThan)
                {
                    App.FastFlags.SetValue("FFlagDebugGraphicsPreferD3D11", null);
                    App.FastFlags.SetValue("FFlagDebugGraphicsPreferD3D11FL10", null);
                }

                App.Settings.Save();
                App.FastFlags.Save();
                App.State.Save();

                if (App.PlayerState.Loaded)
                    App.PlayerState.Save();

                if (App.StudioState.Loaded)
                    App.StudioState.Save();
            }

            if (currentVer is null)
                return;

            App.SendStat("installAction", "upgrade");

            if (isAutoUpgrade)
            {
#pragma warning disable CS0162 // Unreachable code detected
                if (OpenReleaseNotes)
                {
                    string releaseNoteVersion;
                    if (!string.IsNullOrEmpty(ForcedReleaseNotesVersion) && Version.TryParse(ForcedReleaseNotesVersion, out _))
                    {
                        if (!string.IsNullOrEmpty(existingVer))
                        {
                            // dont show release notes if existingVer is greater than or equal to ForcedReleaseNotesVersion
                            VersionComparison compareResult = Utilities.CompareVersions(existingVer, ForcedReleaseNotesVersion);
                            if (compareResult == VersionComparison.Equal || compareResult == VersionComparison.GreaterThan)
                                return;
                        }

                        releaseNoteVersion = ForcedReleaseNotesVersion;
                    }
                    else
                    {
                        releaseNoteVersion = currentVer;
                    }

                    Utilities.ShellExecute($"{App.ProjectHost}/{App.ProjectRepository}/releases");
                }
#pragma warning restore CS0162 // Unreachable code detected
            }
            else
            {
                Frontend.ShowMessageBox(
                    string.Format(Strings.InstallChecker_Updated, App.Version),
                    MessageBoxImage.Information,
                    MessageBoxButton.OK
                );
            }
        }
    }
}
