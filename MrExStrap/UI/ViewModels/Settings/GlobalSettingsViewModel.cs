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
        // FishStrap parity: FramerateCap uses DFIntTaskSchedulerTargetFps semantics where
        // -1 is the engine default (shown as 60 in the UI). GlobalBasicSettings stores the
        // <int name="FramerateCap"> value; FishStrap maps <1 -> -1 on save and <1 -> 60 on load.
        // See D:\FishStrap\Bloxstrap\UI\ViewModels\Settings\GlobalSettingsViewModel.cs:14-35
        public int FramerateCap
        {
            get
            {
                // FishStrap default is 60 when parsing fails; original BeastStrap used 240.
                // Match FishStrap: if stored <1, show 60.
                string? raw = GBS.GetValue(GBS.FramerateCap);
                if (int.TryParse(raw, out int framerate))
                {
                    if (framerate < 1)
                        return 60;
                    return framerate;
                }
                return 60;
            }
            set
            {
                if (value < 1)
                    value = -1;

                GBS.SetValue(GBS.FramerateCap, value);
                OnPropertyChanged(nameof(FramerateCap));
            }
        }

        // FishStrap parity: GraphicsQuality is a 1-10 slider bound to SavedQualityLevel token.
        // See D:\FishStrap\Bloxstrap\UI\ViewModels\Settings\GlobalSettingsViewModel.cs:49-57
        // and D:\FishStrap\Bloxstrap\UI\Elements\Settings\Pages\GlobalSettingsPage.xaml:27-38
        // FishStrap stores the raw string token directly; we store the int value as token text.
        public int GraphicsQuality
        {
            get
            {
                string? raw = GBS.GetValue(GBS.QualityLevel);
                if (int.TryParse(raw, out int lvl) && lvl >= 1 && lvl <= 10)
                    return lvl;

                // FishStrap template defaults to 2; BeastStrap used 0=Automatic. If file has
                // Automatic (0) or missing, show 2 to match FishStrap's fresh-install default.
                // Slider is 1-10, so clamp out-of-range to 2.
                return 2;
            }
            set
            {
                // Clamp to slider range; FishStrap tick is 1, min 1 max 10.
                if (value < 1) value = 1;
                if (value > 10) value = 10;

                GBS.SetValue(GBS.QualityLevel, value);
                OnPropertyChanged(nameof(GraphicsQuality));
            }
        }

        // Kept for backwards compatibility if any XAML still references the old dropdown.
        // Redirects to the slider property.
        public IEnumerable<string> QualityLevels { get; } =
            new[] { "Automatic" }.Concat(Enumerable.Range(1, 10).Select(i => i.ToString())).ToArray();

        public string SelectedQualityLevel
        {
            get => GraphicsQuality.ToString();
            set
            {
                if (int.TryParse(value, out int v) && v >= 1 && v <= 10)
                    GraphicsQuality = v;
                else if (value == "Automatic")
                    GBS.SetValue(GBS.QualityLevel, 0);

                OnPropertyChanged(nameof(SelectedQualityLevel));
                OnPropertyChanged(nameof(GraphicsQuality));
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
                nameof(PerformanceStats), nameof(FramerateCap), nameof(GraphicsQuality), nameof(SelectedQualityLevel),
                nameof(Fullscreen), nameof(StartMaximized), nameof(MasterVolume), nameof(MouseSensitivity)
            })
            {
                OnPropertyChanged(name);
            }
        }
    }
}
