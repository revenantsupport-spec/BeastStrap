using BeastStrap.UI.ViewModels.Settings;

namespace BeastStrap.UI.Elements.Settings.Pages
{
    public partial class VipServerPage
    {
        public VipServerPage()
        {
            DataContext = new VipServerViewModel();
            InitializeComponent();
        }
    }
}
