using BeastStrap.Models.Persistable;
using BeastStrap.UI.ViewModels.Dialogs;

namespace BeastStrap.UI.Elements.Dialogs
{
    public partial class VersionPickerDialog
    {
        public VersionProfile? PickedProfile { get; private set; }

        public VersionPickerDialog()
        {
            var vm = new VersionPickerViewModel();
            vm.CloseRequested += (_, profile) =>
            {
                PickedProfile = profile;
                Close();
            };
            DataContext = vm;
            InitializeComponent();
        }
    }
}
