using BeastStrap.UI.ViewModels.Settings;

namespace BeastStrap.UI.Elements.Settings.Pages
{
    public partial class GlobalPage
    {
        public GlobalPage()
        {
            DataContext = new GlobalSettingsViewModel();
            InitializeComponent();
        }
    }
}
