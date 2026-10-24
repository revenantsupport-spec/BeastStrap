using BeastStrap.UI.ViewModels.Settings;

namespace BeastStrap.UI.Elements.Settings.Pages
{
    public partial class AppearancePage
    {
        public AppearancePage()
        {
            var viewModel = new AppearanceViewModel(this);
            DataContext = viewModel;
            InitializeComponent();

            // This page is cached for the window's lifetime, so the theme can change under it (a seasonal run starting
            // or ending at midnight, a dialog that noticed first). It follows ThemeManager while it's on screen and
            // catches up whenever it's shown again. Unhooked when it isn't, so the static event never outlives it.
            Loaded += (_, _) => viewModel.StartFollowingTheme();
            Unloaded += (_, _) => viewModel.StopFollowingTheme();

            // Being here is what the Home page's "New: themes" callout asks for, so it can stop asking.
            Loaded += (_, _) => BeastStrap.UI.ViewModels.HomeViewModel.MarkThemesNoticeSeen();
        }

        // The notice's button flips between "Turn it off" and "Turn it back on" in the same spot, so the second press
        // of a double-click would undo the first. Handling the double-click marks that press handled before the
        // button sees it, so only the first click counts.
        private void SeasonalButton_PreviewMouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => e.Handled = true;
    }
}
