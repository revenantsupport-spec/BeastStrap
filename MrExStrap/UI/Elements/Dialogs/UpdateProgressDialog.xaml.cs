using System.ComponentModel;
using System.Windows;

using BeastStrap.Utility;

namespace BeastStrap.UI.Elements.Dialogs
{
    public partial class UpdateProgressDialog
    {
        private const string LOG_IDENT = "UpdateProgressDialog";

        private readonly GithubRelease _release;
        private readonly string[] _relaunchArgs;
        private bool _downloadFinished;

        // True when the download succeeded and the new exe was started. Caller should exit
        // the current process (App.Terminate) so the new exe takes over.
        public bool UpdateStarted { get; private set; }

        // Set when UpdateStarted is false. Human-readable reason the download failed, suitable
        // for showing directly to the user.
        public string? FailureReason { get; private set; }

        public UpdateProgressDialog(GithubRelease release, IEnumerable<string> relaunchArgs)
        {
            _release = release;
            _relaunchArgs = relaunchArgs?.ToArray() ?? Array.Empty<string>();

            InitializeComponent();

            HeaderText.Text = $"Downloading BeastStrap {release.TagName}…";
            Loaded += async (_, _) => await RunAsync();
        }

        private async Task RunAsync()
        {
            try
            {
                var progress = new Progress<AppUpdater.DownloadProgress>(ApplyProgress);
                var result = await AppUpdater.DownloadAndRelaunchAsync(_release, _relaunchArgs, progress);
                UpdateStarted = result.Started;
                FailureReason = result.Reason;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                UpdateStarted = false;
                FailureReason = $"{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                _downloadFinished = true;
                Close();
            }
        }

        private void ApplyProgress(AppUpdater.DownloadProgress p)
        {
            if (p.Total > 0)
            {
                ProgressBar.IsIndeterminate = false;
                double pct = (double)p.Downloaded / p.Total * 100.0;
                ProgressBar.Value = pct;
                StatusText.Text = $"{FormatBytes(p.Downloaded)} / {FormatBytes(p.Total)}  ({(int)pct}%)";
            }
            else
            {
                ProgressBar.IsIndeterminate = true;
                StatusText.Text = $"{FormatBytes(p.Downloaded)} downloaded";
            }
        }

        // Refuse to close until the download finishes — there's nothing useful for the user
        // to do here, and closing mid-stream would just leak the partial file in TempUpdates.
        private void WpfUiWindow_Closing(object sender, CancelEventArgs e)
        {
            if (!_downloadFinished)
                e.Cancel = true;
        }

        private static string FormatBytes(long b)
        {
            if (b < 1024) return $"{b} B";
            if (b < 1024L * 1024) return $"{b / 1024.0:0.0} KB";
            if (b < 1024L * 1024 * 1024) return $"{b / 1024.0 / 1024.0:0.0} MB";
            return $"{b / 1024.0 / 1024.0 / 1024.0:0.00} GB";
        }
    }
}
