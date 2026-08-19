using BeastStrap.UI.ViewModels.Settings;

namespace BeastStrap.UI.Elements.Settings.Pages
{
    public partial class VersionsManagerPage
    {
        public VersionsManagerPage()
        {
            DataContext = new VersionsManagerViewModel();
            InitializeComponent();
        }
    }
}
