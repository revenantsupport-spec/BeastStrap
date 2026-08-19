using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

using BeastStrap.Models.Persistable;
using BeastStrap.Utility.Accounts;

namespace BeastStrap.UI.ViewModels.Dialogs
{
    // Backs AddAccountDialog. Two ways in: paste a .ROBLOSECURITY cookie, or use the embedded
    // browser login. Either way we validate against Roblox, fetch the avatar, encrypt the cookie,
    // and hand a ready-to-save RobloxAccount back to the dialog via CloseRequested.
    public class AddAccountViewModel : NotifyPropertyChangedViewModel
    {
        private const string LOG_IDENT = "AddAccountViewModel";

        public event EventHandler<RobloxAccount?>? CloseRequested;

        private string _cookieText = "";
        public string CookieText
        {
            get => _cookieText;
            set { _cookieText = value ?? ""; OnPropertyChanged(nameof(CookieText)); }
        }

        private string _statusMessage = "";
        public string StatusMessage
        {
            get => _statusMessage;
            private set { _statusMessage = value; OnPropertyChanged(nameof(StatusMessage)); OnPropertyChanged(nameof(HasStatus)); }
        }
        public bool HasStatus => !string.IsNullOrEmpty(_statusMessage);

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            private set { _isBusy = value; OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(IsNotBusy)); }
        }
        public bool IsNotBusy => !_isBusy;

        public ICommand AddCommand { get; }
        public ICommand BrowserLoginCommand { get; }
        public ICommand CancelCommand { get; }

        public AddAccountViewModel()
        {
            AddCommand = new AsyncRelayCommand(AddAsync);
            BrowserLoginCommand = new AsyncRelayCommand(BrowserLoginAsync);
            CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, null));
        }

        private async Task AddAsync() => await TryBuildAndCloseAsync(CookieText);

        private async Task BrowserLoginAsync()
        {
            if (IsBusy)
                return;

            var dialog = new UI.Elements.Dialogs.AccountLoginDialog();
            dialog.ShowDialog();

            if (string.IsNullOrEmpty(dialog.Cookie))
            {
                StatusMessage = "No account was captured from the login window.";
                return;
            }

            await TryBuildAndCloseAsync(dialog.Cookie);
        }

        private async Task TryBuildAndCloseAsync(string? cookie)
        {
            if (IsBusy)
                return;

            cookie = (cookie ?? "").Trim();
            if (string.IsNullOrEmpty(cookie))
            {
                StatusMessage = "Paste a .ROBLOSECURITY cookie, or use \"Log in with browser\".";
                return;
            }

            IsBusy = true;
            StatusMessage = "Checking the account…";
            try
            {
                var account = await AccountManager.BuildFromCookieAsync(cookie);
                if (account is null)
                {
                    StatusMessage = "That cookie didn't work — it may be invalid, expired, or you're offline.";
                    return;
                }

                CloseRequested?.Invoke(this, account);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                StatusMessage = $"Something went wrong ({ex.GetType().Name}).";
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
