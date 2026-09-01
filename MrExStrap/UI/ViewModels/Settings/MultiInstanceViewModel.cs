using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

using BeastStrap.Models;
using BeastStrap.Models.Persistable;
using BeastStrap.Utility.Accounts;

namespace BeastStrap.UI.ViewModels.Settings
{
    // One row in the account list. Wraps a saved RobloxAccount and adds the bulk-launch selection
    // checkbox state without polluting the persisted model.
    public class AccountRow : NotifyPropertyChangedViewModel
    {
        public RobloxAccount Account { get; }

        public AccountRow(RobloxAccount account) => Account = account;

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
        }

        public string Id => Account.Id;
        public string DisplayLabel => Account.DisplayLabel;
        public string UsernameLine => string.IsNullOrEmpty(Account.Username) ? "" : "@" + Account.Username;
        public string? AvatarUrl => Account.AvatarUrl;

        // The Versions Manager profile this account launches under (VersionProfile.Id; "" = use
        // the global active profile). Persisted immediately so the choice survives reload/restart.
        public string VersionProfileId
        {
            get => Account.VersionProfileId;
            set
            {
                if ((value ?? "") == Account.VersionProfileId)
                    return;
                Account.VersionProfileId = value ?? "";
                App.Accounts.Save();
                OnPropertyChanged(nameof(VersionProfileId));
            }
        }

        // Marks this saved account as the main one. Bulk launches open it first so its window
        // sits at the front of the Alt+Tab strip. Only one account can be main at a time.
        public bool IsMain
        {
            get => Account.IsMain;
            set
            {
                if (value == Account.IsMain)
                    return;

                if (value)
                {
                    foreach (var other in App.Accounts.Prop.Accounts)
                    {
                        if (other.Id != Account.Id)
                            other.IsMain = false;
                    }
                }

                Account.IsMain = value;
                App.Accounts.Save();
                OnPropertyChanged(nameof(IsMain));
            }
        }
    }

    // One entry in an account row's version dropdown. An empty Id means "use the active profile".
    public class ProfileChoice
    {
        public string Id { get; }
        public string Name { get; }
        public ProfileChoice(string id, string name) { Id = id; Name = name; }
    }

    // One row in the running-instances list. Wraps a live RobloxPlayerBeta snapshot and adds
    // the main-instance marking without touching the immutable RobloxInstanceInfo record.
    public class RunningInstanceRow : NotifyPropertyChangedViewModel
    {
        public RunningInstanceRow(int pid, string uptime, long memoryMb, string title, IntPtr windowHandle)
        {
            Pid = pid;
            Uptime = uptime;
            MemoryMb = memoryMb;
            WindowTitle = title;
            WindowHandle = windowHandle;
        }

        public int Pid { get; }
        public string Uptime { get; }
        public long MemoryMb { get; }
        public string WindowTitle { get; }

        // Main window of the client, used by the DWM live preview box and the title rewrite.
        public IntPtr WindowHandle { get; }

        private bool _isMain;
        public bool IsMain
        {
            get => _isMain;
            set
            {
                if (_isMain == value)
                    return;
                _isMain = value;
                OnPropertyChanged(nameof(IsMain));
            }
        }
    }

    public class MultiInstanceViewModel : NotifyPropertyChangedViewModel
    {
        private const string LOG_IDENT = "MultiInstanceViewModel";

        public ObservableCollection<AccountRow> Accounts { get; } = new();

        // Version-dropdown options shared by every account row (Default + each Versions Manager
        // profile). Rebuilt inside ReloadAccounts while no rows are bound, so refreshing it can
        // never write back into a live ComboBox selection.
        public ObservableCollection<ProfileChoice> AvailableProfiles { get; } = new();

        public ObservableCollection<RunningInstanceRow> RunningInstances { get; } = new();

        public MultiInstanceViewModel()
        {
            ReloadAccounts();
            RefreshRunningInstances();
        }

        // Called when the tab becomes visible (the page caches its view-model, so this is how
        // accounts saved from other tabs and newly opened/closed Roblox instances get picked up).
        public void RefreshOnShow()
        {
            ReloadAccounts();
            RefreshRunningInstances();
        }

        // ---- Accounts ----

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
            // Keep any bulk-launch ticks the user already made — a reload (including the
            // refresh-on-show) shouldn't silently clear their selection.
            var previouslySelected = Accounts.Where(a => a.IsSelected).Select(a => a.Id).ToHashSet();

            Accounts.Clear();

            // Rebuild the version dropdown options now that no rows (and no bound ComboBoxes)
            // exist, so refreshing the profile list can't blank out a live selection.
            ReloadProfiles();

            foreach (var account in AccountManager.All)
                Accounts.Add(new AccountRow(account) { IsSelected = previouslySelected.Contains(account.Id) });

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
            BulkStatus = $"Added {dialog.CreatedAccount.DisplayLabel}.";
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
        }

        private async Task LaunchAccountAsync(AccountRow? row)
        {
            if (row is null)
                return;

            bool home = LaunchToHome;
            long placeId = 0;
            if (!home && !TryGetPlaceId(out placeId))
                return;

            await _launchGate.WaitAsync();
            try
            {
                await SpaceOutLaunchAsync();
                BulkStatus = $"Launching {row.DisplayLabel}…";
                bool ok = await AccountLauncher.LaunchAsync(row.Account, placeId, NormalizedJobId, home);
                _lastLaunchUtc = DateTime.UtcNow;
                BulkStatus = ok
                    ? $"Launched {row.DisplayLabel}{(home ? " to home" : "")}."
                    : $"Couldn't launch {row.DisplayLabel} — the saved login may have expired. Re-add it.";
            }
            finally
            {
                _launchGate.Release();
            }
            RefreshRunningInstances();
        }

        // ---- Bulk launch ----

        public string BulkPlaceId
        {
            get => App.Settings.Prop.LastBulkPlaceId;
            set { App.Settings.Prop.LastBulkPlaceId = (value ?? "").Trim(); OnPropertyChanged(nameof(BulkPlaceId)); }
        }

        public string BulkJobId
        {
            get => App.Settings.Prop.LastBulkJobId;
            set { App.Settings.Prop.LastBulkJobId = (value ?? "").Trim(); OnPropertyChanged(nameof(BulkJobId)); }
        }

        public int BulkLaunchDelaySeconds
        {
            get => App.Settings.Prop.BulkLaunchDelaySeconds;
            set { App.Settings.Prop.BulkLaunchDelaySeconds = Math.Max(2, value); OnPropertyChanged(nameof(BulkLaunchDelaySeconds)); }
        }

        public bool MultiInstanceEnabled
        {
            get => App.Settings.Prop.MultiInstanceEnabled;
            set { App.Settings.Prop.MultiInstanceEnabled = value; OnPropertyChanged(nameof(MultiInstanceEnabled)); }
        }

        // ---- Multi-instance RAM reducer ----

        public bool RamReducerEnabled
        {
            get => App.Settings.Prop.MultiInstanceRamReducerEnabled;
            set { App.Settings.Prop.MultiInstanceRamReducerEnabled = value; OnPropertyChanged(nameof(RamReducerEnabled)); }
        }

        public bool RamReducerLowTextures
        {
            get => App.Settings.Prop.MultiInstanceRamReducerLowTextures;
            set { App.Settings.Prop.MultiInstanceRamReducerLowTextures = value; OnPropertyChanged(nameof(RamReducerLowTextures)); }
        }

        public bool RamReducerTrimWorkingSet
        {
            get => App.Settings.Prop.MultiInstanceRamReducerTrimWorkingSet;
            set { App.Settings.Prop.MultiInstanceRamReducerTrimWorkingSet = value; OnPropertyChanged(nameof(RamReducerTrimWorkingSet)); }
        }

        public int RamReducerTargetFps
        {
            get => App.Settings.Prop.MultiInstanceRamReducerTargetFps;
            set
            {
                App.Settings.Prop.MultiInstanceRamReducerTargetFps = Math.Clamp(value, 15, 120);
                OnPropertyChanged(nameof(RamReducerTargetFps));
            }
        }

        // When on, launches open to the Roblox home screen and the Place/Job fields are ignored.
        public bool LaunchToHome
        {
            get => App.Settings.Prop.MultiInstanceLaunchToHome;
            set
            {
                App.Settings.Prop.MultiInstanceLaunchToHome = value;
                OnPropertyChanged(nameof(LaunchToHome));
                OnPropertyChanged(nameof(IsGameLaunch));
            }
        }

        // Place ID / Job ID only apply when NOT launching to home — bound to enable those fields.
        public bool IsGameLaunch => !App.Settings.Prop.MultiInstanceLaunchToHome;

        private string _bulkStatus = "";
        public string BulkStatus
        {
            get => _bulkStatus;
            set { _bulkStatus = value; OnPropertyChanged(nameof(BulkStatus)); OnPropertyChanged(nameof(HasBulkStatus)); }
        }
        public bool HasBulkStatus => !string.IsNullOrEmpty(_bulkStatus);

        private bool _isLaunching;
        public bool IsLaunching
        {
            get => _isLaunching;
            private set { _isLaunching = value; OnPropertyChanged(nameof(IsLaunching)); OnPropertyChanged(nameof(IsNotLaunching)); }
        }
        public bool IsNotLaunching => !_isLaunching;

        // Every launch path (single row "Launch" and bulk) runs through this gate so two
        // BeastStrap processes never start in the same UTC second. The launcher names its
        // log file to the second, and a second process that lands on the same name fails to
        // initialise its logger and self-terminates as a "duplicate launch" (see Logger) — so
        // without spacing, clicking Launch on two accounts quickly, or a single launch firing
        // during a bulk launch, would silently drop one of them. Bulk already spaces its own
        // launches internally; the gate + _lastLaunchUtc extend that across single launches too.
        private readonly System.Threading.SemaphoreSlim _launchGate = new(1, 1);
        private DateTime _lastLaunchUtc = DateTime.MinValue;

        private async Task SpaceOutLaunchAsync()
        {
            var gap = TimeSpan.FromSeconds(2) - (DateTime.UtcNow - _lastLaunchUtc);
            if (gap > TimeSpan.Zero)
                await Task.Delay(gap);
        }

        public ICommand LaunchSelectedCommand => new AsyncRelayCommand(LaunchSelectedAsync);
        public ICommand LaunchAllCommand => new AsyncRelayCommand(LaunchAllAsync);

        private async Task LaunchSelectedAsync()
        {
            var selected = Accounts.Where(a => a.IsSelected).Select(a => a.Account).ToList();
            if (selected.Count == 0)
            {
                BulkStatus = "Tick the accounts you want to launch first.";
                return;
            }
            await BulkLaunchAsync(selected);
        }

        private async Task LaunchAllAsync()
        {
            var all = Accounts.Select(a => a.Account).ToList();
            if (all.Count == 0)
            {
                BulkStatus = "Add some accounts first.";
                return;
            }
            await BulkLaunchAsync(all);
        }

        private async Task BulkLaunchAsync(IReadOnlyList<RobloxAccount> accounts)
        {
            if (IsLaunching)
                return;

            bool home = LaunchToHome;
            long placeId = 0;
            if (!home && !TryGetPlaceId(out placeId))
                return;

            // The main account opens first so its window sits at the front of the Alt+Tab strip.
            accounts = accounts.OrderByDescending(a => a.IsMain).ToList();

            IsLaunching = true;
            await _launchGate.WaitAsync();
            try
            {
                await SpaceOutLaunchAsync();
                var progress = new Progress<string>(s => BulkStatus = s);
                int delay = App.Settings.Prop.BulkLaunchDelaySeconds;
                await AccountLauncher.BulkLaunchAsync(accounts, placeId, NormalizedJobId, delay, home, progress);
                _lastLaunchUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::BulkLaunch", ex);
                BulkStatus = $"Bulk launch failed ({ex.GetType().Name}).";
            }
            finally
            {
                _launchGate.Release();
                IsLaunching = false;
                RefreshRunningInstances();
            }
        }

        private string? NormalizedJobId => string.IsNullOrWhiteSpace(BulkJobId) ? null : BulkJobId.Trim();

        private bool TryGetPlaceId(out long placeId)
        {
            placeId = 0;
            string raw = (BulkPlaceId ?? "").Trim();
            if (long.TryParse(raw, out placeId) && placeId > 0)
                return true;

            BulkStatus = "Enter a valid Place ID (the number in the game's URL).";
            return false;
        }

        // ---- Running instances (relocated from the Settings page) ----

        public string RunningInstancesHeader => RunningInstances.Count switch
        {
            0 => "Running Roblox instances (none)",
            1 => "Running Roblox instances (1)",
            _ => $"Running Roblox instances ({RunningInstances.Count})"
        };

        public bool HasNoRunningInstances => RunningInstances.Count == 0;

        public ICommand RefreshRunningInstancesCommand => new RelayCommand(RefreshRunningInstances);
        public ICommand KillInstanceCommand => new RelayCommand<int>(KillInstance);
        public ICommand SetMainInstanceCommand => new RelayCommand<int>(SetMainInstance);

        private void RefreshRunningInstances()
        {
            RunningInstances.Clear();

            try
            {
                var rows = new List<RunningInstanceRow>();

                foreach (var p in Process.GetProcessesByName("RobloxPlayerBeta"))
                {
                    string uptime;
                    long memMb = 0;
                    string title = "";
                    IntPtr hwnd = IntPtr.Zero;

                    try
                    {
                        uptime = FormatUptime(DateTime.Now - p.StartTime);
                        memMb = p.WorkingSet64 / 1024 / 1024;
                        hwnd = InstanceWindow.FindMainWindow(p.Id);
                        title = hwnd == IntPtr.Zero ? "" : InstanceWindow.GetWindowTitle(hwnd);
                    }
                    catch
                    {
                        uptime = "?";
                    }

                    rows.Add(new RunningInstanceRow(p.Id, uptime, memMb, title, hwnd));
                    p.Dispose();
                }

                // Persisted main marking: if the saved PID is gone, clear it; otherwise flag the
                // row and re-arm the title marker (idempotent — an already-running marker no-ops).
                int mainPid = App.State.Prop.MainInstancePid;
                bool mainAlive = rows.Any(r => r.Pid == mainPid);
                if (!mainAlive && mainPid != 0)
                {
                    App.State.Prop.MainInstancePid = 0;
                    App.State.Save();
                }

                foreach (var row in rows)
                    row.IsMain = row.Pid == mainPid;

                foreach (var row in rows.OrderByDescending(r => r.IsMain).ThenBy(r => r.Pid))
                    RunningInstances.Add(row);

                if (mainAlive)
                    MainInstanceMarker.Mark(mainPid);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::RefreshRunningInstances", ex);
            }

            OnPropertyChanged(nameof(HasNoRunningInstances));
            OnPropertyChanged(nameof(RunningInstancesHeader));
        }

        private void KillInstance(int pid)
        {
            MainInstanceMarker.Unmark(pid);

            if (App.State.Prop.MainInstancePid == pid)
            {
                App.State.Prop.MainInstancePid = 0;
                App.State.Save();
            }

            try
            {
                using var proc = Process.GetProcessById(pid);
                proc.Kill();
                proc.WaitForExit(2000);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::KillInstance", ex);
            }
            RefreshRunningInstances();
        }

        // Marks a running instance as the main one: gold badge + top-of-list in the tab, the
        // Roblox window gets a re-applied "★ MAIN — <game>" title (see MainInstanceMarker) so
        // Alt+Tab shows it instantly, and it's brought to the foreground right now. Persisted
        // in State so the marking survives an app restart while the client runs.
        private void SetMainInstance(int pid)
        {
            if (RunningInstances.All(r => r.Pid != pid))
                return;

            foreach (var row in RunningInstances)
                row.IsMain = row.Pid == pid;

            App.State.Prop.MainInstancePid = pid;
            App.State.Save();

            MainInstanceMarker.Mark(pid);

            // Re-sort so the main sits on top without rebuilding the collection.
            var ordered = RunningInstances.OrderByDescending(r => r.IsMain).ThenBy(r => r.Pid).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                int current = RunningInstances.IndexOf(ordered[i]);
                if (current != i)
                    RunningInstances.Move(current, i);
            }
        }

        private static string FormatUptime(TimeSpan t)
        {
            if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
            if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
            return $"{(int)t.TotalSeconds}s";
        }
    }
}
