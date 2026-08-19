using BeastStrap.Integrations;
using BeastStrap.UI.ViewModels.ContextMenu;

namespace BeastStrap.UI.Elements.ContextMenu
{
    /// <summary>
    /// Interaction logic for ServerInformation.xaml
    /// </summary>
    public partial class ServerHistory
    {
        public ServerHistory(ActivityWatcher watcher)
        {
            var viewModel = new ServerHistoryViewModel(watcher);

            viewModel.RequestCloseEvent += (_, _) => Close();

            // The view model subscribes to ActivityWatcher.OnGameLeave, and the watcher outlives
            // every history window by the whole session — so without this the closed window and
            // its view model stayed rooted and kept reloading on every disconnect.
            Closed += (_, _) => viewModel.Detach();

            DataContext = viewModel;
            InitializeComponent();
        }
    }
}
