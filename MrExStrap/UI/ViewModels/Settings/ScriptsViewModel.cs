using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;

using BeastStrap.Models.APIs;
using BeastStrap.Models.Persistable;
using BeastStrap.Utility;

namespace BeastStrap.UI.ViewModels.Settings
{
    // Backs the Scripts page: search robloxscripts.com, copy a script, star the good ones.
    //
    // Search is SERVER-side (the API takes q / access / sort / placeId / page), which is the main
    // way this differs from the News page. That means:
    //   • typing must be debounced — the API allows 60 requests a minute and a keystroke-triggered
    //     search would burn that in seconds;
    //   • the access and sort chips re-query rather than filtering a local list;
    //   • "Favourites" is the one genuinely local view, and it reads straight out of State so it
    //     still works with no connection.
    public class ScriptsViewModel : NotifyPropertyChangedViewModel
    {
        private const string LOG_IDENT = "ScriptsViewModel";

        // Long enough that a normal typing burst is one request, short enough to feel live.
        private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(400);

        public ObservableCollection<ScriptRow> Scripts { get; } = new();

        public ScriptsViewModel()
        {
            // Always open on the whole catalogue. This used to open pre-filtered to the last game
            // played, and since the page is rebuilt every time the settings window opens, that filter
            // came back on EVERY launch: whoever last joined a test baseplate got "Showing scripts for
            // [UP] Just a baseplate." forever and had to dismiss it each time just to browse. The last
            // game is still one click away, as a suggestion chip, but it's the user's call now.
            var recent = App.State.Prop.RecentPlaces.FirstOrDefault();
            if (recent is not null && recent.PlaceId > 0)
            {
                _suggestedPlaceId = recent.PlaceId;
                _suggestedPlaceName = recent.DisplayLabel;
            }

            _ = SearchAsync(false);
        }

        // ---- filters ----------------------------------------------------------------

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set
            {
                _searchText = value ?? "";
                OnPropertyChanged(nameof(SearchText));

                // Typing a query means they've stopped caring about the place pre-filter.
                if (_searchText.Length > 0 && _placeId > 0)
                    ClearPlaceFilter();

                QueueSearch();
            }
        }

        private string _accessFilter = "All";
        public string AccessFilter
        {
            get => _accessFilter;
            set
            {
                _accessFilter = value ?? "All";
                OnPropertyChanged(nameof(AccessFilter));
                _ = SearchAsync(false);
            }
        }

        private bool _verifiedOnly;
        public bool VerifiedOnly
        {
            get => _verifiedOnly;
            set
            {
                _verifiedOnly = value;
                OnPropertyChanged(nameof(VerifiedOnly));
                _ = SearchAsync(false);
            }
        }

        private string _sortOption = "Most popular";
        public string SortOption
        {
            get => _sortOption;
            set
            {
                _sortOption = value ?? "Most popular";
                OnPropertyChanged(nameof(SortOption));
                _ = SearchAsync(false);
            }
        }

        public List<string> SortOptions { get; } = new()
        {
            "Most popular", "Most liked", "Trending", "Newest", "Title (A-Z)"
        };

        // API sort keys. "Most popular" is the default because a catalogue this size sorted by
        // newest is mostly noise.
        private string ApiSort => _sortOption switch
        {
            "Most liked" => "most-liked",
            "Trending" => "trending",
            "Newest" => "newest",
            "Title (A-Z)" => "title-asc",
            _ => "most-viewed",
        };

        private string? ApiAccess => _accessFilter switch
        {
            "Free" => "free",
            "Key" => "key",
            "Paid" => "paid",
            _ => null,
        };

        // ---- the "scripts for this game" chip ---------------------------------------

        private long _placeId;
        private string _placeName = "";

        public string PlaceFilterText => $"Showing scripts for {_placeName}";
        public Visibility PlaceFilterVisibility => _placeId > 0 ? Visibility.Visible : Visibility.Collapsed;

        public ICommand ClearPlaceFilterCommand => new RelayCommand(() => { ClearPlaceFilter(); _ = SearchAsync(false); });

        private void ClearPlaceFilter()
        {
            _placeId = 0;
            _placeName = "";
            RaisePlaceChips();
        }

        // The last game played, offered as a one-click filter rather than applied on open.
        private long _suggestedPlaceId;
        private string _suggestedPlaceName = "";

        public string PlaceSuggestionText => $"Scripts for {_suggestedPlaceName}";
        public Visibility PlaceSuggestionVisibility =>
            _suggestedPlaceId > 0 && _placeId == 0 && !_showFavourites ? Visibility.Visible : Visibility.Collapsed;

        public ICommand ApplyPlaceSuggestionCommand => new RelayCommand(() =>
        {
            if (_suggestedPlaceId <= 0)
                return;

            // A typed query clears the place filter the moment it's set (see SearchText), so the
            // query has to go first or the chip would undo itself.
            if (_searchText.Length > 0)
            {
                _searchText = "";
                OnPropertyChanged(nameof(SearchText));
            }

            _placeId = _suggestedPlaceId;
            _placeName = _suggestedPlaceName;
            RaisePlaceChips();
            _ = SearchAsync(false);
        });

        private void RaisePlaceChips()
        {
            OnPropertyChanged(nameof(PlaceFilterText));
            OnPropertyChanged(nameof(PlaceFilterVisibility));
            OnPropertyChanged(nameof(PlaceSuggestionText));
            OnPropertyChanged(nameof(PlaceSuggestionVisibility));
        }


        // ---- favourites -------------------------------------------------------------

        private bool _showFavourites;
        public bool ShowFavourites
        {
            get => _showFavourites;
            set
            {
                _showFavourites = value;
                OnPropertyChanged(nameof(ShowFavourites));
                OnPropertyChanged(nameof(SearchEnabled));
                OnPropertyChanged(nameof(PlaceSuggestionVisibility));

                if (_showFavourites)
                {
                    // Stop any pending/in-flight search before showing the local list, or its
                    // response repaints over the favourites a second later.
                    _debounceCts?.Cancel();
                    _searchCts?.Cancel();
                    LoadFavourites();
                }
                else
                {
                    _ = SearchAsync(false);
                }
            }
        }

        /// <summary>Search controls are pointless while the local favourites list is showing.</summary>
        public bool SearchEnabled => !_showFavourites;

        private void LoadFavourites()
        {
            Scripts.Clear();
            foreach (var fav in App.State.Prop.FavouriteScripts)
                Scripts.Add(new ScriptRow(fav, this));

            StatusText = Scripts.Count == 0
                ? "You haven't starred any scripts yet. Star one and it'll show up here, even offline."
                : $"{Scripts.Count} starred script{(Scripts.Count == 1 ? "" : "s")}.";
            IsError = false;
        }

        /// <summary>Called by a row when its star is toggled.</summary>
        internal void OnFavouriteToggled(ScriptRow row, bool nowStarred)
        {
            // Discrete user action, so commit immediately rather than waiting for the Save button.
            // Plain Save (not SaveMerged) — this is the settings UI, the user is looking at it.
            App.State.Save();

            StatusText = nowStarred ? $"Starred \"{row.Title}\"." : $"Removed \"{row.Title}\" from favourites.";
            IsError = false;

            // Keep the favourites list honest in both directions. Un-starring drops the row; re-
            // starring it (easy to do from the details dialog, which stays open over the list) has
            // to put it back, or the list disagrees with what's actually stored.
            if (_showFavourites)
            {
                if (!nowStarred)
                    Scripts.Remove(row);
                else if (!Scripts.Contains(row))
                    Scripts.Insert(0, row);
            }
        }

        // ---- status -----------------------------------------------------------------

        private bool _loading;
        public bool Loading
        {
            get => _loading;
            set { _loading = value; OnPropertyChanged(nameof(Loading)); OnPropertyChanged(nameof(NotLoading)); }
        }
        public bool NotLoading => !_loading;

        private string _statusText = "";
        public string StatusText
        {
            get => _statusText;
            set { _statusText = value ?? ""; OnPropertyChanged(nameof(StatusText)); }
        }

        private bool _isError;
        public bool IsError
        {
            get => _isError;
            set { _isError = value; OnPropertyChanged(nameof(IsError)); }
        }

        // ---- commands ---------------------------------------------------------------

        public ICommand RefreshCommand => new AsyncRelayCommand(() => SearchAsync(true));

        public ICommand CopyCommand => new RelayCommand<ScriptRow?>(row =>
        {
            if (row is null)
                return;

            // The loadstring one-liner, because that's what gets pasted into an executor. The raw
            // body is usually just another loader anyway.
            if (Utilities.TrySetClipboardText(row.Script, LOG_IDENT))
            {
                StatusText = $"Copied \"{row.Title}\" to your clipboard.";
                IsError = false;
            }
            else
            {
                StatusText = "Couldn't copy — something else is holding the clipboard. Try again.";
                IsError = true;
            }
        });

        public ICommand OpenPageCommand => new RelayCommand<ScriptRow?>(row =>
        {
            if (!string.IsNullOrWhiteSpace(row?.ScriptPageUrl))
                GlobalViewModel.OpenWebpageCommand.Execute(row!.ScriptPageUrl);
        });

        // ---- search -----------------------------------------------------------------

        private CancellationTokenSource? _debounceCts;

        /// <summary>Debounced search — one request per typing burst, not per keystroke.</summary>
        private async void QueueSearch()
        {
            _debounceCts?.Cancel();

            var cts = new CancellationTokenSource();
            _debounceCts = cts;

            try
            {
                // Started from the UI thread, so this resumes on the UI thread — which matters
                // because SearchAsync mutates an ObservableCollection.
                await Task.Delay(SearchDebounce, cts.Token);

                if (!cts.IsCancellationRequested)
                    await SearchAsync(false, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a later keystroke. Normal.
            }
            catch (Exception ex)
            {
                // async void — nothing else will ever see this.
                App.Logger.WriteException(LOG_IDENT + "::QueueSearch", ex);
            }
        }

        // One live search at a time. Every entry point — typing, the access chips, Verified only,
        // sort, Refresh, dismissing the place chip — supersedes the previous one.
        //
        // Without this a slower earlier request lands after a faster later one and repaints the list
        // with results that don't match the visible filters. That isn't a rare race here: the client
        // caches by URL for ten minutes, so going back to a filter you already used returns
        // synchronously while the request you just superseded is still open.
        private CancellationTokenSource? _searchCts;

        // `note` goes in front of the result count, for the one case that needs to explain itself: a place
        // filter that found nothing and fell back to the whole catalogue.
        private async Task SearchAsync(bool force, CancellationToken token = default, string? note = null)
        {
            if (_showFavourites)
                return;

            var superseded = _searchCts;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            _searchCts = cts;

            // Cancel and dispose the one we just replaced. Deliberately NOT disposing our own in a
            // finally: it stays reachable as _searchCts, and the next search disposes it here.
            superseded?.Cancel();
            superseded?.Dispose();

            Loading = true;
            StatusText = "Searching…";
            IsError = false;

            try
            {
                var result = await RobloxScriptsClient.SearchAsync(
                    query: _searchText,
                    placeId: _placeId,
                    access: ApiAccess,
                    sort: ApiSort,
                    verifiedOnly: _verifiedOnly,
                    page: 1,
                    forceRefresh: force,
                    token: cts.Token);

                // Stale in any of three ways: a newer search started, we were cancelled, or the user
                // switched to the Favourites view while this was in flight. Touching the list now
                // would wipe whatever replaced it.
                if (!ReferenceEquals(_searchCts, cts) || cts.IsCancellationRequested || _showFavourites)
                    return;

                if (!result.Success)
                {
                    // An empty error means the caller superseded us — say nothing.
                    if (result.Error!.Length > 0)
                    {
                        StatusText = result.Error;
                        IsError = true;
                    }
                    return;
                }

                // ⚠️ An unresolved placeId returns ZERO results, not the whole catalogue, and plenty of
                // places have no scripts indexed. Drop the filter and search again for real, and stop
                // suggesting that game for the rest of the session since we now know it's empty.
                if (_placeId > 0 && result.MatchedGameName is null)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"No scripts indexed for place {_placeId}; showing the full catalogue instead.");
                    string fallback = $"robloxscripts.com doesn't list any scripts for {_placeName} yet, so here's everything.";
                    if (_suggestedPlaceId == _placeId)
                        _suggestedPlaceId = 0;
                    ClearPlaceFilter();
                    await SearchAsync(force, token, fallback);   // _placeId is 0 now, so this can't recurse
                    return;
                }

                var scripts = result.Scripts;

                // ⚠️ placeId OVERRIDES access and verified server-side — verified against the live
                // API, the same 102 mixed free/key/paid results come back whether or not those
                // parameters are sent. Since the tab OPENS with a place filter, leaving this to the
                // server makes the chips look broken by default. Filter the page ourselves.
                if (_placeId > 0)
                {
                    string? access = ApiAccess;
                    if (access is not null || _verifiedOnly)
                    {
                        scripts = result.Scripts
                            .Where(s => (access is null || string.Equals(s.AccessType, access, StringComparison.OrdinalIgnoreCase))
                                     && (!_verifiedOnly || s.Author?.Verified == true))
                            .ToList();
                    }
                }

                Scripts.Clear();
                foreach (var script in scripts)
                {
                    var row = new ScriptRow(script, this);
                    Scripts.Add(row);

                    // Fire-and-forget per row: 24 thumbnails shouldn't hold up the list appearing,
                    // and each one caches to disk so a repeat search is instant.
                    _ = row.LoadThumbnailAsync();
                }

                // RecentPlaces often has no name (the join couldn't resolve a title), so take the
                // one the API resolved — that's how the chip ends up saying "Blox Fruits" rather
                // than "Place 2753915549".
                if (result.MatchedGameName is not null)
                {
                    _placeName = result.MatchedGameName;
                    if (_suggestedPlaceId == _placeId)
                        _suggestedPlaceName = result.MatchedGameName;
                    RaisePlaceChips();
                }

                string counts = Scripts.Count == 0
                    ? "No scripts matched. Try a different search."
                    : $"Showing {Scripts.Count} of {result.Total} scripts.";

                StatusText = note is null ? counts : $"{note} {counts}";
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Search", ex);
                StatusText = "Something went wrong searching. Try again.";
                IsError = true;
            }
            finally
            {
                // A search that has been superseded must not clear the spinner for the one that
                // replaced it, which is still running.
                if (ReferenceEquals(_searchCts, cts))
                    Loading = false;
            }
        }
    }

    /// <summary>One row in the scripts list. Wraps either an API result or a stored favourite.</summary>
    public class ScriptRow : NotifyPropertyChangedViewModel
    {
        private readonly ScriptsViewModel _owner;

        public string Slug { get; }
        public string Title { get; }
        public string Description { get; }
        public string Game { get; }
        public string Author { get; }
        public string AccessType { get; }
        public string Script { get; }
        public string ScriptPageUrl { get; }
        public int Views { get; }
        public int Likes { get; }
        public bool AuthorVerified { get; }

        /// <summary>
        /// The full API payload behind this row, for the details dialog. Null on rows built from a
        /// stored favourite — State only keeps enough to render the list offline — in which case
        /// the dialog re-fetches by slug.
        /// </summary>
        public RobloxScript? Source { get; }

        private readonly string _imageUrl;

        public ScriptRow(RobloxScript script, ScriptsViewModel owner)
        {
            _owner = owner;
            Source = script;
            Slug = script.Slug;
            Title = script.Title;
            // Descriptions are markdown and often long; the list wants one tidy line.
            Description = Flatten(script.Description);
            // `game` (object) and `games` (array of names) are mutually exclusive in practice —
            // across 400 sampled scripts not one had both — so checking only `games` leaves roughly
            // two rows in three with a blank game name.
            Game = script.IsUniversal
                ? "Universal"
                : string.IsNullOrWhiteSpace(script.GameInfo?.Name) ? FirstGame(script.Games) : script.GameInfo!.Name;
            Author = script.Author?.Username ?? "";
            AuthorVerified = script.Author?.Verified ?? false;
            _imageUrl = script.Image ?? "";
            AccessType = script.AccessType;
            Script = script.Script;
            ScriptPageUrl = script.ScriptPageUrl;
            Views = script.Views;
            Likes = script.Likes;

            _isFavourite = App.State.Prop.IsScriptFavourited(Slug);
        }

        public ScriptRow(FavouriteScript fav, ScriptsViewModel owner)
        {
            _owner = owner;
            Slug = fav.Slug;
            Title = fav.Title;
            Description = "";
            Game = fav.Game;
            Author = "";
            AuthorVerified = false;
            _imageUrl = "";
            AccessType = fav.AccessType;
            Script = fav.Script;
            ScriptPageUrl = fav.ScriptPageUrl;
            _isFavourite = true;
        }

        private bool _isFavourite;
        public bool IsFavourite
        {
            get => _isFavourite;
            private set
            {
                _isFavourite = value;
                OnPropertyChanged(nameof(IsFavourite));
                OnPropertyChanged(nameof(FavouriteSymbol));
                OnPropertyChanged(nameof(FavouriteTooltip));
            }
        }

        // ---- thumbnail --------------------------------------------------------------

        private ImageSource? _thumbnail;
        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            private set
            {
                _thumbnail = value;
                OnPropertyChanged(nameof(Thumbnail));
                OnPropertyChanged(nameof(ThumbnailVisibility));
                OnPropertyChanged(nameof(PlaceholderVisibility));
            }
        }

        public Visibility ThumbnailVisibility => _thumbnail is null ? Visibility.Collapsed : Visibility.Visible;

        // Roughly one script in five has no image, so the placeholder is a normal state, not an
        // error. A single letter reads better at 44px than a generic icon.
        public Visibility PlaceholderVisibility => _thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

        public string PlaceholderGlyph =>
            string.IsNullOrWhiteSpace(Title) ? "?" : Title.Trim()[..1].ToUpperInvariant();

        /// <summary>Downloads and decodes this row's thumbnail. Safe to fire and forget.</summary>
        public async Task LoadThumbnailAsync()
        {
            if (string.IsNullOrWhiteSpace(_imageUrl))
                return;

            try
            {
                string? path = await ExecutorLogoCache.GetLogoAsync(
                    _imageUrl,
                    folder: ExecutorLogoCache.ScriptThumbnailsFolder,
                    maxFiles: 300);

                if (string.IsNullOrEmpty(path))
                    return;

                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null)
                    return;

                dispatcher.Invoke(() =>
                {
                    try
                    {
                        var img = new BitmapImage();
                        img.BeginInit();
                        img.CacheOption = BitmapCacheOption.OnLoad;   // don't hold the file open
                        img.DecodePixelWidth = 144;                   // 72px tile at 2x; the cache stores at most 128px
                        img.UriSource = new Uri(path);
                        img.EndInit();
                        img.Freeze();
                        Thumbnail = img;
                    }
                    catch (Exception ex)
                    {
                        // A truncated or non-image response just leaves the placeholder up.
                        App.Logger.WriteException("ScriptRow::LoadThumbnailAsync::Bitmap", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("ScriptRow::LoadThumbnailAsync", ex);
            }
        }

        // ---- author -----------------------------------------------------------------

        public string AuthorText => string.IsNullOrWhiteSpace(Author) ? "" : $"by {Author}";

        public Visibility AuthorVisibility => string.IsNullOrWhiteSpace(Author) ? Visibility.Collapsed : Visibility.Visible;

        public Visibility VerifiedVisibility => AuthorVerified ? Visibility.Visible : Visibility.Collapsed;

        // ---- favourites ---------------------------------------------------------------

        public Wpf.Ui.Common.SymbolRegular FavouriteSymbol =>
            _isFavourite ? Wpf.Ui.Common.SymbolRegular.Star24 : Wpf.Ui.Common.SymbolRegular.StarAdd24;

        public string FavouriteTooltip => _isFavourite ? "Remove from favourites" : "Add to favourites";

        public string AccessLabel => AccessType switch
        {
            "free" => "Free",
            "key" => "Key",
            "paid" => "Paid",
            _ => "",
        };

        public Visibility AccessVisibility => string.IsNullOrEmpty(AccessLabel) ? Visibility.Collapsed : Visibility.Visible;

        public string StatsText => Views > 0 ? $"{Views:N0} views · {Likes:N0} likes" : "";

        public ICommand ToggleFavouriteCommand => new RelayCommand(() =>
        {
            bool starred = App.State.Prop.ToggleFavouriteScript(new FavouriteScript
            {
                Slug = Slug,
                Title = Title,
                Game = Game,
                AccessType = AccessType,
                Script = Script,
                ScriptPageUrl = ScriptPageUrl,
            });

            IsFavourite = starred;
            _owner.OnFavouriteToggled(this, starred);
        });

        // "Game 4954752502" placeholders come back from the API on hub entries where it never
        // resolved a name. Showing the raw id helps nobody.
        private static string FirstGame(List<string>? games)
        {
            string first = games?.FirstOrDefault() ?? "";
            return first.StartsWith("Game ", StringComparison.Ordinal) ? "" : first;
        }

        private static string Flatten(string? markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown))
                return "";

            string text = markdown.Replace('\r', ' ').Replace('\n', ' ').Replace("#", "").Replace("*", "");
            while (text.Contains("  "))
                text = text.Replace("  ", " ");

            text = text.Trim();
            return text.Length > 220 ? text[..220] + "…" : text;
        }
    }
}
