using BeastStrap.UI.ViewModels.Settings;

namespace BeastStrap.UI.Elements.Settings.Pages
{
    public partial class ChangelogPage
    {
        public ChangelogPage()
        {
            DataContext = new ChangelogViewModel();
            InitializeComponent();
        }
    }
}
