using System.Windows;
using System.Windows.Input;
using BeastStrap.UI.Elements.About;
using CommunityToolkit.Mvvm.Input;

namespace BeastStrap.UI.ViewModels.Settings
{
    public class MainWindowViewModel : NotifyPropertyChangedViewModel
    {
        public ICommand OpenAboutCommand => new RelayCommand(OpenAbout);

        public ICommand SaveSettingsCommand => new RelayCommand(SaveSettings);

        public ICommand SaveAndLaunchCommand => new RelayCommand(SaveAndLaunch);

        public ICommand CloseWindowCommand => new RelayCommand(CloseWindow);

        public EventHandler? RequestSaveNoticeEvent;

        public EventHandler? RequestCloseWindowEvent;

        public EventHandler? RequestLaunchAndCloseEvent;

        public bool TestModeEnabled
        {
            get => App.LaunchSettings.TestModeFlag.Active;
            set
            {
                if (value)
                {
                    var result = Frontend.ShowMessageBox(Strings.Menu_TestMode_Prompt, MessageBoxImage.Information, MessageBoxButton.YesNo);

                    if (result != MessageBoxResult.Yes)
                        return;
                }

                App.LaunchSettings.TestModeFlag.Active = value;
            }
        }

        public bool SimpleMode
        {
            get => App.Settings.Prop.SimpleMode;
            set
            {
                App.Settings.Prop.SimpleMode = value;
                App.Settings.Save();
                OnPropertyChanged(nameof(SimpleMode));
                OnPropertyChanged(nameof(IsAdvancedMode));
            }
        }

        public bool IsAdvancedMode => !SimpleMode;

        private void OpenAbout() => new MainWindow().ShowDialog();

        private void CloseWindow() => RequestCloseWindowEvent?.Invoke(this, EventArgs.Empty);

        private void SaveSettings()
        {
            const string LOG_IDENT = "MainWindowViewModel::SaveSettings";

            App.Settings.Save();
            App.State.Save();
            App.FastFlags.Save();

            foreach (var pair in App.PendingSettingTasks)
            {
                var task = pair.Value;

                if (task.Changed)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Executing pending task '{task}'");
                    task.Execute();
                }
            }

            App.PendingSettingTasks.Clear();

            // The Global page edits ROBLOX's own settings file rather than ours, so it has its own
            // Apply button further up the page. People press this one instead — it's the big one in
            // the window footer and every other page is saved by it — and their change silently never
            // reached disk. A user set their framerate cap to 69, pressed Save, launched, saw 240, and
            // reasonably concluded the feature was broken. If that page has pending edits, flush them.
            if (GlobalBasicSettings.Loaded && GlobalBasicSettings.Dirty)
            {
                if (GlobalBasicSettings.Save())
                    App.Logger.WriteLine(LOG_IDENT, "Flushed pending Roblox settings changes from the Global page");
                else
                    Frontend.ShowMessageBox(
                        "Couldn't save your changes to Roblox's own settings file. Everything else was saved. See the log for details.",
                        MessageBoxImage.Error);
            }

            RequestSaveNoticeEvent?.Invoke(this, EventArgs.Empty);
        }

        private void SaveAndLaunch()
        {
            // Roblox rewrites its settings file when it STARTS, so unlocked Global-page changes are
            // gone before the user even reaches the menu. Saving and launching would look like it
            // worked and change nothing, so say so while they can still do something about it.
            // Checked before SaveSettings, because that clears the dirty flag.
            if (GlobalBasicSettings.Loaded && GlobalBasicSettings.Dirty && !GlobalBasicSettings.IsLocked)
            {
                var result = Frontend.ShowMessageBox(
                    "Your Global page changes won't survive this launch.\n\n" +
                    "Roblox rewrites its own settings file when it starts, so anything that isn't locked goes " +
                    "straight back to what Roblox had. Turn on \"Lock the file\" on the Global page first if you " +
                    "want them to stick.\n\n" +
                    "Launch anyway?",
                    MessageBoxImage.Warning, MessageBoxButton.YesNo, MessageBoxResult.No);

                if (result != MessageBoxResult.Yes)
                    return;
            }

            SaveSettings();
            RequestLaunchAndCloseEvent?.Invoke(this, EventArgs.Empty);
        }
    }
}
