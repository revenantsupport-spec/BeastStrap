using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

using BeastStrap.Models.APIs;
using BeastStrap.UI.ViewModels.Settings;
using BeastStrap.Utility;

namespace BeastStrap.UI.ViewModels.Dialogs
{
    // Backs the dialog that opens when a script row is clicked: the full description, what the
    // script actually does (features), its tags, and any alternative builds.
    //
    // The list rows deliberately flatten and truncate — this is where the whole record gets shown.
    // Everything comes from the payload the search already returned, so opening this costs no
    // requests. The one exception is a starred script opened from the Favourites view: State only
    // keeps enough to render that list offline, so the rest is fetched by slug on open.
    public class ScriptDetailsViewModel : NotifyPropertyChangedViewModel
    {
        private const string LOG_IDENT = "ScriptDetailsViewModel";

        public event EventHandler? CloseRequested;

        /// <summary>The list row this was opened from — the star stays in sync through it.</summary>
        public ScriptRow Row { get; }

        private RobloxScript? _script;

        public ScriptDetailsViewModel(ScriptRow row)
        {
            Row = row;
            _script = row.Source;

            if (_script is not null)
                Populate(_script);
            else
                _ = LoadAsync();   // favourite: we only stored the slug
        }

        // ---- loading (favourites only) ----------------------------------------------

        private bool _loading;
        public bool Loading
        {
            get => _loading;
            set { _loading = value; OnPropertyChanged(nameof(Loading)); }
        }

        private string _statusText = "";
        public string StatusText
        {
            get => _statusText;
            set { _statusText = value ?? ""; OnPropertyChanged(nameof(StatusText)); }
        }

        private async Task LoadAsync()
        {
            Loading = true;
            StatusText = "Loading details…";

            try
            {
                var script = await RobloxScriptsClient.GetBySlugAsync(Row.Slug, Row.Title);

                if (script is null)
                {
                    // Starred scripts can be delisted, and we're offline-tolerant by design, so this
                    // is a normal outcome rather than an error. The dialog still shows the stored
                    // title and the Copy button still works off State.
                    StatusText = "Couldn't load the full details — the script may have been removed, or you're offline.";
                    return;
                }

                _script = script;
                Populate(script);
                StatusText = "";
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Load", ex);
                StatusText = "Couldn't load the full details.";
            }
            finally
            {
                Loading = false;
            }
        }

        private void Populate(RobloxScript script)
        {
            Description = CleanDescription(script.Description);

            Features.Clear();
            foreach (string feature in Tidy(script.Features, capitalise: true))
                Features.Add(feature);

            SupportedGames.Clear();
            foreach (var game in BuildGameLinks(script))
                SupportedGames.Add(game);

            Variants.Clear();
            foreach (var variant in script.Variants ?? new List<RobloxScriptVariant>())
                Variants.Add(new ScriptVariantRow(variant, this));

            OnPropertyChanged(nameof(GamesVisibility));
            OnPropertyChanged(nameof(GamesHeader));

            _author = script.Author?.Username ?? "";
            _authorVerified = script.Author?.Verified ?? false;

            _meta = BuildMeta(script);
            _keyLink = script.KeyLink ?? "";
            _needsKey = script.Key || script.AccessType == "key";

            OnPropertyChanged(nameof(AuthorText));
            OnPropertyChanged(nameof(AuthorVisibility));
            OnPropertyChanged(nameof(VerifiedVisibility));
            OnPropertyChanged(nameof(Meta));
            OnPropertyChanged(nameof(FeaturesVisibility));
            OnPropertyChanged(nameof(VariantsVisibility));
            OnPropertyChanged(nameof(VariantsHeader));
            OnPropertyChanged(nameof(KeyVisibility));
            OnPropertyChanged(nameof(KeyLinkVisibility));
            OnPropertyChanged(nameof(DescriptionVisibility));
        }

        // ---- content ------------------------------------------------------------------

        public string Title => Row.Title;

        // A row built from a stored favourite has no author — State doesn't keep one — so prefer
        // whatever the fetch turned up and only fall back to the row.
        private string _author = "";
        private bool _authorVerified;

        public string AuthorText =>
            string.IsNullOrWhiteSpace(_author) ? Row.AuthorText : $"by {_author}";

        public Visibility AuthorVisibility =>
            string.IsNullOrWhiteSpace(_author) ? Row.AuthorVisibility : Visibility.Visible;

        public Visibility VerifiedVisibility =>
            string.IsNullOrWhiteSpace(_author)
                ? Row.VerifiedVisibility
                : _authorVerified ? Visibility.Visible : Visibility.Collapsed;

        public string AccessLabel => Row.AccessLabel;
        public Visibility AccessVisibility => Row.AccessVisibility;

        public string PlaceholderGlyph => Row.PlaceholderGlyph;

        private string _description = "";
        public string Description
        {
            get => _description;
            private set
            {
                _description = value ?? "";
                OnPropertyChanged(nameof(Description));
                OnPropertyChanged(nameof(DescriptionVisibility));
            }
        }

        public Visibility DescriptionVisibility =>
            string.IsNullOrWhiteSpace(_description) ? Visibility.Collapsed : Visibility.Visible;

        // Tags are deliberately not surfaced — they're near-duplicates of the features and made the
        // dialog noisier without adding anything. The API still returns them if that changes.
        public ObservableCollection<string> Features { get; } = new();
        public ObservableCollection<GameLinkRow> SupportedGames { get; } = new();
        public ObservableCollection<ScriptVariantRow> Variants { get; } = new();

        public Visibility FeaturesVisibility => Features.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        public Visibility GamesVisibility => SupportedGames.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        public Visibility VariantsVisibility => Variants.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        public string GamesHeader => $"Supported games ({SupportedGames.Count})";
        public string VariantsHeader => $"Versions ({Variants.Count})";

        private string _meta = "";
        public string Meta => _meta;

        // ---- key system ---------------------------------------------------------------

        private bool _needsKey;
        private string _keyLink = "";

        public Visibility KeyVisibility => _needsKey ? Visibility.Visible : Visibility.Collapsed;

        public Visibility KeyLinkVisibility =>
            _needsKey && !string.IsNullOrWhiteSpace(_keyLink) ? Visibility.Visible : Visibility.Collapsed;

        public ICommand GetKeyCommand => new RelayCommand(() =>
        {
            if (!string.IsNullOrWhiteSpace(_keyLink))
                GlobalViewModel.OpenWebpageCommand.Execute(_keyLink);
        });

        // ---- actions -------------------------------------------------------------------

        public ICommand CopyCommand => new RelayCommand(() => Copy(Row.Script, Row.Title));

        public ICommand OpenPageCommand => new RelayCommand(() =>
        {
            if (!string.IsNullOrWhiteSpace(Row.ScriptPageUrl))
                GlobalViewModel.OpenWebpageCommand.Execute(Row.ScriptPageUrl);
        });

        public ICommand CloseCommand => new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));

        internal void Copy(string script, string label)
        {
            if (string.IsNullOrWhiteSpace(script))
            {
                StatusText = "There's no loadstring to copy for this one.";
                return;
            }

            StatusText = Utilities.TrySetClipboardText(script, LOG_IDENT)
                ? $"Copied \"{label}\" to your clipboard."
                : "Couldn't copy — something else is holding the clipboard. Try again.";
        }

        // ---- formatting ----------------------------------------------------------------

        private static string BuildMeta(RobloxScript script)
        {
            var parts = new List<string>();

            string game = script.GameInfo?.Name ?? "";
            if (string.IsNullOrWhiteSpace(game) && script.Games is { Count: > 0 })
            {
                // "Game 4954752502" is an unresolved place id, not a name — see RobloxScript.Games.
                string first = script.Games[0];
                if (!first.StartsWith("Game ", StringComparison.Ordinal))
                    game = first;
            }

            if (script.IsUniversal)
                parts.Add("Universal");
            else if (!string.IsNullOrWhiteSpace(game))
                parts.Add(game);

            if (script.IsHub)
                parts.Add("Hub");

            if (script.Price is > 0 and var price)
                parts.Add($"costs {price:0.##}");

            if (script.Views > 0)
                parts.Add($"{script.Views:N0} views");

            if (script.Likes > 0)
                parts.Add($"{script.Likes:N0} likes");

            if (script.UpdatedAt is { } updated)
                parts.Add($"updated {updated.ToLocalTime():d MMM yyyy}");

            return string.Join(" · ", parts);
        }

        /// <summary>Every game this script claims to support, each as an openable link.</summary>
        private static List<GameLinkRow> BuildGameLinks(RobloxScript script)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<GameLinkRow>();

            // The richer game object first when we have it — it's the one the API actually resolved.
            if (!string.IsNullOrWhiteSpace(script.GameInfo?.Name))
            {
                seen.Add(script.GameInfo!.Name);
                result.Add(MakeGameLink(script.GameInfo.Name)!);
            }

            foreach (string raw in script.Games ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(raw) || !seen.Add(raw.Trim()))
                    continue;

                var link = MakeGameLink(raw);
                if (link is not null)
                    result.Add(link);
            }

            return result;
        }

        private static GameLinkRow? MakeGameLink(string raw)
        {
            string name = raw.Trim();
            if (name.Length == 0)
                return null;

            // "Game 4954752502" is an entry the site never resolved to a name. The id is worth more
            // than the label — it opens the actual place — so link straight to it.
            var placeId = System.Text.RegularExpressions.Regex.Match(name, @"^Game (\d{4,})$");
            if (placeId.Success)
                return new GameLinkRow($"Place {placeId.Groups[1].Value}", $"https://www.roblox.com/games/{placeId.Groups[1].Value}");

            // Named games carry no place id anywhere in the API (there is no games endpoint), so
            // the honest link is a Roblox search for the name.
            return new GameLinkRow(name, "https://www.roblox.com/discover?Keyword=" + Uri.EscapeDataString(name));
        }

        private static List<string> Tidy(List<string>? values, bool capitalise = false)
        {
            if (values is null)
                return new List<string>();

            // Tags arrive slug-cased ("other-stupid-stuff"); features arrive already readable.
            // Either way, de-duplicate and drop blanks before they reach the UI.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();

            foreach (string raw in values)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                string value = raw.Replace('-', ' ').Replace('_', ' ').Trim();

                // Features arrive lowercase ("autofarms"); they're headline content here, so give
                // them a capital. Tags stay as-is — they read as metadata, not prose.
                if (capitalise && value.Length > 0 && char.IsLower(value[0]))
                    value = char.ToUpperInvariant(value[0]) + value[1..];

                if (value.Length > 0 && seen.Add(value))
                    result.Add(value);
            }

            return result;
        }

        /// <summary>
        /// Descriptions are markdown. We deliberately do NOT use MarkdownTextBlock here — it drops
        /// headings, lists and blockquotes and truncates nested inlines, which mangles exactly the
        /// kind of feature list these descriptions are made of. Strip the syntax and keep the lines.
        /// </summary>
        private static string CleanDescription(string? markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown))
                return "";

            var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var output = new List<string>();

            foreach (string raw in lines)
            {
                string line = raw.Trim();

                line = line.TrimStart('#', '>', ' ').Trim();

                // Bullets read better than the raw marker, and markdown uses several.
                if (line.StartsWith("- ", StringComparison.Ordinal) ||
                    line.StartsWith("* ", StringComparison.Ordinal) ||
                    line.StartsWith("+ ", StringComparison.Ordinal))
                    line = "• " + line[2..].Trim();

                line = line.Replace("**", "").Replace("__", "").Replace("`", "");

                // Collapse runs of blank lines rather than letting them pad the dialog out.
                if (line.Length == 0 && (output.Count == 0 || output[^1].Length == 0))
                    continue;

                output.Add(line);
            }

            while (output.Count > 0 && output[^1].Length == 0)
                output.RemoveAt(output.Count - 1);

            return string.Join(Environment.NewLine, output);
        }
    }

    /// <summary>A game a script supports, linked to somewhere the user can actually open it.</summary>
    public class GameLinkRow
    {
        public string Name { get; }
        public string Url { get; }

        public GameLinkRow(string name, string url)
        {
            Name = name;
            Url = url;
        }

        public ICommand OpenCommand => new RelayCommand(() => GlobalViewModel.OpenWebpageCommand.Execute(Url));
    }

    /// <summary>One alternative build of a script, each with its own loadstring.</summary>
    public class ScriptVariantRow
    {
        private readonly ScriptDetailsViewModel _owner;

        public string Name { get; }
        public string Description { get; }
        public string Script { get; }

        public Visibility DescriptionVisibility =>
            string.IsNullOrWhiteSpace(Description) ? Visibility.Collapsed : Visibility.Visible;

        public ScriptVariantRow(RobloxScriptVariant variant, ScriptDetailsViewModel owner)
        {
            _owner = owner;
            Name = string.IsNullOrWhiteSpace(variant.Name) ? "Unnamed version" : variant.Name;
            Description = variant.Description ?? "";
            Script = variant.Script ?? "";
        }

        public ICommand CopyCommand => new RelayCommand(() => _owner.Copy(Script, Name));
    }
}
