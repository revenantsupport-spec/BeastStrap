using BeastStrap.UI.ViewModels.Settings;

namespace BeastStrap.UI.Elements.Settings.Pages
{
    public partial class ObfuscatorPage
    {
        public ObfuscatorPage()
        {
            DataContext = new ObfuscatorViewModel();
            InitializeComponent();
        }
    }
}
