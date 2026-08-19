using System.Windows.Input;

using BeastStrap.UI.Utility;
using BeastStrap.UI.ViewModels.Settings;

namespace BeastStrap.UI.Elements.Settings.Pages
{
    public partial class VersionPage
    {
        public VersionPage()
        {
            DataContext = new VersionViewModel();
            InitializeComponent();
        }

        private void ComboBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
            => ComboBoxScrollFix.HandleWheel(sender, e);
    }
}
