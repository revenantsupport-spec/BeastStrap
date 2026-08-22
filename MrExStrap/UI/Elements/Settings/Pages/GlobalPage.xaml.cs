using System.Windows.Input;

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

        // FishStrap parity: D:\FishStrap\Bloxstrap\UI\Elements\Settings\Pages\GlobalSettingsPage.xaml.cs:29
        private void ValidateUInt32(object sender, TextCompositionEventArgs e) => e.Handled = !UInt32.TryParse(e.Text, out uint _);
    }
}
