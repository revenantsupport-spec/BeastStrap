using BeastStrap.Models.Persistable;
using BeastStrap.UI.ViewModels.Dialogs;

namespace BeastStrap.UI.Elements.Dialogs
{
    public partial class AddAccountDialog
    {
        public RobloxAccount? CreatedAccount { get; private set; }

        public AddAccountDialog()
        {
            var vm = new AddAccountViewModel();
            vm.CloseRequested += (_, account) =>
            {
                CreatedAccount = account;
                Close();
            };
            DataContext = vm;
            InitializeComponent();
        }
    }
}
