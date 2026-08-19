using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

using CommunityToolkit.Mvvm.Input;

using BeastStrap.Utility;

namespace BeastStrap.UI.ViewModels.Settings
{
    // Backs the News page. Two independent feeds:
    //   • Announcements — DevForum "updates/announcements" topics.
    //   • Release notes  — a release picker (from DevForum "release-notes" titles) plus the selected
    //                      release's Improvements/Fixes, with real Live/Pending status from create.roblox.com.
    // Both cache in RobloxNewsClient, so navigating away and back doesn't refetch until the cache ages out.
    public class NewsViewModel : NotifyPropertyChangedViewModel
    {
        private const string LOG_IDENT = "NewsViewModel";

        public NewsViewModel()
        {
            AnnouncementsView = CollectionViewSource.GetDefaultView(_announcements);
            AnnouncementsView.Filter = FilterAnnouncement;

            ReleaseNotesView = CollectionViewSource.GetDefaultView(_releaseNotes);
            ReleaseNotesView.Filter = FilterReleaseNote;

            _ = LoadAnnouncementsAsync(false);
            _ = LoadReleasesAsync(false);
        }

        // ===================== Announcements =====================

        private readonly ObservableCollection<AnnouncementRow> _announcements = new();
        public ICollectionView AnnouncementsView { get; }

        private bool _announcementsLoading;
        public bool AnnouncementsLoading
        {
            get => _announcementsLoading;
            set { _announcementsLoading = value; OnPropertyChanged(nameof(AnnouncementsLoading)); OnPropertyChanged(nameof(AnnouncementsNotLoading)); }
        }
        public bool AnnouncementsNotLoading => !_announcementsLoading;

        private string _announcementSearch = "";
        public string AnnouncementSearch
        {
            get => _announcementSearch;
            set { _announcementSearch = value ?? ""; OnPropertyChanged(nameof(AnnouncementSearch)); AnnouncementsView.Refresh(); }
        }

        private string _announcementsFooter = "";
        public string AnnouncementsFooter
        {
            get => _announcementsFooter;
            set { _announcementsFooter = value; OnPropertyChanged(nameof(AnnouncementsFooter)); }
        }

        public string AnnouncementsSourceUrl => "https://devforum.roblox.com/c/updates/announcements/36";

        public ICommand RefreshAnnouncementsCommand => new AsyncRelayCommand(() => LoadAnnouncementsAsync(true));

        private async Task LoadAnnouncementsAsync(bool force)
        {
            AnnouncementsLoading = true;
            AnnouncementsFooter = "Loading…";
            try
            {
                var topics = await RobloxNewsClient.GetAnnouncementsAsync(force);
                _announcements.Clear();
                foreach (var t in topics)
                    _announcements.Add(new AnnouncementRow(t));
                AnnouncementsView.Refresh();

                if (topics.Count == 0)
                {
                    AnnouncementsFooter = "Couldn't reach DevForum. Check your connection and refresh.";
                }
                else
                {
                    var age = DateTime.Now - RobloxNewsClient.AnnouncementsFetchedAt;
                    string cached = age.TotalMinutes < 1 ? "just now" : $"cached {(int)age.TotalMinutes} min ago";
                    AnnouncementsFooter = $"{topics.Count} topics · {cached}";
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Announcements", ex);
                AnnouncementsFooter = "Couldn't load announcements.";
            }
            finally
            {
                AnnouncementsLoading = false;
            }
        }

        private bool FilterAnnouncement(object o)
        {
            if (o is not AnnouncementRow r)
                return false;
            if (_announcementSearch.Length == 0)
                return true;
            return r.Title.Contains(_announcementSearch, StringComparison.OrdinalIgnoreCase)
                || r.Excerpt.Contains(_announcementSearch, StringComparison.OrdinalIgnoreCase);
        }

        // ===================== Release notes =====================

        private readonly ObservableCollection<ReleaseNoteRow> _releaseNotes = new();
        public ICollectionView ReleaseNotesView { get; }

        public ObservableCollection<int> Releases { get; } = new();

        private bool _suppressReleaseLoad;
        private int? _selectedRelease;
        public int? SelectedRelease
        {
            get => _selectedRelease;
            set
            {
                if (_selectedRelease == value)
                    return;
                _selectedRelease = value;
                OnPropertyChanged(nameof(SelectedRelease));
                OnPropertyChanged(nameof(ReleaseDocsUrl));
                if (!_suppressReleaseLoad && value.HasValue)
                    _ = LoadReleaseNotesAsync(value.Value, false);
            }
        }

        private bool _releaseNotesLoading;
        public bool ReleaseNotesLoading
        {
            get => _releaseNotesLoading;
            set { _releaseNotesLoading = value; OnPropertyChanged(nameof(ReleaseNotesLoading)); OnPropertyChanged(nameof(ReleaseNotesNotLoading)); }
        }
        public bool ReleaseNotesNotLoading => !_releaseNotesLoading;

        private string _releaseNoteSearch = "";
        public string ReleaseNoteSearch
        {
            get => _releaseNoteSearch;
            set { _releaseNoteSearch = value ?? ""; OnPropertyChanged(nameof(ReleaseNoteSearch)); ReleaseNotesView.Refresh(); }
        }

        // "All" | "Improvements" | "Fixes"
        private string _typeFilter = "All";
        public string TypeFilter
        {
            get => _typeFilter;
            set { _typeFilter = value ?? "All"; OnPropertyChanged(nameof(TypeFilter)); ReleaseNotesView.Refresh(); }
        }

        // "All" | "Live" | "Pending"
        private string _statusFilter = "All";
        public string StatusFilter
        {
            get => _statusFilter;
            set { _statusFilter = value ?? "All"; OnPropertyChanged(nameof(StatusFilter)); ReleaseNotesView.Refresh(); }
        }

        private int _impCount, _fixCount, _liveCount, _pendingCount;
        public string ImpCountText => $"Imp {_impCount}";
        public string FixCountText => $"Fix {_fixCount}";
        public string LiveCountText => $"Live {_liveCount}";
        public string PendCountText => $"Pend {_pendingCount}";
        public string ReleaseNotesSummary => $"{_liveCount} live · {_pendingCount} pending";

        private string _releaseNotesFooter = "";
        public string ReleaseNotesFooter
        {
            get => _releaseNotesFooter;
            set { _releaseNotesFooter = value; OnPropertyChanged(nameof(ReleaseNotesFooter)); }
        }

        public string ReleaseForumUrl => "https://devforum.roblox.com/c/updates/release-notes/62";
        public string ReleaseDocsUrl =>
            _selectedRelease.HasValue
                ? $"https://create.roblox.com/docs/en-us/release-notes/release-notes-{_selectedRelease.Value}"
                : "https://create.roblox.com/docs/release-notes";

        public ICommand RefreshReleaseNotesCommand => new AsyncRelayCommand(RefreshReleasesAsync);

        private async Task RefreshReleasesAsync()
        {
            await LoadReleasesAsync(true);
        }

        private async Task LoadReleasesAsync(bool force)
        {
            try
            {
                var numbers = await RobloxNewsClient.GetReleaseNumbersAsync(force);
                if (numbers.Count == 0)
                {
                    if (_releaseNotes.Count == 0)
                        ReleaseNotesFooter = "Couldn't reach the release-notes list.";
                    return;
                }

                // Only rebuild the picker if the set of releases actually changed — that keeps the user's
                // current selection intact across a refresh instead of snapping back to the newest.
                if (!Releases.SequenceEqual(numbers))
                {
                    int? previous = _selectedRelease;
                    _suppressReleaseLoad = true;
                    Releases.Clear();
                    foreach (var n in numbers)
                        Releases.Add(n);
                    _selectedRelease = previous.HasValue && numbers.Contains(previous.Value) ? previous : numbers[0];
                    OnPropertyChanged(nameof(SelectedRelease));
                    OnPropertyChanged(nameof(ReleaseDocsUrl));
                    _suppressReleaseLoad = false;
                }

                if (_selectedRelease.HasValue)
                    await LoadReleaseNotesAsync(_selectedRelease.Value, force);
                else
                    SelectedRelease = numbers[0];
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Releases", ex);
            }
        }

        private async Task LoadReleaseNotesAsync(int release, bool force)
        {
            ReleaseNotesLoading = true;
            ReleaseNotesFooter = "Loading…";
            try
            {
                var notes = await RobloxNewsClient.GetReleaseNotesAsync(release, force);

                // If the user switched releases while this was awaiting, drop the stale result and let
                // the newer load own the panel (data, counts, and the loading flag).
                if (release != _selectedRelease)
                    return;

                _releaseNotes.Clear();
                foreach (var e in notes.Entries)
                    _releaseNotes.Add(new ReleaseNoteRow(e));

                _impCount = notes.Entries.Count(e => e.Type.StartsWith("Improvement", StringComparison.OrdinalIgnoreCase));
                _fixCount = notes.Entries.Count(e => e.Type.StartsWith("Fix", StringComparison.OrdinalIgnoreCase));
                _liveCount = notes.Entries.Count(e => e.Status.Equals("Live", StringComparison.OrdinalIgnoreCase));
                _pendingCount = notes.Entries.Count(e => e.Status.Equals("Pending", StringComparison.OrdinalIgnoreCase));
                OnPropertyChanged(nameof(ImpCountText));
                OnPropertyChanged(nameof(FixCountText));
                OnPropertyChanged(nameof(LiveCountText));
                OnPropertyChanged(nameof(PendCountText));
                OnPropertyChanged(nameof(ReleaseNotesSummary));

                ReleaseNotesView.Refresh();

                ReleaseNotesFooter = notes.Entries.Count == 0
                    ? $"No notes found for release {release} yet — it may not be published. Try Docs to open it on create.roblox.com."
                    : $"Release {release} · {notes.Entries.Count} notes";
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::ReleaseNotes", ex);
                if (release == _selectedRelease)
                    ReleaseNotesFooter = "Couldn't load release notes.";
            }
            finally
            {
                // Only the load for the current selection owns the loading flag — a stale load bailing
                // out must not switch the ring off while the newer load is still running.
                if (release == _selectedRelease)
                    ReleaseNotesLoading = false;
            }
        }

        private bool FilterReleaseNote(object o)
        {
            if (o is not ReleaseNoteRow r)
                return false;
            if (!_typeFilter.Equals("All", StringComparison.OrdinalIgnoreCase)
                && !r.Type.StartsWith(_typeFilter.TrimEnd('s'), StringComparison.OrdinalIgnoreCase))
                return false;
            if (!_statusFilter.Equals("All", StringComparison.OrdinalIgnoreCase)
                && !r.Status.Equals(_statusFilter, StringComparison.OrdinalIgnoreCase))
                return false;
            if (_releaseNoteSearch.Length > 0
                && !r.Text.Contains(_releaseNoteSearch, StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }
    }

    // ---- row view-models --------------------------------------------------------------

    public class AnnouncementRow : NotifyPropertyChangedViewModel
    {
        private readonly RobloxNewsClient.DevForumTopic _t;
        public AnnouncementRow(RobloxNewsClient.DevForumTopic t) => _t = t;

        public string Title => _t.Title;
        public string Excerpt => _t.Excerpt;
        public string Url => _t.Url;

        public Visibility PinnedVisibility => _t.Pinned ? Visibility.Visible : Visibility.Collapsed;
        public string RepliesText => Compact(_t.Replies);
        public string ViewsText => Compact(_t.Views);
        public string LikesText => Compact(_t.Likes);
        public string AgeText => Age(_t.CreatedAt);

        private bool _expanded;
        public bool IsExpanded
        {
            get => _expanded;
            set
            {
                _expanded = value;
                OnPropertyChanged(nameof(IsExpanded));
                OnPropertyChanged(nameof(ChevronSymbol));
                OnPropertyChanged(nameof(ExpandedVisibility));
                OnPropertyChanged(nameof(ExcerptMaxHeight));
            }
        }
        public Wpf.Ui.Common.SymbolRegular ChevronSymbol =>
            _expanded ? Wpf.Ui.Common.SymbolRegular.ChevronDown24 : Wpf.Ui.Common.SymbolRegular.ChevronRight24;
        public Visibility ExpandedVisibility => _expanded ? Visibility.Visible : Visibility.Collapsed;
        // Collapsed cards clip the excerpt to a couple of lines; expanded shows it all.
        public double ExcerptMaxHeight => _expanded ? double.PositiveInfinity : 36;

        public ICommand ToggleCommand => new RelayCommand(() => IsExpanded = !IsExpanded);

        private static string Compact(int n) =>
            n >= 1000 ? $"{n / 1000.0:0.#}K" : n.ToString();

        private static string Age(DateTime dt)
        {
            if (dt == DateTime.MinValue)
                return "";
            var span = DateTime.Now - dt;
            if (span.TotalMinutes < 60)
                return $"{Math.Max(1, (int)span.TotalMinutes)}m ago";
            if (span.TotalHours < 24)
                return $"{(int)span.TotalHours}h ago";
            return $"{(int)span.TotalDays}d ago";
        }
    }

    public class ReleaseNoteRow
    {
        private readonly RobloxNewsClient.ReleaseNoteEntry _e;
        public ReleaseNoteRow(RobloxNewsClient.ReleaseNoteEntry e) => _e = e;

        public string Text => _e.Text;
        public string Type => _e.Type;
        public string Status => _e.Status;

        public bool IsFix => _e.Type.StartsWith("Fix", StringComparison.OrdinalIgnoreCase);
        public string TypeBadge => IsFix ? "FIX" : "IMP";
        public bool IsPending => _e.Status.Equals("Pending", StringComparison.OrdinalIgnoreCase);
        public Visibility StatusVisibility => string.IsNullOrEmpty(_e.Status) ? Visibility.Collapsed : Visibility.Visible;
    }
}
