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

        // Rolling window of (utc timestamp, bytes so far) samples for the speed line.
        // All samples are UtcNow pairs and only deltas matter, so clock timezone is a non-issue.
        private readonly List<(DateTime TimestampUtc, long Bytes)> _samples = new();

        // Speed samples older than this fall out of the window.
        private const double SpeedWindowSeconds = 5.0;

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

            VersionText.Text = $"{release.TagName} · updating";
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
            string speedTail = ComputeSpeedTail(p.Downloaded);

            if (p.Total > 0)
            {
                ProgressBar.IsIndeterminate = false;
                double pct = (double)p.Downloaded / p.Total * 100.0;
                ProgressBar.Value = pct;

                PercentText.Visibility = Visibility.Visible;
                PercentText.Text = $"{(int)pct}%";

                StatusText.Text = $"{FormatBytes(p.Downloaded)} / {FormatBytes(p.Total)}{speedTail}";
            }
            else
            {
                ProgressBar.IsIndeterminate = true;

                PercentText.Visibility = Visibility.Collapsed;

                StatusText.Text = $"{FormatBytes(p.Downloaded)} downloaded{speedTail}";
            }
        }

        // Record this report and estimate bytes/sec over the recent window. Only meaningful
        // with two+ samples inside the window and positive elapsed time AND positive byte
        // delta — otherwise return "" and the status line simply omits the speed tail. The
        // guards are the only thing standing between "first report microseconds after the
        // last" and a divide-by-zero on the UI thread.
        private string ComputeSpeedTail(long downloaded)
        {
            DateTime now = DateTime.UtcNow;
            _samples.Add((now, downloaded));
            _samples.RemoveAll(s => (now - s.TimestampUtc).TotalSeconds > SpeedWindowSeconds);

            (DateTime TimestampUtc, long Bytes) first = _samples[0];
            (DateTime TimestampUtc, long Bytes) last = _samples[^1];
            long bytesDelta = last.Bytes - first.Bytes;
            double seconds = (last.TimestampUtc - first.TimestampUtc).TotalSeconds;

            if (_samples.Count < 2 || bytesDelta <= 0 || seconds <= 0)
                return "";

            long bytesPerSecond = (long)Math.Round(bytesDelta / seconds);
            return $" · {FormatSpeed(bytesPerSecond)}";
        }

        private static string FormatSpeed(long bytesPerSecond) => FormatBytes(bytesPerSecond) + "/s";

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
