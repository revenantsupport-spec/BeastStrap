using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

using BeastStrap.Models;
using BeastStrap.UI;

namespace BeastStrap.UI.ViewModels.Settings
{
    public class BeastStrapViewModel : NotifyPropertyChangedViewModel
    {
        public WebEnvironment[] WebEnvironments => Enum.GetValues<WebEnvironment>();

        public bool UpdateCheckingEnabled
        {
            get => App.Settings.Prop.CheckForUpdates;
            set => App.Settings.Prop.CheckForUpdates = value;
        }

        public bool LiveChannelToastEnabled
        {
            get => App.Settings.Prop.ShowLiveChannelToast;
            set => App.Settings.Prop.ShowLiveChannelToast = value;
        }

        public bool PrivacyModeEnabled
        {
            get => App.Settings.Prop.EnablePrivacyMode;
            set => App.Settings.Prop.EnablePrivacyMode = value;
        }

        // v420.30.3: Froststrap-style memory saver — close RobloxCrashHandler.exe while Roblox runs.
        public bool CloseRobloxCrashHandlerEnabled
        {
            get => App.Settings.Prop.CloseRobloxCrashHandler;
            set => App.Settings.Prop.CloseRobloxCrashHandler = value;
        }

        // v420.30.5: disable Roblox's built-in screenshot + video capture by blocking the save
        // folders. State lives in the filesystem (read-only placeholder), so the getter reflects
        // reality rather than a stored flag.
        public bool DisableRobloxCapturesEnabled
        {
            // Fully qualified: the BeastStrap.UI.Utility namespace shadows the top-level Utility here.
            get => BeastStrap.Utility.RobloxCaptureBlocker.IsBlocked;
            set
            {
                // SetBlocked is all-or-nothing and reports its own failure. Surface it — the toggle
                // reads back from the filesystem, so a silent partial failure just made the switch
                // snap back with no explanation and no way to undo the half that applied.
                string? error = BeastStrap.Utility.RobloxCaptureBlocker.SetBlocked(value);

                if (error is not null)
                    Frontend.ShowMessageBox(error, System.Windows.MessageBoxImage.Error);

                OnPropertyChanged(nameof(DisableRobloxCapturesEnabled));
            }
        }

        // v420.28: Stream Mode
        public bool StreamModeEnabled
        {
            get => App.Settings.Prop.EnableStreamMode;
            set { App.Settings.Prop.EnableStreamMode = value; OnPropertyChanged(nameof(StreamModeEnabled)); }
        }

        // v420.28: persistent system tray launcher. Toggle drives the HKCU Run
        // key + optionally spawns a -tray instance immediately so the user
        // doesn't have to reboot before seeing it work.
        public bool TrayLauncherEnabled
        {
            get => App.Settings.Prop.EnableTrayLauncher;
            set
            {
                App.Settings.Prop.EnableTrayLauncher = value;
                OnPropertyChanged(nameof(TrayLauncherEnabled));

                try
                {
                    if (value)
                    {
                        StartupRegistration.Enable();

                        var prompt = Frontend.ShowMessageBox(
                            "BeastStrap will now start in the system tray every time Windows starts.\n\n" +
                            "Start the tray launcher now without rebooting?",
                            MessageBoxImage.Information,
                            MessageBoxButton.YesNo,
                            MessageBoxResult.Yes);
                        if (prompt == MessageBoxResult.Yes)
                        {
                            try { Process.Start(Paths.Process, "-tray"); }
                            catch (Exception ex) { App.Logger.WriteException("BeastStrapViewModel::TrayLauncher::StartNow", ex); }
                        }
                    }
                    else
                    {
                        StartupRegistration.Disable();
                    }
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException("BeastStrapViewModel::TrayLauncher::Toggle", ex);
                }
            }
        }

        // v420.28: opt-in toasts for LIVE-channel changes + executor updates.
        public bool NotifyOnLiveChange
        {
            get => App.Settings.Prop.NotifyOnLiveChange;
            set { App.Settings.Prop.NotifyOnLiveChange = value; OnPropertyChanged(nameof(NotifyOnLiveChange)); }
        }

        public bool NotifyOnExecutorUpdate
        {
            get => App.Settings.Prop.NotifyOnExecutorUpdate;
            set { App.Settings.Prop.NotifyOnExecutorUpdate = value; OnPropertyChanged(nameof(NotifyOnExecutorUpdate)); }
        }

        // v420.29.5: toast when a newer BeastStrap release is available. Default ON.
        public bool NotifyOnAppUpdate
        {
            get => App.Settings.Prop.NotifyOnAppUpdate;
            set { App.Settings.Prop.NotifyOnAppUpdate = value; OnPropertyChanged(nameof(NotifyOnAppUpdate)); }
        }

        public bool WindowTilingEnabled
        {
            get => App.Settings.Prop.WindowTilingEnabled;
            set => App.Settings.Prop.WindowTilingEnabled = value;
        }

        public WindowTilingLayout[] WindowTilingLayouts => Enum.GetValues<WindowTilingLayout>();

        public WindowTilingLayout WindowTilingLayout
        {
            get => App.Settings.Prop.WindowTilingLayout;
            set => App.Settings.Prop.WindowTilingLayout = value;
        }

        public bool DebugModeEnabled
        {
            get => App.Settings.Prop.DebugModeEnabled;
            set
            {
                App.Settings.Prop.DebugModeEnabled = value;
                OnPropertyChanged(nameof(DebugModeEnabled));
                OnPropertyChanged(nameof(DebugModeVisibility));
            }
        }

        public Visibility DebugModeVisibility => App.Settings.Prop.DebugModeEnabled ? Visibility.Visible : Visibility.Collapsed;

        public ICommand RunHealthCheckCommand => new RelayCommand(RunHealthCheck);

        public ICommand OpenLogFolderCommand => new RelayCommand(OpenLogFolder);

        public ICommand OpenDebugFolderCommand => new RelayCommand(OpenDebugFolder);

        public ICommand SaveDiagnosticSnapshotCommand => new AsyncRelayCommand(SaveDiagnosticSnapshotAsync);

        private void RunHealthCheck()
        {
            var dialog = new UI.Elements.Dialogs.HealthCheckDialog();
            dialog.ShowDialog();
        }

        private void OpenLogFolder()
        {
            try
            {
                if (!string.IsNullOrEmpty(Paths.Logs) && Directory.Exists(Paths.Logs))
                    Process.Start(new ProcessStartInfo { FileName = Paths.Logs, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("BeastStrapViewModel::OpenLogFolder", ex);
            }
        }

        private void OpenDebugFolder()
        {
            try
            {
                Directory.CreateDirectory(Paths.DebugOutput);
                Process.Start(new ProcessStartInfo { FileName = Paths.DebugOutput, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("BeastStrapViewModel::OpenDebugFolder", ex);
                Frontend.ShowMessageBox(
                    $"Couldn't open the debug folder at {Paths.DebugOutput}.\n\nReason: {ex.GetType().Name}: {ex.Message}",
                    MessageBoxImage.Warning);
            }
        }

        private async Task SaveDiagnosticSnapshotAsync()
        {
            const string LOG_IDENT = "BeastStrapViewModel::SaveDiagnosticSnapshotAsync";
            try
            {
                string zipPath = await DiagnosticBundle.CreateAsync();
                App.Logger.WriteLine(LOG_IDENT, $"Snapshot ready at {zipPath}");

                var result = Frontend.ShowMessageBox(
                    $"Diagnostic snapshot saved to:\n\n{zipPath}\n\n" +
                    "It contains your settings, recent logs, environment info, network adapters, " +
                    "running Roblox processes, a health check, and a fresh GitHub probe — exactly what " +
                    "the maintainer needs to debug a problem.\n\nOpen the folder now?",
                    MessageBoxImage.Information,
                    MessageBoxButton.YesNo,
                    MessageBoxResult.Yes);

                if (result == MessageBoxResult.Yes)
                    Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{zipPath}\"" });
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                Frontend.ShowMessageBox(
                    $"Couldn't build the diagnostic snapshot.\n\nReason: {ex.GetType().Name}: {ex.Message}",
                    MessageBoxImage.Error);
            }
        }

        public WebEnvironment WebEnvironment
        {
            get => App.Settings.Prop.WebEnvironment;
            set => App.Settings.Prop.WebEnvironment = value;
        }

        public Visibility WebEnvironmentVisibility => App.Settings.Prop.DeveloperMode ? Visibility.Visible : Visibility.Collapsed;

        public ICommand ExportDataCommand => new AsyncRelayCommand(ExportDataAsync);

        public ICommand ClearRobloxCacheCommand => new AsyncRelayCommand(ClearRobloxCacheAsync);

        private async Task ClearRobloxCacheAsync()
        {
            const string LOG_IDENT = "BeastStrapViewModel::ClearRobloxCacheAsync";

            // Candidate cache locations. We touch the fork's own version/download cache and
            // the default-location Roblox install (when the user also runs vanilla Roblox on
            // the same machine). Settings, FastFlags, and custom themes are intentionally NOT
            // in this list — those are user config, not cache.
            var candidates = new List<string>();

            string? robloxPlayerDefault = Path.Combine(Paths.LocalAppData, "Roblox");
            string? robloxTemp = Path.Combine(Path.GetTempPath(), "Roblox");

            if (!string.IsNullOrEmpty(Paths.Versions) && Directory.Exists(Paths.Versions))
                candidates.Add(Paths.Versions);
            // Inactive profiles' installs live here, and they are the
            // bulk of the disk usage for anyone with more than one profile.
            if (!string.IsNullOrEmpty(Paths.ParkedVersions) && Directory.Exists(Paths.ParkedVersions))
                candidates.Add(Paths.ParkedVersions);
            if (!string.IsNullOrEmpty(Paths.Downloads) && Directory.Exists(Paths.Downloads))
                candidates.Add(Paths.Downloads);
            if (Directory.Exists(robloxPlayerDefault))
                candidates.Add(robloxPlayerDefault);
            if (Directory.Exists(robloxTemp))
                candidates.Add(robloxTemp);

            if (candidates.Count == 0)
            {
                Frontend.ShowMessageBox("No Roblox cache folders were found on this machine.",
                    MessageBoxImage.Information);
                return;
            }

            string bulletList = string.Join("\n", candidates.Select(p => "- " + p));
            string prompt =
                "The following folders will be permanently deleted:\n\n" + bulletList +
                "\n\nYour BeastStrap settings, FastFlags, and custom themes will be kept. " +
                "Roblox and the client cache will redownload on next launch.\n\n" +
                "Continue?";

            var result = Frontend.ShowMessageBox(prompt, MessageBoxImage.Warning,
                MessageBoxButton.YesNo, MessageBoxResult.No);
            if (result != MessageBoxResult.Yes)
                return;

            var failed = new List<string>();

            await Task.Run(() =>
            {
                foreach (var path in candidates)
                {
                    try
                    {
                        Directory.Delete(path, recursive: true);
                        App.Logger.WriteLine(LOG_IDENT, $"Deleted {path}");
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteException(LOG_IDENT, ex);
                        failed.Add(path);
                    }
                }
            });

            if (failed.Count == 0)
            {
                Frontend.ShowMessageBox($"Cleared {candidates.Count} folder(s) successfully.",
                    MessageBoxImage.Information);
            }
            else
            {
                string failedList = string.Join("\n", failed.Select(p => "- " + p));
                Frontend.ShowMessageBox(
                    $"Cleared {candidates.Count - failed.Count} of {candidates.Count} folder(s). " +
                    "The following were skipped (likely in use — close Roblox and try again):\n\n" +
                    failedList,
                    MessageBoxImage.Warning);
            }
        }

        private async Task ExportDataAsync()
        {
            const string LOG_IDENT = "BeastStrapViewModel::ExportDataAsync";

            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");

            var dialog = new SaveFileDialog
            {
                FileName = $"BeastStrap-diagnostics-{timestamp}.zip",
                Filter = $"{Strings.FileTypes_ZipArchive}|*.zip"
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                // Reuse the full DiagnosticBundle so the zip the user sends actually contains the
                // Roblox client logs (roblox-logs/) — that's where a Roblox-side crash is diagnosable,
                // unlike the old Config/Logs-only export. It also folds in this session's log,
                // environment info and a health check. quick:false adds the (time-bounded, never-throwing)
                // health + GitHub probes; it runs on await so the UI thread is never blocked.
                string bundlePath = await DiagnosticBundle.CreateAsync(quick: false);

                // The bundle is written to Paths.DebugOutput; copy it to wherever the user chose to save.
                File.Copy(bundlePath, dialog.FileName, overwrite: true);

                App.Logger.WriteLine(LOG_IDENT, $"Exported full diagnostic bundle to {dialog.FileName}");
                Process.Start("explorer.exe", $"/select,\"{dialog.FileName}\"");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                Frontend.ShowMessageBox(
                    $"Couldn't export diagnostic data.\n\nReason: {ex.GetType().Name}: {ex.Message}",
                    MessageBoxImage.Error);
            }
        }
    }
}
