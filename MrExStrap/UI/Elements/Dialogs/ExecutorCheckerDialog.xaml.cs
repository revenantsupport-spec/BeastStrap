using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using BeastStrap.Models.APIs;
using BeastStrap.Models.Persistable;
using BeastStrap.UI.Utility;

namespace BeastStrap.UI.Elements.Dialogs
{
    // Executor version checker: a live board of the latest Windows executor builds from
    // WEAO (with the robloxscripts.com backup). Each row shows the executor's current
    // version, updated date and status badges (up to date / detected / free), links out
    // to the executor's website for the changelog, and — when the user has a Versions
    // Manager profile tracking that executor — whether that profile is on the latest
    // Roblox build WEAO lists.
    public partial class ExecutorCheckerDialog
    {
        private readonly List<ExecutorCheckerRow> _rows = new();
        private bool _isLoading;
        private string _sourceLabel = "weao.xyz";
        private CheckerSort _sort = CheckerSort.Recent;

        public ExecutorCheckerDialog()
        {
            InitializeComponent();
            _ = LoadAsync();
        }

        private async Task LoadAsync()
        {
            if (_isLoading) return;
            _isLoading = true;
            RefreshButton.IsEnabled = false;
            ShowEmpty("Loading executor list…");

            try
            {
                var result = await WeaoClient.GetWindowsExploitsAsync();
                var profiles = App.Settings.Prop.VersionProfiles;

                _rows.Clear();
                if (result.Success)
                {
                    _sourceLabel = result.Source == WeaoSource.Mirror ? "robloxscripts.com backup" : "weao.xyz";

                    foreach (var exploit in result.Exploits)
                        _rows.Add(new ExecutorCheckerRow(exploit, profiles));

                    ApplySort();

                    // Fire-and-forget the logo fetch for each row.
                    foreach (var row in _rows)
                        _ = row.LoadLogoAsync();

                    UpdateStats();

                    StatusText.Text = $"{_rows.Count} executor(s) · {_sourceLabel}";
                    ApplyFilter();

                    if (_rows.Count == 0)
                        ShowEmpty("No Windows executors were listed.");
                }
                else
                {
                    StatusText.Text = "";
                    ShowEmpty($"Couldn't load the executor list.\n\n{result.Error}\n\nClick Refresh to try again.");
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("ExecutorCheckerDialog::Load", ex);
                ShowEmpty($"Loading failed ({ex.GetType().Name}).\n\nClick Refresh to try again.");
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
            ExecutorList.Visibility = Visibility.Collapsed;
        }

        // Default ordering: updated executors first (green "Updated" badge), then most
        // recently updated within each group — the top of the list is "what's current".
        private static int UpdatedFirst(ExecutorCheckerRow a, ExecutorCheckerRow b)
        {
            if (a.IsUpdated != b.IsUpdated)
                return b.IsUpdated.CompareTo(a.IsUpdated);
            return b.UpdatedUtc.CompareTo(a.UpdatedUtc);
        }

        // Applies the selected sort (see the "Sort" combo) to _rows.
        private void ApplySort()
        {
            _rows.Sort(_sort switch
            {
                CheckerSort.Name => (a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase),
                CheckerSort.Unc => (a, b) => a.UncSort != b.UncSort
                    ? b.UncSort.CompareTo(a.UncSort)
                    : UpdatedFirst(a, b),
                CheckerSort.Cost => (a, b) => a.CostSort != b.CostSort
                    ? a.CostSort.CompareTo(b.CostSort)
                    : UpdatedFirst(a, b),
                _ => UpdatedFirst,
            });
        }

        private void UpdateStats()
        {
            int updated = _rows.Count(r => r.IsUpdated && !r.HasIssues);
            int notUpdated = _rows.Count(r => !r.IsUpdated && !r.HasIssues);
            int issues = _rows.Count(r => r.HasIssues);
            int tracked = _rows.Count(r => r.IsTracked);
            SummaryText.Text =
                $"{_rows.Count} Windows executors · {updated} up to date · {notUpdated} not updated · " +
                $"{issues} with issues · {tracked} tracked · {_sourceLabel}";
        }

        private void ApplyFilter()
        {
            string search = (SearchBox?.Text ?? "").Trim().ToLowerInvariant();

            IEnumerable<ExecutorCheckerRow> filtered = _rows;
            if (search.Length > 0)
                filtered = filtered.Where(r =>
                    r.Title.ToLowerInvariant().Contains(search) ||
                    r.Version.ToLowerInvariant().Contains(search));

            var items = filtered.ToList();
            items.Sort(_sort switch
            {
                CheckerSort.Name => (a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase),
                CheckerSort.Unc => (a, b) => a.UncSort != b.UncSort
                    ? b.UncSort.CompareTo(a.UncSort)
                    : UpdatedFirst(a, b),
                CheckerSort.Cost => (a, b) => a.CostSort != b.CostSort
                    ? a.CostSort.CompareTo(b.CostSort)
                    : UpdatedFirst(a, b),
                _ => UpdatedFirst,
            });

            ExecutorList.ItemsSource = items;
            ExecutorList.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            EmptyOverlay.Visibility = items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            if (items.Count == 0)
            {
                EmptyText.Text = search.Length > 0
                    ? "No executors match that search."
                    : "No Windows executors were listed.";
            }

            StatusText.Text = _rows.Count > 0
                ? $"{items.Count} of {_rows.Count} · {_sourceLabel}"
                : "";
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (ExecutorList == null) return;
            ApplyFilter();
        }

        private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ExecutorList == null) return;

            _sort = SortCombo.SelectedIndex switch
            {
                1 => CheckerSort.Name,
                2 => CheckerSort.Unc,
                3 => CheckerSort.Cost,
                _ => CheckerSort.Recent,
            };
            ApplySort();
            ApplyFilter();
        }

        private void ComboBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
            => ComboBoxScrollFix.HandleWheel(sender, e);

        private void RefreshButton_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

        // "Set active": pins the executor's currently supported Roblox version as the
        // active Versions Manager profile (creating the executor-tracked profile if the
        // user never added one). The next launch uses — and downloads if needed — that
        // build, so this is the one-click bridge from "the checker says compatible" to
        // "actually launch on it".
        private void PinButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ExecutorCheckerRow row) return;

            try
            {
                if (!VersionGuidValidator.IsWellFormed(row.Exploit.RbxVersion))
                {
                    Frontend.ShowMessageBox(
                        $"WEAO didn't report a usable supported version for '{row.Title}' right now.\n\n" +
                        "Try again once the executor updates.",
                        MessageBoxImage.Warning);
                    return;
                }

                var profiles = App.Settings.Prop.VersionProfiles;
                var profile = profiles.FirstOrDefault(p =>
                    !string.IsNullOrWhiteSpace(p.ExecutorRefreshKey) &&
                    string.Equals(p.ExecutorRefreshKey, row.Title, StringComparison.OrdinalIgnoreCase));

                if (profile is null)
                {
                    profile = new VersionProfile
                    {
                        Name = row.Title,
                        VersionGuid = row.Exploit.RbxVersion,
                        ExecutorTitle = row.Title,
                        ExecutorLogoUrl = row.Exploit.Slug?.Logo,
                        ExecutorRefreshKey = row.Title
                    };
                    profiles.Add(profile);
                }
                else
                {
                    // Keep the display fields current even if the user renamed things.
                    profile.Name = row.Title;
                    profile.ExecutorTitle = row.Title;
                    profile.ExecutorLogoUrl = row.Exploit.Slug?.Logo;
                    profile.ExecutorRefreshKey = row.Title;
                }

                profile.VersionGuid = row.Exploit.RbxVersion;
                profile.InstalledVersionGuid = row.Exploit.RbxVersion;
                App.Settings.Prop.ActiveVersionProfileId = profile.Id;
                App.Settings.Save();

                row.MarkActive();

                App.Logger.WriteLine("ExecutorCheckerDialog::Pin",
                    $"Pinned '{row.Title}' to {row.Exploit.RbxVersion} as active profile '{profile.Name}'.");

                Frontend.ShowMessageBox(
                    $"'{row.Title}' is now the active version profile.\n\n" +
                    $"It will launch on {row.Exploit.RbxVersion}. Roblox downloads that build first if it isn't installed yet.",
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("ExecutorCheckerDialog::Pin", ex);
                Frontend.ShowMessageBox($"Couldn't set the active profile ({ex.Message}).", MessageBoxImage.Error);
            }
        }

private void WebsiteButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ExecutorCheckerRow row) return;
            if (string.IsNullOrWhiteSpace(row.WebsiteLink)) return;

            try
            {
                Process.Start(new ProcessStartInfo { FileName = row.WebsiteLink, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("ExecutorCheckerDialog::Website", ex);
                Frontend.ShowMessageBox($"Couldn't open the website ({ex.Message}).", MessageBoxImage.Error);
            }
        }

        private void DiscordButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ExecutorCheckerRow row) return;
            if (string.IsNullOrWhiteSpace(row.DiscordLink)) return;

            try
            {
                Process.Start(new ProcessStartInfo { FileName = row.DiscordLink, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("ExecutorCheckerDialog::Discord", ex);
                Frontend.ShowMessageBox($"Couldn't open the Discord invite ({ex.Message}).", MessageBoxImage.Error);
            }
        }

        private void PurchaseButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ExecutorCheckerRow row) return;
            if (string.IsNullOrWhiteSpace(row.PurchaseLink)) return;

            try
            {
                Process.Start(new ProcessStartInfo { FileName = row.PurchaseLink, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("ExecutorCheckerDialog::Purchase", ex);
                Frontend.ShowMessageBox($"Couldn't open the purchase page ({ex.Message}).", MessageBoxImage.Error);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }

    // Sort modes for the checker board, driven by the toolbar combo.
    internal enum CheckerSort
    {
        Recent, // default: updated first, then most recently updated
        Name,
        Unc,
        Cost
    }

    // One row in the checker: the WEAO executor plus (when applicable) the Versions
    // Manager profile tracking it and whether that profile matches the latest build.
    public class ExecutorCheckerRow : INotifyPropertyChanged
    {
        // The raw WEAO entry this row was built from. Kept for the "Set active" pin
        // button, which writes the executor's current supported version onto the
        // active Versions Manager profile.
        public WeaoExploit Exploit { get; }
        public string Title { get; }
        public string Version { get; }
        public string LetterPlaceholder { get; }
        public string WebsiteLink { get; }
        public string? LogoUrl { get; }
        public string DiscordLink { get; }
        public string PurchaseLink { get; }
        public bool HasDiscord => !string.IsNullOrWhiteSpace(DiscordLink);
        public bool HasPurchase => !string.IsNullOrWhiteSpace(PurchaseLink);

        // "Updated" / "Not updated" / "Issues", mirroring whatexpsare.online. The green/red
        // read is driven by hasIssues/updateStatus, NOT the detected flag, so it agrees with
        // the site the user compares against.
        public string StatusText { get; }
        public string StatusColor { get; }
        public string StatusTooltip { get; }

        public string VersionLine { get; }
        public string RbxLine { get; }

        // True when WEAO says this executor is up to date with the current Roblox build
        // ("Updated" badge). Used so updated executors sort to the top of the board.
        public bool IsUpdated { get; }

        // WEAO "hasIssues" flag — drives the amber "Issues" status and the stats strip.
        public bool HasIssues { get; }

        // Cost display ("Free" when WEAO says free or omits a price) + a numeric sort key
        // so the "Cost (low → high)" sort works even with "$4.99"-style strings. Cost is
        // folded into the version line; the numeric key drives the sort.
        public string CostDisplay { get; }
        public double CostSort { get; }

        // Numeric UNC score for the "UNC (high → low)" sort.
        public int UncSort { get; }

        // Feature chips rendered under the version line.
        public IReadOnlyList<ExecutorFeature> Features { get; }

        // Detection. WEAO's "detected" flag is historical (Potassium: "Last banwave: June 1st";
        // Seliware: "We have observed no bans" yet detected=true) so it's never shown as a
        // badge. Only a subtle note when WEAO actually has a reason, plus a distinct warning
        // when WEAO thinks a banwave is imminent.
        public string DetectionNote { get; }
        public bool HasDetectionNote => !string.IsNullOrWhiteSpace(DetectionNote);
        public string BanwaveNote { get; }
        public bool HasBanwaveNote => !string.IsNullOrWhiteSpace(BanwaveNote);

        public bool IsTracked { get; }
        public string TrackedLine { get; }
        public string TrackedColor { get; }

        // "Set active" pin state. True when the executor's profile is the currently
        // active Versions Manager profile (the one the next launch uses). Mutable so
        // the pin button can flip it without rebuilding the list.
        public bool IsActive { get; private set; }
        public string PinButtonText => IsActive ? "Active" : "Set active";
        public bool PinButtonEnabled => !IsActive;
        public string PinButtonTooltip => IsActive
            ? "This executor's profile is already the active version."
            : "Pin this executor's supported Roblox version as the active profile. The next launch uses (and downloads if needed) that build.";

        // For sorting: most-recently-updated first by default.
        public DateTime UpdatedUtc { get; }

        private ImageSource? _logo;
        public ImageSource? Logo
        {
            get => _logo;
            private set { _logo = value; OnPropertyChanged(nameof(Logo)); OnPropertyChanged(nameof(HasLogo)); OnPropertyChanged(nameof(NoLogo)); }
        }
        public bool HasLogo => _logo != null;
        public bool NoLogo => _logo == null;

        public ExecutorCheckerRow(WeaoExploit exploit, IReadOnlyList<VersionProfile> profiles)
        {
            Exploit = exploit;
            Title = exploit.Title;
            Version = string.IsNullOrWhiteSpace(exploit.Version) ? "—" : exploit.Version;
            WebsiteLink = exploit.WebsiteLink ?? "";
            DiscordLink = exploit.DiscordLink ?? "";
            PurchaseLink = exploit.PurchaseLink ?? "";
            LogoUrl = exploit.Slug?.Logo;
            LetterPlaceholder = string.IsNullOrEmpty(exploit.Title) ? "?" : exploit.Title.Substring(0, 1).ToUpperInvariant();

            UpdatedUtc = ParseDateUtc(exploit.UpdatedDate);
            IsUpdated = exploit.UpdateStatus;
            HasIssues = exploit.HasIssues;
            VersionLine = $"v{Version} · updated {FormatRelative(UpdatedUtc)} · {CostDisplay}";
            RbxLine = $"Supports {exploit.RbxVersion}";

            // Cost: treat "Free", empty, or a missing price as free.
            string cost = (exploit.Cost ?? "").Trim();
            bool isFree = exploit.Free || string.IsNullOrWhiteSpace(cost)
                || cost.Equals("free", StringComparison.OrdinalIgnoreCase);
            CostDisplay = isFree ? "Free" : cost;
            CostSort = isFree ? 0 : TryParseCost(cost);
            UncSort = exploit.UncPercentage;

            StatusText = exploit.HasIssues ? "Issues" : exploit.UpdateStatus ? "Updated" : "Not Updated";
            StatusColor = exploit.HasIssues ? "#F59E0B" : exploit.UpdateStatus ? "#4CAF50" : "#EF4444";
            StatusTooltip = exploit.HasIssues
                ? "WEAO is tracking issues with this executor's update."
                : exploit.UpdateStatus
                    ? "Up to date with the current Roblox build."
                    : "Not updated for the current Roblox build yet.";

            // Build the feature chips from WEAO's rich flags.
            var features = new List<ExecutorFeature>();

            if (exploit.SuncPercentage > 0)
                features.Add(new ExecutorFeature("sUNC " + exploit.SuncPercentage + "%", "#8B5CF6",
                    $"Share of scripts this executor claims to support (sUNC score {exploit.SuncPercentage}%)."));
            if (exploit.UncPercentage > 0)
                features.Add(new ExecutorFeature("UNC " + exploit.UncPercentage + "%", "#7C3AED",
                    $"UNC score: {exploit.UncPercentage}%."));
            if (exploit.Decompiler)
                features.Add(new ExecutorFeature("Decompiler", "#4CAF50", "Has a built-in decompiler."));
            if (exploit.MultiInject)
                features.Add(new ExecutorFeature("Multi-instance", "#3B82F6", "Supports multiple Roblox instances."));
            if (exploit.Raknet)
                features.Add(new ExecutorFeature("Raknet", "#06B6D4", "Has Raknet library support."));
            if (exploit.ClientMods)
                features.Add(new ExecutorFeature("Client mods", "#14B8A6", "Bypasses client-modification bans."));
            if (exploit.KeySystem)
                features.Add(new ExecutorFeature("Key system", "#F59E0B", "Requires a key to use (free executor)."));
            if (exploit.Beta)
                features.Add(new ExecutorFeature("Beta", "#F59E0B", "Marked as a beta build."));

            Features = features;

            // Detection as an honest footnote, never a scare badge.
            string reason = (exploit.DetectionReason ?? "").Trim();
            if (exploit.Detected)
                DetectionNote = string.IsNullOrWhiteSpace(reason)
                    ? "WEAO has flagged this executor before — check its website/Discord for the current status."
                    : $"WEAO note: {reason}. Historical — check the executor's website/Discord for the current status.";
            else
                DetectionNote = "";
            BanwaveNote = exploit.PossibleBanwave
                ? "WEAO suspects a possible banwave is coming — use carefully."
                : "";

            var tracked = profiles.FirstOrDefault(p =>
                !string.IsNullOrWhiteSpace(p.ExecutorRefreshKey) &&
                string.Equals(p.ExecutorRefreshKey, exploit.Title, StringComparison.OrdinalIgnoreCase));

            IsTracked = tracked != null;
            IsActive = tracked != null &&
                string.Equals(tracked.Id, App.Settings.Prop.ActiveVersionProfileId, StringComparison.Ordinal);
            if (tracked == null)
            {
                TrackedLine = "";
                TrackedColor = "";
            }
            else
            {
                bool upToDate = string.Equals(tracked.VersionGuid, exploit.RbxVersion, StringComparison.OrdinalIgnoreCase);
                TrackedLine = upToDate
                    ? $"Tracked as '{tracked.Name}' — on the latest build"
                    : $"Tracked as '{tracked.Name}' — update available ({exploit.RbxVersion})";
                TrackedColor = upToDate ? "#3B82F6" : "#F59E0B";
            }
        }

        private static double TryParseCost(string cost)
        {
            // "4.99", "$4.99", "1,299" … → number. Anything unparseable sorts last.
            var match = Regex.Match(cost, @"\d+(?:[.,]\d+)?");
            if (!match.Success)
                return double.MaxValue;
            string num = match.Value.Replace(",", ".");
            return double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                ? v
                : double.MaxValue;
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

        public async Task LoadLogoAsync()
        {
            if (string.IsNullOrWhiteSpace(LogoUrl)) return;
            try
            {
                string? path = await ExecutorLogoCache.GetLogoAsync(LogoUrl);
                if (string.IsNullOrEmpty(path)) return;

                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher == null) return;

                dispatcher.Invoke(() =>
                {
                    try
                    {
                        var img = new BitmapImage();
                        img.BeginInit();
                        img.CacheOption = BitmapCacheOption.OnLoad;
                        img.UriSource = new Uri(path);
                        img.EndInit();
                        img.Freeze();
                        Logo = img;
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteException("ExecutorCheckerRow::Bitmap", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("ExecutorCheckerRow::LoadLogo", ex);
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // Flipped by the dialog after a successful pin so the button reads "Active"
        // without rebuilding the whole list.
        public void MarkActive()
        {
            IsActive = true;
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(PinButtonText));
            OnPropertyChanged(nameof(PinButtonEnabled));
            OnPropertyChanged(nameof(PinButtonTooltip));
        }
    }

    // One colored feature chip ("sUNC 100%", "Decompiler", …) on an executor row.
    public class ExecutorFeature
    {
        public string Text { get; }
        public string Color { get; }
        public string Tooltip { get; }
        public ExecutorFeature(string text, string color, string tooltip) { Text = text; Color = color; Tooltip = tooltip; }
    }
}