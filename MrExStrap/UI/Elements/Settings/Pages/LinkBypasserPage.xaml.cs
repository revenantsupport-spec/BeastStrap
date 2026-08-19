using BeastStrap.UI.ViewModels.Settings;

namespace BeastStrap.UI.Elements.Settings.Pages
{
    public partial class LinkBypasserPage
    {
        public LinkBypasserPage()
        {
            DataContext = new LinkBypasserViewModel();
            InitializeComponent();
        }
    }
}
