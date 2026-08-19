using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

using BeastStrap.UI.Utility;
using BeastStrap.UI.ViewModels.Settings;

namespace BeastStrap.UI.Elements.Settings.Pages
{
    /// <summary>
    /// Interaction logic for BeastStrapPage.xaml
    /// </summary>
    public partial class BeastStrapPage
    {
        public BeastStrapPage()
        {
            DataContext = new BeastStrapViewModel();
            InitializeComponent();
        }

        private void ComboBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
            => ComboBoxScrollFix.HandleWheel(sender, e);
    }
}
