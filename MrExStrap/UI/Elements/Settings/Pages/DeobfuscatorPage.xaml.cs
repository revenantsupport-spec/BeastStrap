using BeastStrap.UI.ViewModels.Settings;

namespace BeastStrap.UI.Elements.Settings.Pages
{
    public partial class DeobfuscatorPage
    {
        public DeobfuscatorPage()
        {
            DataContext = new DeobfuscatorViewModel();
            InitializeComponent();
        }
    }
}
