using BeastStrap.UI.ViewModels.Settings;

namespace BeastStrap.UI.Elements.Settings.Pages
{
    public partial class NewsPage
    {
        public NewsPage()
        {
            DataContext = new NewsViewModel();
            InitializeComponent();
        }
    }
}
