using System.Windows;

using BeastStrap.UI.ViewModels.Settings;

namespace BeastStrap.UI.Elements.Settings.Pages
{
    /// <summary>
    /// Interaction logic for MultiInstancePage.xaml
    /// </summary>
    public partial class MultiInstancePage
    {
        public MultiInstancePage()
        {
            DataContext = new MultiInstanceViewModel();
            InitializeComponent();
        }

        // The navigation caches this page, so the view-model is built once and its account list
        // would otherwise go stale. Reload every time the tab is shown so accounts saved from
        // other tabs (e.g. the Alt Generator's "Save to Multi Instance") show up without an app
        // restart, and the running-instances list reflects what's open right now.
        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is MultiInstanceViewModel vm)
                vm.RefreshOnShow();
        }
    }
}
