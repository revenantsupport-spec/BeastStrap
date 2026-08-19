using BeastStrap.Models.Persistable;
using BeastStrap.UI.ViewModels.Dialogs;

namespace BeastStrap.UI.Elements.Dialogs
{
    public partial class AddVersionProfileDialog
    {
        public VersionProfile? CreatedProfile { get; private set; }

        public AddVersionProfileDialog()
        {
            var vm = new AddVersionProfileViewModel();
            vm.CloseRequested += (_, profile) =>
            {
                CreatedProfile = profile;
                Close();
            };
            DataContext = vm;
            InitializeComponent();
        }
    }
}
