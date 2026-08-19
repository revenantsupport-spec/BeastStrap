using System.Windows;
using System.Windows.Input;
using BeastStrap.Integrations;
using BeastStrap.Utility;
using CommunityToolkit.Mvvm.Input;

namespace BeastStrap.UI.ViewModels.ContextMenu
{
    internal class ServerInformationViewModel : NotifyPropertyChangedViewModel
    {
        private readonly ActivityWatcher _activityWatcher;

        public string InstanceId => _activityWatcher.Data.JobId;

        public string ServerType => _activityWatcher.Data.ServerType.ToTranslatedString();

        public string ServerLocation { get; private set; } = Strings.Common_Loading;

        public string ServerRegion { get; private set; } = Strings.Common_Loading;

        public Visibility ServerLocationVisibility => App.Settings.Prop.ShowServerDetails ? Visibility.Visible : Visibility.Collapsed;

        public ICommand CopyInstanceIdCommand => new RelayCommand(CopyInstanceId);

        public ServerInformationViewModel(Watcher watcher)
        {
            _activityWatcher = watcher.ActivityWatcher!;

            if (ServerLocationVisibility == Visibility.Visible)
            {
                QueryServerLocation();
                QueryServerRegion();
            }
        }

        // Both queries are async void (fired from the ctor), so an exception escaping
        // them is an unhandled process-killer — catch everything and show "N/A".

        public async void QueryServerLocation()
        {
            string? location = null;
            try
            {
                location = await _activityWatcher.Data.QueryServerLocation();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("ServerInformationViewModel::QueryServerLocation", ex);
            }

            ServerLocation = String.IsNullOrEmpty(location) ? Strings.Common_NotAvailable : location;
            OnPropertyChanged(nameof(ServerLocation));
        }

        public async void QueryServerRegion()
        {
            string? region = null;
            try
            {
                if (_activityWatcher.Data.MachineAddressValid)
                    region = await RobloxDatacenters.ResolveRegionAsync(_activityWatcher.Data.MachineAddress);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("ServerInformationViewModel::QueryServerRegion", ex);
            }

            ServerRegion = String.IsNullOrEmpty(region) ? Strings.Common_NotAvailable : region;
            OnPropertyChanged(nameof(ServerRegion));
        }

        private void CopyInstanceId() => Utilities.TrySetClipboardText(InstanceId, "ServerInformationViewModel::CopyInstanceId");
    }
}
