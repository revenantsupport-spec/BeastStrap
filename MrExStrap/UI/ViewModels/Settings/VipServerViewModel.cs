using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

using BeastStrap.Utility;

namespace BeastStrap.UI.ViewModels.Settings
{
    public class VipServerViewModel : NotifyPropertyChangedViewModel
    {
        private const string LOG_IDENT = "VipServerViewModel";

        public VipServerViewModel()
        {
            _ = LoadLiveIndexAsync(false);
        }

        public bool VipServerPromptEnabled
        {
            get => App.Settings.Prop.EnableVipServerPrompt;
            set => App.Settings.Prop.EnableVipServerPrompt = value;
        }

        // ---- live index -------------------------------------------------------------

        private string _liveIndexText = "Checking how many servers are listed…";
        public string LiveIndexText
        {
            get => _liveIndexText;
            private set { _liveIndexText = value; OnPropertyChanged(nameof(LiveIndexText)); }
        }

        private bool _liveIndexLoading;
        public bool LiveIndexLoading
        {
            get => _liveIndexLoading;
            private set
            {
                _liveIndexLoading = value;
                OnPropertyChanged(nameof(LiveIndexLoading));
                OnPropertyChanged(nameof(LiveIndexRingVisibility));
            }
        }

        public Visibility LiveIndexRingVisibility => _liveIndexLoading ? Visibility.Visible : Visibility.Collapsed;

        public ICommand RefreshLiveIndexCommand => new AsyncRelayCommand(() => LoadLiveIndexAsync(true));

        private async Task LoadLiveIndexAsync(bool forceRefresh)
        {
            LiveIndexLoading = true;

            try
            {
                var index = await RbxServersClient.GetLiveIndexAsync(forceRefresh);

                LiveIndexText = index is null
                    ? "Couldn't reach rbxservers.xyz to count the servers. The picker will still work if the site comes back."
                    : index.Value.Games > 0
                        ? $"{index.Value.Servers:N0} VIP servers you can join right now, across {index.Value.Games:N0} games."
                        : $"{index.Value.Servers:N0} VIP servers you can join right now.";
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::LoadLiveIndex", ex);
                LiveIndexText = "Couldn't read the server count right now.";
            }
            finally
            {
                LiveIndexLoading = false;
            }
        }
    }
}
