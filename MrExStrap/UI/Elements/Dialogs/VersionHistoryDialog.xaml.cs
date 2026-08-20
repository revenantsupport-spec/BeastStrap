using System.Diagnostics;
using System.Windows;

namespace BeastStrap.UI.Elements.Dialogs
{
    // Version history / changelog: the app's own published GitHub releases, newest
    // first, with whatever notes the updater script wrote for each build. Lives in
    // the same compact-list style as the executor checker.
    public partial class VersionHistoryDialog
    {
        private bool _isLoading;

        public VersionHistoryDialog()
        {
            InitializeComponent();
            _ = LoadAsync();
        }

        private async Task LoadAsync()
        {
            if (_isLoading) return;
            _isLoading = true;
            RefreshButton.IsEnabled = false;
            ShowEmpty("Loading release history…");

            try
            {
                var releases = await Http.GetJson<List<GithubRelease>>(
                    $"{App.ProjectApiBase}/repos/{App.ProjectRepository}/releases?per_page=60");

                var rows = new List<VersionHistoryRow>();
                if (releases is not null)
                {
                    foreach (var release in releases)
                        rows.Add(new VersionHistoryRow(release));
                }

                ReleaseList.ItemsSource = rows;
                ReleaseList.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                EmptyOverlay.Visibility = rows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
                EmptyText.Text = rows.Count > 0 ? "" : "No releases were returned.";

                StatusText.Text = rows.Count > 0
                    ? $"{rows.Count} release(s) · github.com/{App.ProjectRepository}"
                    : "";
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("VersionHistoryDialog::Load", ex);
                ShowEmpty($"Couldn't load the release history.\n\n{ex.Message}\n\nClick Refresh to try again.");
            }
            finally
            {
                _isLoading = false;
                RefreshButton.IsEnabled = true;
            }
        }

        private void ShowEmpty(string text)
        {
            EmptyText.Text = text;
            EmptyOverlay.Visibility = Visibility.Visible;
            ReleaseList.Visibility = Visibility.Collapsed;
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

        private void OpenButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not VersionHistoryRow row) return;
            if (string.IsNullOrWhiteSpace(row.HtmlUrl)) return;

            try
            {
                Process.Start(new ProcessStartInfo { FileName = row.HtmlUrl, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("VersionHistoryDialog::Open", ex);
                Frontend.ShowMessageBox($"Couldn't open the release page ({ex.Message}).", MessageBoxImage.Error);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }

    // One release in the history: version tag, published date and its notes.
    public class VersionHistoryRow
    {
        public string Version { get; }
        public string PublishedText { get; }
        public string BodyText { get; }
        public string HtmlUrl { get; }

        public VersionHistoryRow(GithubRelease release)
        {
            Version = string.IsNullOrWhiteSpace(release.TagName) ? "unknown" : release.TagName;
            PublishedText = "published " + FormatRelative(ParseDateUtc(release.PublishedAt));

            string body = (release.Body ?? "").Trim();
            BodyText = body.Length > 0 ? body : "No release notes.";
            HtmlUrl = release.HtmlUrl ?? "";
        }

        private static DateTime ParseDateUtc(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return DateTime.MinValue;
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return parsed.ToUniversalTime();
            return DateTime.MinValue;
        }

        private static string FormatRelative(DateTime utc)
        {
            if (utc == DateTime.MinValue) return "unknown";
            var ago = DateTime.UtcNow - utc;
            if (ago < TimeSpan.Zero) ago = TimeSpan.Zero;
            if (ago.TotalDays >= 30) return $"{(int)(ago.TotalDays / 30)} mo ago";
            if (ago.TotalDays >= 1) return $"{(int)ago.TotalDays} d ago";
            if (ago.TotalHours >= 1) return $"{(int)ago.TotalHours} h ago";
            if (ago.TotalMinutes >= 1) return $"{(int)ago.TotalMinutes} min ago";
            return "just now";
        }
    }
}