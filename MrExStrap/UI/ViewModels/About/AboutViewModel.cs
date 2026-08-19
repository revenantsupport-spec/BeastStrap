using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

using BeastStrap.UI;

namespace BeastStrap.UI.ViewModels.About
{
    public class AboutViewModel : NotifyPropertyChangedViewModel
    {
        public string Version => string.Format(Strings.Menu_About_Version, App.Version);

        public BuildMetadataAttribute BuildMetadata => App.BuildMetadata;

        public string BuildTimestamp => BuildMetadata.Timestamp.ToFriendlyString();
        public string BuildCommitHashUrl => $"{App.ProjectHost}/{App.ProjectRepository}/commit/{BuildMetadata.CommitHash}";

        public Visibility BuildInformationVisibility => App.IsProductionBuild ? Visibility.Collapsed : Visibility.Visible;
        public Visibility BuildCommitVisibility => App.IsActionBuild ? Visibility.Visible : Visibility.Collapsed;

        public bool IsPortableMode => App.IsPortableMode;
        public Visibility PortableModeVisibility => App.IsPortableMode ? Visibility.Visible : Visibility.Collapsed;

        public string PortableDescription => App.IsPortableFastCache
            ? "Settings, logs, mods, and themes live next to the exe (so they travel). The heavy Roblox binaries cache to local AppData on this machine for speed — they do not travel. No data is written to the registry."
            : "All settings, logs, and downloaded Roblox versions live next to the exe. No data is written to AppData or the registry. (Tip: add 'cache=local' to portable.txt to keep config portable but cache Roblox binaries locally for much faster installs.)";

        public ICommand CopyDiagnosticInfoCommand => new AsyncRelayCommand(CopyDiagnosticInfoAsync);

        private async Task CopyDiagnosticInfoAsync()
        {
            const string LOG_IDENT = "AboutViewModel::CopyDiagnosticInfo";
            try
            {
                string blob = await BeastStrap.Utility.Diagnostics.BuildAsync();
                Clipboard.SetDataObject(blob, true);
                Frontend.ShowMessageBox("Diagnostic info copied to clipboard. Paste it into your support message.",
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                Frontend.ShowMessageBox("Couldn't build or copy diagnostic info. Check the log file.",
                    MessageBoxImage.Warning);
            }
        }
    }
}
