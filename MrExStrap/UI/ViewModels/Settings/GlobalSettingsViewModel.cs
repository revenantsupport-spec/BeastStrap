using System.Windows;
using System.Windows.Input;

using CommunityToolkit.Mvvm.Input;

using GBS = BeastStrap.Utility.GlobalBasicSettings;

namespace BeastStrap.UI.ViewModels.Settings
{
    // Backs the Global page, which edits Roblox's own in-game settings file rather than any of ours.
    // Everything reads and writes straight through Utility.GlobalBasicSettings; nothing here is stored
    // in Settings.json, which is why this page has its own Apply button instead of riding the window's
    // Save button.
    public class GlobalSettingsViewModel : NotifyPropertyChangedViewModel
    {
        private const string LOG_IDENT = "GlobalSettingsViewModel";

        public GlobalSettingsViewModel()
        {
            GBS.Load();
        }

        public ICommand ApplyCommand => new RelayCommand(Apply);
        public ICommand ReloadCommand => new RelayCommand(Reload);
        public ICommand ResetCommand => new RelayCommand(Reset);

        // ===== Availability / state =====

        public bool IsAvailable => GBS.Loaded;

        public Visibility MissingFileVisibility => GBS.Exists ? Visibility.Collapsed : Visibility.Visible;

        public Visibility RobloxRunningVisibility => GBS.RobloxRunning ? Visibility.Visible : Visibility.Collapsed;

        public bool HasBackup => GBS.HasBackup;

        public string StatusText
        {
            get
            {
                if (!GBS.Exists)
                    return "Roblox hasn't written a settings file yet. Launch Roblox once, change any in-game setting, then come back.";

                if (!GBS.Loaded)
                    return "The settings file exists but couldn't be read. It may be corrupt — use Reset to restore Roblox's copy.";

                return GBS.IsLocked
                    ? "Locked. Roblox can't overwrite these when it closes."
                    : "Unlocked. Roblox will overwrite these when it next closes — turn on Lock to make them stick.";
            }
        }

        // ===== User interface =====

        public double UiTransparency
        {
            get => GBS.GetFloat(GBS.UiTransparency, 1f);
            set { GBS.SetValue(GBS.UiTransparency, (float)value); OnPropertyChanged(nameof(UiTransparency)); }
        }

        public IEnumerable<string> TextSizes { get; } = new[] { "Small", "Normal", "Large", "Largest" };

        public string SelectedTextSize
        {
            get
            {
                int i = GBS.GetInt(GBS.TextSize, 1);
                var sizes = (string[])TextSizes;
                return i >= 0 && i < sizes.Length ? sizes[i] : "Normal";
            }
            set
            {
                int i = Array.IndexOf((string[])TextSizes, value);
                GBS.SetValue(GBS.TextSize, i < 0 ? 1 : i);
                OnPropertyChanged(nameof(SelectedTextSize));
            }
        }

        public bool ReducedMotion
        {
            get => GBS.GetBool(GBS.ReducedMotion);
            set { GBS.SetValue(GBS.ReducedMotion, value); OnPropertyChanged(nameof(ReducedMotion)); }
        }

        public bool ChatVisible
        {
            get => GBS.GetBool(GBS.ChatVisible, true);
            set { GBS.SetValue(GBS.ChatVisible, value); OnPropertyChanged(nameof(ChatVisible)); }
        }

        public bool PlayerNames
        {
            get => GBS.GetBool(GBS.PlayerNames, true);
            set { GBS.SetValue(GBS.PlayerNames, value); OnPropertyChanged(nameof(PlayerNames)); }
        }

        public bool PlayerList
        {
            get => GBS.GetBool(GBS.PlayerList, true);
            set { GBS.SetValue(GBS.PlayerList, value); OnPropertyChanged(nameof(PlayerList)); }
        }

        public bool BadgeVisible
        {
            get => GBS.GetBool(GBS.BadgeVisible, true);
            set { GBS.SetValue(GBS.BadgeVisible, value); OnPropertyChanged(nameof(BadgeVisible)); }
        }

        public bool PerformanceStats
        {
            get => GBS.GetBool(GBS.PerformanceStats);
            set { GBS.SetValue(GBS.PerformanceStats, value); OnPropertyChanged(nameof(PerformanceStats)); }
        }

        // ===== Graphics and rendering =====

        public string FramerateCap
        {
            get => GBS.GetInt(GBS.FramerateCap, 240).ToString();
            set
            {
                if (int.TryParse(value, out int fps))
                    GBS.SetValue(GBS.FramerateCap, fps);

                OnPropertyChanged(nameof(FramerateCap));
            }
        }

        // Roblox stores 0 as "let the client decide", then 1-10 as manual steps.
        public IEnumerable<string> QualityLevels { get; } =
            new[] { "Automatic" }.Concat(Enumerable.Range(1, 10).Select(i => i.ToString())).ToArray();

        public string SelectedQualityLevel
        {
            get
            {
                int level = GBS.GetInt(GBS.QualityLevel, 0);
                return level <= 0 ? "Automatic" : level.ToString();
            }
            set
            {
                GBS.SetValue(GBS.QualityLevel, value == "Automatic" ? 0 : int.TryParse(value, out int v) ? v : 0);
                OnPropertyChanged(nameof(SelectedQualityLevel));
            }
        }

        public bool Fullscreen
        {
            get => GBS.GetBool(GBS.Fullscreen, true);
            set { GBS.SetValue(GBS.Fullscreen, value); OnPropertyChanged(nameof(Fullscreen)); }
        }

        public bool StartMaximized
        {
            get => GBS.GetBool(GBS.StartMaximized, true);
            set { GBS.SetValue(GBS.StartMaximized, value); OnPropertyChanged(nameof(StartMaximized)); }
        }

        // ===== Audio and input =====

        public double MasterVolume
        {
            get => GBS.GetFloat(GBS.MasterVolume, 0.5f);
            set { GBS.SetValue(GBS.MasterVolume, (float)value); OnPropertyChanged(nameof(MasterVolume)); }
        }

        public double MouseSensitivity
        {
            get => GBS.GetFloat(GBS.MouseSensitivity, 0.36f);
            set { GBS.SetValue(GBS.MouseSensitivity, (float)value); OnPropertyChanged(nameof(MouseSensitivity)); }
        }

        // ===== Lock =====

        public bool Locked
        {
            get => GBS.IsLocked;
            set
            {
                GBS.SetLocked(value);
                OnPropertyChanged(nameof(Locked));
                OnPropertyChanged(nameof(StatusText));
            }
        }

        // ===== Actions =====

        private void Apply()
        {
            if (!GBS.Loaded)
            {
                Frontend.ShowMessageBox(
                    "There's no Roblox settings file to write to yet. Launch Roblox once, change any in-game setting so it gets created, then come back.",
                    MessageBoxImage.Warning);
                return;
            }

            // Writing while a client is open is pointless — it holds its settings in memory and dumps
            // the whole lot back over this file on exit. Say so rather than letting the save look fine
            // and quietly evaporate ten minutes later.
            if (GBS.RobloxRunning)
            {
                var result = Frontend.ShowMessageBox(
                    "Roblox is running. It rewrites this file when it closes, so anything saved now will be lost unless you turn on Lock.\n\nSave anyway?",
                    MessageBoxImage.Warning, MessageBoxButton.YesNo);

                if (result != MessageBoxResult.Yes)
                    return;
            }

            if (GBS.Save())
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(HasBackup));
            }
            else
            {
                Frontend.ShowMessageBox("Couldn't save the Roblox settings file. See the log for details.", MessageBoxImage.Error);
            }
        }

        private void Reload()
        {
            GBS.Load();
            NotifyAll();
        }

        private void Reset()
        {
            if (!GBS.HasBackup)
            {
                Frontend.ShowMessageBox(
                    "Nothing to reset — BeastStrap hasn't changed your Roblox settings, so there's no backup to restore.",
                    MessageBoxImage.Information);
                return;
            }

            var confirm = Frontend.ShowMessageBox(
                "This restores the Roblox settings exactly as they were before BeastStrap first changed them. Continue?",
                MessageBoxImage.Warning, MessageBoxButton.YesNo);

            if (confirm != MessageBoxResult.Yes)
                return;

            if (GBS.Reset())
                NotifyAll();
            else
                Frontend.ShowMessageBox("Couldn't restore the backup. See the log for details.", MessageBoxImage.Error);
        }

        private void NotifyAll()
        {
            foreach (string name in new[]
            {
                nameof(IsAvailable), nameof(MissingFileVisibility), nameof(RobloxRunningVisibility),
                nameof(HasBackup), nameof(StatusText), nameof(Locked),
                nameof(UiTransparency), nameof(SelectedTextSize), nameof(ReducedMotion),
                nameof(ChatVisible), nameof(PlayerNames), nameof(PlayerList), nameof(BadgeVisible),
                nameof(PerformanceStats), nameof(FramerateCap), nameof(SelectedQualityLevel),
                nameof(Fullscreen), nameof(StartMaximized), nameof(MasterVolume), nameof(MouseSensitivity)
            })
            {
                OnPropertyChanged(name);
            }
        }
    }
}
