namespace BeastStrap.UI.ViewModels.Settings
{
    public class BehaviourViewModel : NotifyPropertyChangedViewModel
    {
        public bool ConfirmLaunches
        {
            get => App.Settings.Prop.ConfirmLaunches;
            set => App.Settings.Prop.ConfirmLaunches = value;
        }

        public bool ShowVersionPickerOnLaunch
        {
            get => App.Settings.Prop.ShowVersionPickerOnLaunch;
            set { App.Settings.Prop.ShowVersionPickerOnLaunch = value; OnPropertyChanged(nameof(ShowVersionPickerOnLaunch)); }
        }

        public bool JoinEmptiestServerOnLaunch
        {
            get => App.Settings.Prop.JoinEmptiestServerOnLaunch;
            set { App.Settings.Prop.JoinEmptiestServerOnLaunch = value; OnPropertyChanged(nameof(JoinEmptiestServerOnLaunch)); }
        }

        public bool ConfirmNonLiveLaunch
        {
            get => App.Settings.Prop.ConfirmNonLiveLaunch;
            set { App.Settings.Prop.ConfirmNonLiveLaunch = value; OnPropertyChanged(nameof(ConfirmNonLiveLaunch)); }
        }

        public bool BackgroundUpdates
        {
            get => App.Settings.Prop.BackgroundUpdatesEnabled;
            set => App.Settings.Prop.BackgroundUpdatesEnabled = value;
        }

        public bool IsRobloxInstallationMissing => !App.IsPlayerInstalled && !App.IsStudioInstalled;

        public bool ForceRobloxReinstallation
        {
            get => App.State.Prop.ForceReinstall || IsRobloxInstallationMissing;
            set => App.State.Prop.ForceReinstall = value;
        }
    }
}
