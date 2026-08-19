using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

using BeastStrap.Models.Persistable;
using BeastStrap.UI.ViewModels.Settings;

namespace BeastStrap.UI.ViewModels.Dialogs
{
    // Compact picker shown right before a Roblox launch when the user has
    // ShowVersionPickerOnLaunch turned on. Same tile renderer as the Versions
    // Manager tab (VersionProfileTile, with WEAO logo fetch + letter fallback)
    // so the selection UI matches what the user already configured upstream.
    public class VersionPickerViewModel : NotifyPropertyChangedViewModel
    {
        // Caller subscribes to find out what the user picked. profile == null means
        // "Cancel button or window closed without a Launch click" — caller aborts
        // the entire launch in that case.
        public event EventHandler<VersionProfile?>? CloseRequested;

        public ObservableCollection<VersionProfileTile> Tiles { get; } = new();

        private VersionProfileTile? _selectedTile;
        public VersionProfileTile? SelectedTile
        {
            get => _selectedTile;
            set
            {
                if (_selectedTile != null)
                    _selectedTile.IsActive = false;
                _selectedTile = value;
                if (_selectedTile != null)
                    _selectedTile.IsActive = true;
                OnPropertyChanged(nameof(SelectedTile));
                OnPropertyChanged(nameof(CanLaunch));
            }
        }

        public bool CanLaunch => _selectedTile != null;

        public ICommand SelectCommand => new RelayCommand<string>(SelectById);
        public ICommand LaunchCommand => new RelayCommand(Launch);
        public ICommand CancelCommand => new RelayCommand(Cancel);

        public VersionPickerViewModel()
        {
            string activeId = App.Settings.Prop.ActiveVersionProfileId;
            foreach (var profile in App.Settings.Prop.VersionProfiles)
            {
                var tile = new VersionProfileTile(profile, profile.Id == activeId);
                Tiles.Add(tile);
                _ = tile.LoadLogoAsync();
                if (profile.Id == activeId)
                    _selectedTile = tile;
            }
            OnPropertyChanged(nameof(CanLaunch));
        }

        private void SelectById(string? id)
        {
            if (string.IsNullOrEmpty(id)) return;
            var tile = Tiles.FirstOrDefault(t => t.Id == id);
            if (tile != null)
                SelectedTile = tile;
        }

        private void Launch()
        {
            if (_selectedTile == null) return;
            var profile = App.Settings.Prop.VersionProfiles.FirstOrDefault(p => p.Id == _selectedTile.Id);
            CloseRequested?.Invoke(this, profile);
        }

        private void Cancel()
        {
            CloseRequested?.Invoke(this, null);
        }
    }
}
