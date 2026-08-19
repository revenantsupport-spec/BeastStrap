using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

using BeastStrap.Models.Persistable;

namespace BeastStrap.UI.ViewModels.Dialogs
{
    // Shown right before a launch when "join the emptiest server on launch" is on but the launch
    // carried no game — i.e. the user opened BeastStrap or the tray icon instead of pressing Play
    // on a game's page. Without this the setting simply can't apply, which reads as it being broken.
    //
    // The user picks from what they've played before, or pastes a game link / place id. The result
    // is turned into a normal roblox:// deeplink by the caller, so the existing emptiest-server
    // rewrite in the bootstrapper handles it exactly like a launch from the website.
    public class GamePickerViewModel : NotifyPropertyChangedViewModel
    {
        // placeId == null means "just launch normally" — the user skipped, or closed the window.
        public event EventHandler<long?>? CloseRequested;

        // Accepts a bare id, a roblox.com/games/<id> link, or anything else with a long run of
        // digits in it. Deliberately loose: users paste all sorts of things.
        private static readonly Regex PlaceIdRegex = new(@"(\d{4,19})", RegexOptions.Compiled);

        public ObservableCollection<RecentPlaceRow> Recents { get; } = new();

        public bool HasRecents => Recents.Count > 0;
        public bool HasNoRecents => Recents.Count == 0;

        private RecentPlaceRow? _selected;
        public RecentPlaceRow? Selected
        {
            get => _selected;
            set
            {
                if (_selected != null)
                    _selected.IsActive = false;
                _selected = value;
                if (_selected != null)
                    _selected.IsActive = true;
                OnPropertyChanged(nameof(Selected));
                OnPropertyChanged(nameof(CanConfirm));
            }
        }

        private string _placeIdInput = "";
        public string PlaceIdInput
        {
            get => _placeIdInput;
            set
            {
                _placeIdInput = value;
                // Typing a link is an implicit deselect — otherwise it's ambiguous which one wins.
                if (!string.IsNullOrWhiteSpace(value) && _selected != null)
                    Selected = null;
                OnPropertyChanged(nameof(PlaceIdInput));
                OnPropertyChanged(nameof(CanConfirm));
            }
        }

        public bool CanConfirm => ResolvePlaceId() > 0;

        public ICommand SelectCommand => new RelayCommand<string>(SelectById);
        public ICommand ConfirmCommand => new RelayCommand(Confirm);
        public ICommand SkipCommand => new RelayCommand(Skip);

        public GamePickerViewModel()
        {
            foreach (var place in App.State.Prop.RecentPlaces)
                Recents.Add(new RecentPlaceRow(place));

            // Pre-select the most recent so the common case is one click.
            if (Recents.Count > 0)
                Selected = Recents[0];

            OnPropertyChanged(nameof(HasRecents));
            OnPropertyChanged(nameof(HasNoRecents));
        }

        // The pasted box wins when it holds something usable, otherwise the selected tile.
        private long ResolvePlaceId()
        {
            if (!string.IsNullOrWhiteSpace(_placeIdInput))
            {
                var match = PlaceIdRegex.Match(_placeIdInput);
                if (match.Success && long.TryParse(match.Groups[1].Value, out long parsed))
                    return parsed;

                return 0;
            }

            return _selected?.PlaceId ?? 0;
        }

        private void SelectById(string? placeId)
        {
            if (string.IsNullOrEmpty(placeId)) return;

            var row = Recents.FirstOrDefault(r => r.PlaceId.ToString() == placeId);
            if (row != null)
            {
                PlaceIdInput = "";
                Selected = row;
            }
        }

        private void Confirm()
        {
            long placeId = ResolvePlaceId();
            if (placeId <= 0) return;
            CloseRequested?.Invoke(this, placeId);
        }

        private void Skip() => CloseRequested?.Invoke(this, null);
    }

    // One row in the recents list.
    public class RecentPlaceRow : NotifyPropertyChangedViewModel
    {
        public long PlaceId { get; }
        public string Name { get; }
        public string LastPlayed { get; }

        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            set { _isActive = value; OnPropertyChanged(nameof(IsActive)); }
        }

        public RecentPlaceRow(RecentPlace place)
        {
            PlaceId = place.PlaceId;
            Name = place.DisplayLabel;
            LastPlayed = FormatAge(place.LastPlayedUtc);
        }

        private static string FormatAge(DateTime lastPlayedUtc)
        {
            var age = DateTime.UtcNow - lastPlayedUtc;

            if (age.TotalMinutes < 1) return "just now";
            if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m ago";
            if (age.TotalDays < 1) return $"{(int)age.TotalHours}h ago";
            if (age.TotalDays < 30) return $"{(int)age.TotalDays}d ago";

            return lastPlayedUtc.ToLocalTime().ToString("d");
        }
    }
}
