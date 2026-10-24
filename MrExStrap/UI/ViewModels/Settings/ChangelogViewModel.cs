using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

using CommunityToolkit.Mvvm.Input;

using BeastStrap.Utility;

namespace BeastStrap.UI.ViewModels.Settings
{
    // Backs the Changelog page: BeastStrap's own release history, read off the forge and grouped
    // into colour-coded sections. The newest release starts expanded and the rest are collapsed, so
    // the page opens on "what changed in the version I just got" rather than a wall of every release
    // we've ever shipped.
    public class ChangelogViewModel : NotifyPropertyChangedViewModel
    {
        private const string LOG_IDENT = "ChangelogViewModel";

        public ChangelogViewModel()
        {
            ReleasesView = CollectionViewSource.GetDefaultView(_releases);
            ReleasesView.Filter = FilterRelease;

            _ = LoadAsync(false);
        }

        private readonly ObservableCollection<ChangelogReleaseRow> _releases = new();

        public ICollectionView ReleasesView { get; }

        private bool _loading;

        public bool Loading
        {
            get => _loading;
            set { _loading = value; OnPropertyChanged(nameof(Loading)); OnPropertyChanged(nameof(NotLoading)); }
        }

        public bool NotLoading => !_loading;

        private string _search = "";

        public string Search
        {
            get => _search;
            set
            {
                _search = value ?? "";
                OnPropertyChanged(nameof(Search));
                ReleasesView.Refresh();

                // A match is usually buried in a collapsed body, so searching would otherwise leave
                // rows on screen with no visible reason for surviving the filter. Expand everything
                // while a search is active and fall back to newest-only when it's cleared.
                foreach (var row in _releases)
                    row.IsExpanded = _search.Length > 0 || row.IsLatest;
            }
        }

        private string _footer = "";

        public string Footer
        {
            get => _footer;
            set { _footer = value; OnPropertyChanged(nameof(Footer)); }
        }

        public string CurrentVersionText => $"You're on v{App.Version}";

        public string ReleasesUrl => ChangelogClient.ReleasesUrl;

        public ICommand RefreshCommand => new AsyncRelayCommand(() => LoadAsync(true));

        private async Task LoadAsync(bool force)
        {
            Loading = true;
            Footer = "Loading…";

            try
            {
                var entries = await ChangelogClient.GetAsync(force);

                _releases.Clear();

                for (int i = 0; i < entries.Count; i++)
                    _releases.Add(new ChangelogReleaseRow(entries[i], isLatest: i == 0));

                ReleasesView.Refresh();

                if (entries.Count > 0)
                {
                    var age = DateTime.Now - ChangelogClient.FetchedAt;
                    string cached = age.TotalMinutes < 1 ? "just now" : $"cached {(int)age.TotalMinutes} min ago";
                    Footer = $"{entries.Count} releases · {cached}";
                }
                else
                {
                    Footer = "No releases published yet.";
                }
            }
            catch (Exception ex)
            {
                // Same split the News page makes: say what the user can do, keep the exception type
                // in the log where it belongs.
                App.Logger.WriteException(LOG_IDENT, ex);
                Footer = "Couldn't reach the release server. Check your connection and hit Refresh.";
            }
            finally
            {
                Loading = false;
            }
        }

        private bool FilterRelease(object o)
        {
            if (o is not ChangelogReleaseRow row)
                return false;

            if (_search.Length == 0)
                return true;

            return row.SearchBlob.Contains(_search, StringComparison.OrdinalIgnoreCase);
        }
    }

    public class ChangelogReleaseRow : NotifyPropertyChangedViewModel
    {
        public ChangelogReleaseRow(ChangelogEntry entry, bool isLatest)
        {
            Version = entry.Version;
            Url = entry.Url;
            IsLatest = isLatest;
            _isExpanded = isLatest;

            // Tags are written "v420.50" and App.Version is "420.50" — it's the same comparison the
            // updater makes, and getting it wrong here just means nobody sees the "you're on this
            // one" badge.
            IsCurrent = string.Equals(Version.TrimStart('v', 'V'), App.Version, StringComparison.OrdinalIgnoreCase);

            DateText = entry.Published == DateTime.MinValue ? "" : entry.Published.ToString("d MMM yyyy");
            AgeText = entry.Published == DateTime.MinValue ? "" : Age(DateTime.Now - entry.Published);

            PrereleaseVisibility = entry.IsPrerelease ? Visibility.Visible : Visibility.Collapsed;
            LatestVisibility = isLatest ? Visibility.Visible : Visibility.Collapsed;
            CurrentVisibility = IsCurrent ? Visibility.Visible : Visibility.Collapsed;

            foreach (var section in entry.Sections)
                Sections.Add(new ChangelogSectionRow(section));

            EmptyVisibility = Sections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            Summary = BuildSummary(entry);

            // Filtering matches on everything the row can show, so a search for a word buried in one
            // bullet still surfaces the release that fixed it.
            var blob = new StringBuilder();
            blob.Append(Version).Append(' ').Append(entry.Title);
            foreach (var section in entry.Sections)
            {
                blob.Append(' ').Append(section.Heading);
                foreach (var line in section.Lines)
                    blob.Append(' ').Append(line.Text);
            }
            SearchBlob = blob.ToString();
        }

        public string Version { get; }

        public string Url { get; }

        public string DateText { get; }

        public string AgeText { get; }

        public string Summary { get; }

        public string SearchBlob { get; }

        public bool IsLatest { get; }

        public bool IsCurrent { get; }

        public Visibility LatestVisibility { get; }

        public Visibility CurrentVisibility { get; }

        public Visibility PrereleaseVisibility { get; }

        public Visibility EmptyVisibility { get; }

        public List<ChangelogSectionRow> Sections { get; } = new();

        private bool _isExpanded;

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value)
                    return;

                _isExpanded = value;
                OnPropertyChanged(nameof(IsExpanded));
                OnPropertyChanged(nameof(BodyVisibility));
                OnPropertyChanged(nameof(ChevronSymbol));
            }
        }

        public Visibility BodyVisibility => _isExpanded ? Visibility.Visible : Visibility.Collapsed;

        public Wpf.Ui.Common.SymbolRegular ChevronSymbol =>
            _isExpanded ? Wpf.Ui.Common.SymbolRegular.ChevronDown24 : Wpf.Ui.Common.SymbolRegular.ChevronRight24;

        public ICommand ToggleCommand => new RelayCommand(() => IsExpanded = !IsExpanded);

        // A one-line "what's in here" for the collapsed rows. Counts bullets rather than sections,
        // since one "## Fixed" heading routinely carries five separate fixes, and only counts the
        // three categories that are actual changes — an antivirus heads-up isn't one.
        private static string BuildSummary(ChangelogEntry entry)
        {
            int news = 0, fixes = 0, changes = 0;

            foreach (var section in entry.Sections)
            {
                int count = Math.Max(1, section.Lines.Count(l => l.IsBullet));

                switch (section.Category)
                {
                    case ChangelogCategory.New: news += count; break;
                    case ChangelogCategory.Fixed: fixes += count; break;
                    case ChangelogCategory.Changed: changes += count; break;
                }
            }

            var parts = new List<string>();

            if (news > 0)
                parts.Add(news == 1 ? "1 new thing" : $"{news} new things");
            if (fixes > 0)
                parts.Add(fixes == 1 ? "1 fix" : $"{fixes} fixes");
            if (changes > 0)
                parts.Add(changes == 1 ? "1 change" : $"{changes} changes");

            return string.Join(" · ", parts);
        }

        private static string Age(TimeSpan span)
        {
            if (span.TotalDays >= 365)
            {
                int years = (int)(span.TotalDays / 365);
                return years == 1 ? "a year ago" : $"{years} years ago";
            }

            if (span.TotalDays >= 30)
            {
                int months = (int)(span.TotalDays / 30);
                return months == 1 ? "a month ago" : $"{months} months ago";
            }

            if (span.TotalDays >= 1)
            {
                int days = (int)span.TotalDays;
                return days == 1 ? "yesterday" : $"{days} days ago";
            }

            if (span.TotalHours >= 1)
            {
                int hours = (int)span.TotalHours;
                return hours == 1 ? "an hour ago" : $"{hours} hours ago";
            }

            return "just now";
        }
    }

    public class ChangelogSectionRow
    {
        public ChangelogSectionRow(ChangelogSection section)
        {
            Heading = section.Heading;

            BadgeText = section.Category switch
            {
                ChangelogCategory.New => "NEW",
                ChangelogCategory.Fixed => "FIXED",
                ChangelogCategory.Changed => "CHANGED",
                ChangelogCategory.Note => "HEADS UP",
                _ => ""
            };

            BadgeVisibility = BadgeText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            // A "## Fixed" heading next to a FIXED badge reads as a stutter, so when the heading
            // says nothing the badge hasn't already said, drop it and keep the badge. "Also fixed"
            // and "New: Scripts tab" carry real information, so they stay.
            bool redundant = string.Equals(Heading.TrimEnd(':', '.'), BadgeText, StringComparison.OrdinalIgnoreCase);

            HeadingTextVisibility = Heading.Length > 0 && !redundant ? Visibility.Visible : Visibility.Collapsed;
            HeaderVisibility = Heading.Length > 0 || BadgeText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            string prefix = section.Category switch
            {
                ChangelogCategory.New => "Imp",
                ChangelogCategory.Fixed => "Fix",
                ChangelogCategory.Changed => "Chg",
                ChangelogCategory.Note => "Note",
                _ => "Neutral"
            };

            BadgeBackground = Lookup(prefix + "BadgeBg");
            BadgeForeground = Lookup(prefix + "BadgeFg");

            // The rail is the same hue as the badge text, which is what ties a run of bullets to the
            // heading above it once several sections stack up.
            RailBrush = BadgeForeground;

            foreach (var line in section.Lines)
                Lines.Add(new ChangelogLineRow(line));
        }

        public string Heading { get; }

        // The header row as a whole, and the heading text within it — a section can show a badge
        // with no heading text, so the two are separate.
        public Visibility HeaderVisibility { get; }

        public Visibility HeadingTextVisibility { get; }

        public string BadgeText { get; }

        public Visibility BadgeVisibility { get; }

        public Brush BadgeBackground { get; }

        public Brush BadgeForeground { get; }

        public Brush RailBrush { get; }

        public List<ChangelogLineRow> Lines { get; } = new();

        // Resolved from Brand.xaml at row-build time. A missing key would otherwise bind to null and
        // paint nothing at all, so an unknown category falls back to a readable grey.
        private static Brush Lookup(string key) =>
            Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gainsboro;
    }

    public class ChangelogLineRow
    {
        public ChangelogLineRow(ChangelogLine line)
        {
            Text = line.Text;
            BulletVisibility = line.IsBullet ? Visibility.Visible : Visibility.Collapsed;

            // Paragraphs sit flush with the heading; bullets clear the dot.
            Margin = line.IsBullet ? new Thickness(0, 3, 0, 0) : new Thickness(0, 5, 0, 2);
        }

        public string Text { get; }

        public Visibility BulletVisibility { get; }

        public Thickness Margin { get; }
    }
}
