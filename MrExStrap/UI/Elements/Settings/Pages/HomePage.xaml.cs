using System.Windows;

using BeastStrap.UI.ViewModels;
using BeastStrap.UI.ViewModels.Settings;

namespace BeastStrap.UI.Elements.Settings.Pages
{
    public partial class HomePage
    {
        public HomePage()
        {
            DataContext = new HomeViewModel();
            InitializeComponent();
        }

        // The dashboard's "Save and launch Roblox" CTA. Reuses the settings window's
        // SaveAndLaunchCommand. Wired in code-behind because the RelativeSource
        // ancestor binding doesn't resolve reliably when the page lives inside the
        // WPF-UI navigation frame — the button appeared to do nothing.
        private void SaveAndLaunchButton_Click(object sender, RoutedEventArgs e)
        {
            var window = Window.GetWindow(this);
            if (window?.DataContext is MainWindowViewModel viewModel)
                viewModel.SaveAndLaunchCommand.Execute(null);
        }

        // Opens the same executor board the Versions Manager toolbar uses.
        private void ExecutorCheckerButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new BeastStrap.UI.Elements.Dialogs.ExecutorCheckerDialog
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            dialog.ShowDialog();
        }
    }
}