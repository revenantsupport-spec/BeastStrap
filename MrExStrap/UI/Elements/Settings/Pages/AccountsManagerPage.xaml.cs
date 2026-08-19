using System.Windows;

using BeastStrap.UI.ViewModels.Settings;

namespace BeastStrap.UI.Elements.Settings.Pages
{
    /// <summary>
    /// Interaction logic for AccountsManagerPage.xaml
    /// </summary>
    public partial class AccountsManagerPage
    {
        public AccountsManagerPage()
        {
            DataContext = new AccountsManagerViewModel();
            InitializeComponent();
        }

        // The navigation caches this page, so the view-model is built once and its account list
        // would otherwise go stale. Reload every time the tab is shown so accounts saved from
        // other tabs (e.g. the Multi Instance tab or the Alt Generator) show up without an app
        // restart.
        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is AccountsManagerViewModel vm)
                vm.RefreshOnShow();
        }
    }
}