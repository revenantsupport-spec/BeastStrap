using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

using BeastStrap.Models.Persistable;
using BeastStrap.Utility.Accounts;

namespace BeastStrap.UI.ViewModels.Settings
{
    // A deliberately simple, single-account launcher, separate from the Multi Instance tab's
    // bulk/tick machinery. Every row launches exactly ONE account; there is no selection, no
    // bulk launch and no multi-account code path here, so the "starts a second Roblox" quirk of
    // the bulk flow can't show up. Accounts are the same store (Accounts.json) as Multi Instance,
    // so an account added here appears there and vice versa.
    public class AccountsManagerViewModel : NotifyPropertyChangedViewModel
    {
        private const string LOG_IDENT = "AccountsManagerViewModel";

        // Reused row + profile-choice types from the Multi Instance view-model so both tabs
        // render the same saved accounts and the same per-account version dropdown.
        public ObservableCollection<AccountRow> Accounts { get; } = new();
        public ObservableCollection<ProfileChoice> AvailableProfiles { get; } = new();

        public AccountsManagerViewModel()
        {
            ReloadAccounts();
        }

        // Called when the tab becomes visible so accounts saved from other tabs (Multi Instance,
        // Alt Generator's "Save to Multi Instance") show up without an app restart.
        public void RefreshOnShow() => ReloadAccounts();

        public string AccountsHeader => Accounts.Count switch
        {
            0 => "Accounts (none yet)",
            1 => "Accounts (1)",
            _ => $"Accounts ({Accounts.Count})"
        };

        public bool HasNoAccounts => Accounts.Count == 0;

        public ICommand AddAccountCommand => new RelayCommand(AddAccount);
        public ICommand RemoveAccountCommand => new RelayCommand<AccountRow>(RemoveAccount);
        public ICommand LaunchAccountCommand => new AsyncRelayCommand<AccountRow>(LaunchAccountAsync);

        private void ReloadAccounts()
        {
            Accounts.Clear();
            ReloadProfiles();

            foreach (var account in AccountManager.All)
                Accounts.Add(new AccountRow(account));

            OnPropertyChanged(nameof(AccountsHeader));
            OnPropertyChanged(nameof(HasNoAccounts));
        }

        private void ReloadProfiles()
        {
            AvailableProfiles.Clear();
            AvailableProfiles.Add(new ProfileChoice("", "Default (active version)"));
            foreach (var p in App.Settings.Prop.VersionProfiles)
                AvailableProfiles.Add(new ProfileChoice(p.Id, p.Name));
        }

        private void AddAccount()
        {
            var dialog = new UI.Elements.Dialogs.AddAccountDialog();
            dialog.ShowDialog();

            if (dialog.CreatedAccount is null)
                return;

            AccountManager.Add(dialog.CreatedAccount);
            ReloadAccounts();
            Status = $"Added {dialog.CreatedAccount.DisplayLabel}.";
        }

        private void RemoveAccount(AccountRow? row)
        {
            if (row is null)
                return;

            var confirm = Frontend.ShowMessageBox(
                $"Remove {row.DisplayLabel} from BeastStrap?\n\nThis only deletes the saved login on this PC. The Roblox account itself is untouched.",
                MessageBoxImage.Question, MessageBoxButton.YesNo, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes)
                return;

            AccountManager.Remove(row.Id);
            ReloadAccounts();
            Status = $"Removed {row.DisplayLabel}.";
        }

        // Launch exactly one account. Opens it to the Roblox home screen (no Place ID needed) —
        // the most reliable single-account path, and the one that always works from a cold
        // state. Spacing still applies so two quick launches never collide on the same-named log.
        private async Task LaunchAccountAsync(AccountRow? row)
        {
            if (row is null)
                return;

            await _launchGate.WaitAsync();
            try
            {
                await SpaceOutLaunchAsync();
                Status = $"Launching {row.DisplayLabel}…";
                bool ok = await AccountLauncher.LaunchAsync(row.Account, 0, null, launchToHome: true);
                _lastLaunchUtc = DateTime.UtcNow;
                Status = ok
                    ? $"Launched {row.DisplayLabel} to the home screen."
                    : $"Couldn't launch {row.DisplayLabel} — the saved login may have expired. Re-add it.";
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::LaunchAccount", ex);
                Status = $"Launch failed ({ex.GetType().Name}).";
            }
            finally
            {
                _launchGate.Release();
            }
        }

        // Same same-named-log safety as the Multi Instance tab: launches never land in the same
        // UTC second, or the later BeastStrap process self-terminates as a "duplicate launch".
        private readonly SemaphoreSlim _launchGate = new(1, 1);
        private DateTime _lastLaunchUtc = DateTime.MinValue;

        private async Task SpaceOutLaunchAsync()
        {
            var gap = TimeSpan.FromSeconds(2) - (DateTime.UtcNow - _lastLaunchUtc);
            if (gap > TimeSpan.Zero)
                await Task.Delay(gap);
        }

        private string _status = "";
        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(HasStatus)); }
        }
        public bool HasStatus => !string.IsNullOrEmpty(_status);
    }
}