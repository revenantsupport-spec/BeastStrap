using System.Windows;
using System.Windows.Input;

using CommunityToolkit.Mvvm.Input;

using BeastStrap.Utility;

namespace BeastStrap.UI.ViewModels.Settings
{
    // Backs the Link Bypasser tab: paste a locked link (Linkvertise, Lootlabs, Work.ink, …), get the
    // destination via bypass.tools. The API key is the user's own (local settings only); sign-up is pushed
    // through the referral link in the view.
    public class LinkBypasserViewModel : NotifyPropertyChangedViewModel
    {
        private const string LOG_IDENT = "LinkBypasser";

        private string _linkInput = "";
        public string LinkInput
        {
            get => _linkInput;
            set { _linkInput = value ?? ""; OnPropertyChanged(nameof(LinkInput)); }
        }

        private string _resultUrl = "";
        public string ResultUrl
        {
            get => _resultUrl;
            set { _resultUrl = value ?? ""; OnPropertyChanged(nameof(ResultUrl)); OnPropertyChanged(nameof(HasResult)); OnPropertyChanged(nameof(ResultVisibility)); }
        }
        public bool HasResult => _resultUrl.Length > 0;
        public Visibility ResultVisibility => HasResult ? Visibility.Visible : Visibility.Collapsed;

        // bypass.tools API key — persisted in local settings only (never shipped). Blank = disabled.
        public string ApiKey
        {
            get => App.Settings.Prop.BypassToolsApiKey;
            set { App.Settings.Prop.BypassToolsApiKey = value ?? ""; OnPropertyChanged(nameof(ApiKey)); }
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(NotBusy)); }
        }
        public bool NotBusy => !_isBusy;

        private string _statusText = "";
        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(nameof(StatusText)); }
        }

        private bool _isError;
        public bool IsError
        {
            get => _isError;
            set { _isError = value; OnPropertyChanged(nameof(IsError)); }
        }

        public ICommand BypassCommand => new AsyncRelayCommand(Bypass);
        public ICommand PasteCommand => new RelayCommand(PasteLink);
        public ICommand CopyResultCommand => new RelayCommand(CopyResult);

        private async Task Bypass()
        {
            IsBusy = true;
            IsError = false;
            ResultUrl = "";
            StatusText = "Bypassing… some links can take up to a minute.";
            try
            {
                var r = await BypassToolsClient.BypassAsync(LinkInput, ApiKey);
                if (r.Success)
                {
                    ResultUrl = r.ResultUrl;
                    IsError = false;
                    StatusText = r.Cached ? "Done — pulled from cache." : "Done.";
                }
                else
                {
                    IsError = true;
                    StatusText = r.Error ?? "Couldn't bypass that link.";
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void PasteLink()
        {
            try
            {
                if (Clipboard.ContainsText())
                    LinkInput = Clipboard.GetText().Trim();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }
        }

        private void CopyResult()
        {
            if (!HasResult)
                return;
            try
            {
                Clipboard.SetDataObject(ResultUrl, true);
                IsError = false;
                StatusText = "Copied the destination link to your clipboard.";
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }
        }
    }
}
