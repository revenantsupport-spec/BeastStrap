using System.Windows;
using System.Windows.Input;

using BeastStrap.UI.Elements.Dialogs;
using BeastStrap.UI.ViewModels.Settings;

namespace BeastStrap.UI.Elements.Settings.Pages
{
    public partial class ScriptsPage
    {
        public ScriptsPage()
        {
            // DataContext before InitializeComponent, same as every other page here.
            DataContext = new ScriptsViewModel();
            InitializeComponent();
        }

        // Clicking anywhere on a row that isn't one of its buttons opens the full record. The
        // buttons don't need excluding by hand: ButtonBase marks both the down and up events
        // handled, so a click on Copy or the star never bubbles this far.
        //
        // Lives in code-behind rather than the view model because the dialog needs an Owner, and
        // that's the pattern the other pages use (see FastFlagsPage.ShowFlagSearch).
        private void ScriptRow_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: ScriptRow row })
                return;

            new ScriptDetailsDialog(row) { Owner = Window.GetWindow(this) }.ShowDialog();
        }
    }
}
