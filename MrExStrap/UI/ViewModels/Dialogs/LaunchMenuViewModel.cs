using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

using BeastStrap.RobloxInterfaces;
using BeastStrap.UI.Elements.About;

namespace BeastStrap.UI.ViewModels.Installer
{
    public enum ChannelLockState
    {
        Locked,
        Overridden
    }

    public class LaunchMenuViewModel
    {
        public string Version => string.Format(Strings.Menu_About_Version, App.Version);

        public ICommand LaunchSettingsCommand => new RelayCommand(LaunchSettings);

        public ICommand LaunchRobloxCommand => new RelayCommand(LaunchRoblox);

        public ICommand LaunchRobloxStudioCommand => new RelayCommand(LaunchRobloxStudio);

        public ICommand LaunchAboutCommand => new RelayCommand(LaunchAbout);

        public ICommand ResetToStockCommand => new RelayCommand(ResetToStock);

        public ICommand CheckForUpdatesCommand => new AsyncRelayCommand(CheckForUpdatesAsync);

        public event EventHandler<NextAction>? CloseWindowRequest;

        private bool _isCheckingForUpdates;

        // Computed once at construction; reflects the Roblox-side channel registry at the
        // moment the launch menu opens. If Overridden, the bootstrapper will still force
        // LIVE when the user clicks Launch Roblox — the chip just reports current state.
        public ChannelLockState ChannelLockState { get; } = DetectChannelLockState();

        public string ChannelLockText => ChannelLockState switch
        {
            ChannelLockState.Locked => "CHANNEL: LIVE (locked)",
            ChannelLockState.Overridden => "CHANNEL: will be forced to LIVE on launch",
            _ => "CHANNEL: LIVE (locked)"
        };

        public Brush ChannelLockBackground => ChannelLockState switch
        {
            ChannelLockState.Locked => new SolidColorBrush(Color.FromRgb(0x1E, 0x6B, 0x2E)),      // green
            ChannelLockState.Overridden => new SolidColorBrush(Color.FromRgb(0x8A, 0x6B, 0x17)),  // amber
            _ => new SolidColorBrush(Color.FromRgb(0x1E, 0x6B, 0x2E))
        };

        private static ChannelLockState DetectChannelLockState()
        {
            const string LOG_IDENT = "LaunchMenuViewModel::DetectChannelLockState";
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                    $"SOFTWARE\\ROBLOX Corporation\\Environments\\RobloxPlayer\\Channel",
                    writable: false);
                string? value = key?.GetValue("www.roblox.com") as string;

                if (string.IsNullOrEmpty(value)
                    || string.Equals(value, Deployment.DefaultChannel, StringComparison.OrdinalIgnoreCase))
                {
                    return ChannelLockState.Locked;
                }

                return ChannelLockState.Overridden;
            }
            catch (Exception ex)
            {
                // Read errors are non-fatal. The bootstrapper will still force LIVE at launch;
                // the chip just optimistically shows Locked if we can't read the key.
                App.Logger.WriteException(LOG_IDENT, ex);
                return ChannelLockState.Locked;
            }
        }

        private void LaunchSettings() => CloseWindowRequest?.Invoke(this, NextAction.LaunchSettings);

        private void LaunchRoblox() => CloseWindowRequest?.Invoke(this, NextAction.LaunchRoblox);

        private void LaunchRobloxStudio() => CloseWindowRequest?.Invoke(this, NextAction.LaunchRobloxStudio);

        private void LaunchAbout() => new MainWindow().ShowDialog();

        // "Reset to non-BeastStrap setup": hand the roblox:// protocol back to stock Roblox
        // (or unregister it if there's no stock install) so the user can run normal Roblox
        // or an executor that doesn't support bootstrappers (e.g. Volt). Reversible — the
        // next Roblox launch through BeastStrap re-registers it automatically.
        private void ResetToStock()
        {
            const string LOG_IDENT = "LaunchMenuViewModel::ResetToStock";

            var confirm = Frontend.ShowMessageBox(
                "This hands Roblox's launch link back to normal Roblox, so Roblox stops launching through BeastStrap.\n\n" +
                "Use this when you want to run plain Roblox or an executor that doesn't support bootstrappers (like Volt).\n\n" +
                "Nothing is uninstalled and none of your profiles or settings are touched. To start using BeastStrap again, just launch Roblox through it once — it re-hooks itself automatically.\n\n" +
                "Continue?",
                MessageBoxImage.Question,
                MessageBoxButton.YesNo,
                MessageBoxResult.No);

            if (confirm != MessageBoxResult.Yes)
            {
                App.Logger.WriteLine(LOG_IDENT, "User cancelled reset-to-stock.");
                return;
            }

            string summary = BeastStrap.Utility.WindowsRegistry.ResetToStockRoblox();

            Frontend.ShowMessageBox(
                "Done — Roblox is back to its normal setup.\n\n" +
                (string.IsNullOrEmpty(summary) ? "" : summary + "\n\n") +
                "Launch Roblox through BeastStrap any time to switch back to it.",
                MessageBoxImage.Information);

            App.Logger.WriteLine(LOG_IDENT, "Reset-to-stock completed.");
        }

        // Manual "Check for updates" from the launch menu. Unlike the silent on-open auto-check
        // (LaunchHandler.TryMenuAutoUpgrade), this always reports a result — up to date, an
        // available update, or why the check failed — since the user explicitly asked. Reuses the
        // same release fetch, version compare, and UpdateProgressDialog download/relaunch contract.
        private async Task CheckForUpdatesAsync()
        {
            const string LOG_IDENT = "LaunchMenuViewModel::CheckForUpdates";

            if (_isCheckingForUpdates)
                return;
            _isCheckingForUpdates = true;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                GithubRelease? release = await App.GetLatestRelease();
                Mouse.OverrideCursor = null;

                if (release is null || string.IsNullOrEmpty(release.TagName))
                {
                    Frontend.ShowMessageBox(
                        "Couldn't check for updates right now. GitHub may be unreachable — check your connection and try again.",
                        MessageBoxImage.Warning);
                    return;
                }

                VersionComparison cmp;
                try
                {
                    cmp = Utilities.CompareVersions(App.Version, release.TagName);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException(LOG_IDENT + "::Compare", ex);
                    Frontend.ShowMessageBox(
                        $"You're on v{App.Version}; the latest is {release.TagName}. Couldn't compare them automatically — grab the latest from the releases page if you're behind.",
                        MessageBoxImage.Warning);
                    return;
                }

                if (cmp != VersionComparison.LessThan)
                {
                    Frontend.ShowMessageBox(
                        $"You're up to date — running the latest version (v{App.Version}).",
                        MessageBoxImage.Information);
                    return;
                }

                // A newer version exists. Portable builds can't self-update — point them at the page.
                if (App.IsPortableMode)
                {
                    var open = Frontend.ShowMessageBox(
                        $"An update is available.\n\nYou're on v{App.Version}. Latest is {release.TagName}.\n\n" +
                        "This is a portable build, so it can't update itself. Download the new portable build from the releases page. Open it now?",
                        MessageBoxImage.Information, MessageBoxButton.YesNo, MessageBoxResult.Yes);
                    if (open == MessageBoxResult.Yes)
                        Utilities.ShellExecute($"{App.ProjectHost}/{App.ProjectRepository}/releases/latest");
                    return;
                }

                var prompt = Frontend.ShowMessageBox(
                    $"An update is available.\n\nYou're on v{App.Version}. Latest is {release.TagName}.\n\n" +
                    "Install now? BeastStrap will download the update and reopen the menu on the new version.",
                    MessageBoxImage.Question, MessageBoxButton.YesNo, MessageBoxResult.Yes);

                if (prompt != MessageBoxResult.Yes)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"User declined update to {release.TagName}");
                    return;
                }

                var progressDialog = new UI.Elements.Dialogs.UpdateProgressDialog(release, new[] { "-menu" });
                progressDialog.ShowDialog();

                if (progressDialog.UpdateStarted)
                {
                    // New exe is running with -menu. This process MUST exit (App ShutdownMode is
                    // OnExplicitShutdown) so it doesn't linger as a ghost behind the new window.
                    App.Terminate();
                    return;
                }

                string reason = string.IsNullOrEmpty(progressDialog.FailureReason) ? "Unknown error." : progressDialog.FailureReason!;
                Frontend.ShowMessageBox(
                    $"Couldn't download {release.TagName}.\n\nReason: {reason}\n\n" +
                    "You can grab the installer manually from the GitHub releases page.",
                    MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                Frontend.ShowMessageBox(
                    $"Something went wrong checking for updates ({ex.GetType().Name}).",
                    MessageBoxImage.Warning);
            }
            finally
            {
                Mouse.OverrideCursor = null;
                _isCheckingForUpdates = false;
            }
        }
    }
}
