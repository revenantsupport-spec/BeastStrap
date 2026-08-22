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
            SetupCollapsibleHeaders();

            // QoL: restore last visited page (FrostStrap parity) + track recent
            try
            {
                string last = App.State.Prop.LastVisitedPage;
                if (!string.IsNullOrWhiteSpace(last) && last != "HomePage")
                {
                    var type = AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => a.GetTypes()).FirstOrDefault(t => t.Name == last);
                    if (type != null)
                    {
                        Dispatcher.BeginInvoke(() => { try { RootNavigation.Navigate(type); } catch { } }, System.Windows.Threading.DispatcherPriority.Loaded);
                    }
                }
            }
            catch { }

            RootFrame.Navigated += (s, e) =>
            {
                try
                {
                    string name = e.Content?.GetType().Name ?? "";
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        App.State.Prop.LastVisitedPage = name;
                        App.State.Save();
                    }
                }
                catch { }
            };

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

        private void NavSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            string q = NavSearchBox.Text?.Trim().ToLowerInvariant() ?? "";
            bool hasQuery = !string.IsNullOrWhiteSpace(q);

            // When empty, restore collapsed state so headers that were collapsed stay collapsed
            if (!hasQuery)
            {
                foreach (var obj in RootNavigation.Items)
                {
                    if (obj is System.Windows.UIElement el) el.Visibility = System.Windows.Visibility.Visible;
                }
                // Re-apply saved collapsed sections
                foreach (var obj in RootNavigation.Items.OfType<Wpf.Ui.Controls.Navigation.NavigationHeader>())
                {
                    string key = obj.Text ?? "";
                    if (App.State.Prop.CollapsedSidebarSections.Contains(key))
                        SetSectionCollapsed(obj, true, false);
                }
                return;
            }

            // Searching expands everything first, then filters
            foreach (var obj in RootNavigation.Items.OfType<Wpf.Ui.Controls.Navigation.NavigationHeader>())
                obj.Visibility = System.Windows.Visibility.Visible;
            foreach (var obj in RootNavigation.Items)
            {
                if (obj is System.Windows.UIElement el) el.Visibility = System.Windows.Visibility.Visible;
            }

            // Filter NavigationItems only — headers stay visible so structure isn't lost.
            foreach (var obj in RootNavigation.Items)
            {
                if (obj is Wpf.Ui.Controls.NavigationItem nav)
                {
                    string title = nav.Content?.ToString()?.ToLowerInvariant() ?? "";
                    string tag = nav.Tag?.ToString()?.ToLowerInvariant() ?? "";
                    string page = nav.PageType?.Name.ToLowerInvariant() ?? "";

                    bool match = title.Contains(q) || tag.Contains(q) || page.Contains(q);
                    if (!match && title.Contains("engine") && (q.Contains("fps") || q.Contains("flag") || q.Contains("texture") || q.Contains("msaa"))) match = true;
                    if (!match && title == "global" && (q.Contains("graphics") || q.Contains("framerate") || q.Contains("fps") || q.Contains("volume"))) match = true;
                    if (!match && title == "appearance" && (q.Contains("bootstrapper") || q.Contains("terminal") || q.Contains("premium") || q.Contains("wallpaper") || q.Contains("gif"))) match = true;
                    if (!match && title.Contains("appearance") && q.Contains("theme")) match = true;
                    if (!match && title.Contains("versions") && q.Contains("downgrade")) match = true;

                    nav.Visibility = match ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                }
            }
        }

        private void SetupCollapsibleHeaders()
        {
            try
            {
                foreach (var hdr in RootNavigation.Items.OfType<Wpf.Ui.Controls.Navigation.NavigationHeader>())
                {
                    string key = hdr.Text ?? "";
                    // Restore collapsed state
                    if (App.State.Prop.CollapsedSidebarSections.Contains(key))
                        SetSectionCollapsed(hdr, true, false);

                    // Make header look interactive
                    hdr.Cursor = System.Windows.Input.Cursors.Hand;
                    hdr.ToolTip = "Click to collapse/expand • " + key;
                    // Attach click handler (use PreviewMouseLeftButtonUp to avoid nav selection)
                    hdr.PreviewMouseLeftButtonUp += (s, e) =>
                    {
                        string k = hdr.Text ?? "";
                        bool currentlyCollapsed = App.State.Prop.CollapsedSidebarSections.Contains(k);
                        SetSectionCollapsed(hdr, !currentlyCollapsed, true);
                        e.Handled = true;
                    };
                }
            }
            catch (Exception ex) { App.Logger.WriteException("MainWindow::SetupCollapsibleHeaders", ex); }
        }

        private void SetSectionCollapsed(Wpf.Ui.Controls.Navigation.NavigationHeader header, bool collapsed, bool save)
        {
            try
            {
                string key = header.Text ?? "";
                var items = RootNavigation.Items.Cast<object>().ToList();
                int idx = items.IndexOf(header);
                if (idx < 0) return;

                for (int i = idx + 1; i < items.Count; i++)
                {
                    var obj = items[i];
                    if (obj is Wpf.Ui.Controls.Navigation.NavigationHeader) break;
                    if (obj is System.Windows.UIElement el)
                        el.Visibility = collapsed ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                }

                header.Opacity = collapsed ? 0.55 : 1.0;

                if (save)
                {
                    if (collapsed)
                    {
                        if (!App.State.Prop.CollapsedSidebarSections.Contains(key))
                            App.State.Prop.CollapsedSidebarSections.Add(key);
                    }
                    else
                    {
                        App.State.Prop.CollapsedSidebarSections.Remove(key);
                    }
                    App.State.Save();
                }
            }
            catch (Exception ex) { App.Logger.WriteException("MainWindow::SetSectionCollapsed", ex); }
        }
    }
}
