using BeastStrap.UI.ViewModels.Dialogs;
using BeastStrap.UI.ViewModels.Settings;

namespace BeastStrap.UI.Elements.Dialogs
{
    public partial class ScriptDetailsDialog
    {
        public ScriptDetailsDialog(ScriptRow row)
        {
            var vm = new ScriptDetailsViewModel(row);
            vm.CloseRequested += (_, _) => Close();

            DataContext = vm;
            InitializeComponent();
        }
    }
}
