using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

using Wpf.Ui.Controls.Interfaces;
using Wpf.Ui.Mvvm.Contracts;

using BeastStrap.UI.ViewModels.Settings;

namespace BeastStrap.UI.Elements.Settings
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : INavigationWindow
    {
        private Models.Persistable.WindowState _state => App.State.Prop.SettingsWindow;

        private bool _launchOnClose;

        // Settings as they were once the window had finished setting itself up. Taken after
        // LoadState() rather than before, so any normalising a page's view model does while
        // initialising counts as "not a user edit" and doesn't trigger the prompt on close.
        private readonly string _settingsBaseline;

        public MainWindow(bool showAlreadyRunningWarning)
        {
            var viewModel = new MainWindowViewModel();

            viewModel.RequestSaveNoticeEvent += (_, _) => SettingsSavedSnackbar.Show();
            viewModel.RequestCloseWindowEvent += (_, _) => Close();
            viewModel.RequestLaunchAndCloseEvent += (_, _) => { _launchOnClose = true; Close(); };

            DataContext = viewModel;
            
            InitializeComponent();

            // Show the app version in the window + taskbar title, e.g. "BeastStrap - Settings - v420.35".
            Title = RootTitleBar.Title = $"BeastStrap - Settings - v{App.Version}";

            App.Logger.WriteLine("MainWindow", "Initializing settings window");

            if (showAlreadyRunningWarning)
                ShowAlreadyRunningSnackbar();

            LoadState();

            _settingsBaseline = App.Settings.ComputeCurrentHash();
        }

        public void LoadState()
        {
            if (_state.Left > SystemParameters.VirtualScreenWidth)
                _state.Left = 0;

            if (_state.Top > SystemParameters.VirtualScreenHeight)
                _state.Top = 0;

            if (_state.Width > 0)
                this.Width = _state.Width;

            if (_state.Height > 0)
                this.Height = _state.Height;

            if (_state.Left > 0 && _state.Top > 0)
            {
                this.WindowStartupLocation = WindowStartupLocation.Manual;
                this.Left = _state.Left;
                this.Top = _state.Top;
            }
        }

        private async void ShowAlreadyRunningSnackbar()
        {
            await Task.Delay(500); // wait for everything to finish loading
            AlreadyRunningSnackbar.Show();
        }

        #region INavigationWindow methods

        public Frame GetFrame() => RootFrame;

        public INavigation GetNavigation() => RootNavigation;

        public bool Navigate(Type pageType) => RootNavigation.Navigate(pageType);

        public void SetPageService(IPageService pageService) => RootNavigation.PageService = pageService;

        public void ShowWindow() => Show();

        public void CloseWindow() => Close();

        #endregion INavigationWindow methods

        private void WpfUiWindow_Closing(object sender, CancelEventArgs e)
        {
            // Settings pages bind straight to App.Settings.Prop and rely on the Save button, so
            // closing with the X used to throw away every change on every page without a word.
            // Multi Instance was the worst of it: its toggles are next to a working "Launch all"
            // button, so the page behaves like a tool and nothing hinted the state was temporary.
            //
            // Settings count as safe to discard if they match EITHER what we started with (no
            // edits) or what is on disk (edited, then saved — LastFileHash moves on every write,
            // including the save behind "Save and launch Roblox", which closes this window).
            string current = App.Settings.ComputeCurrentHash();
            bool settingsEdited = current != _settingsBaseline
                               && current != (App.Settings.LastFileHash ?? current);

            if (App.FastFlags.Changed || App.PendingSettingTasks.Any() || settingsEdited)
            {
                var result = Frontend.ShowMessageBox(Strings.Menu_UnsavedChanges, MessageBoxImage.Warning, MessageBoxButton.YesNo);

                if (result != MessageBoxResult.Yes)
                    e.Cancel = true;
            }
            
            _state.Width = this.Width;
            _state.Height = this.Height;

            _state.Top = this.Top;
            _state.Left = this.Left;

            App.State.Save();
        }

        private void WpfUiWindow_Closed(object sender, EventArgs e)
        {
            if (_launchOnClose || App.LaunchSettings.TestModeFlag.Active)
                LaunchHandler.LaunchRoblox(LaunchMode.Player);
            else
                App.SoftTerminate();
        }
    }
}
