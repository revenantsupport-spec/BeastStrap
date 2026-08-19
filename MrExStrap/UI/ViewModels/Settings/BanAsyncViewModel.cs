using System.Collections.ObjectModel;
using System.Security.Principal;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

using BeastStrap.Utility.BanAsync;

namespace BeastStrap.UI.ViewModels.Settings
{
    public class BanAsyncViewModel : NotifyPropertyChangedViewModel
    {
        private const string LOG_IDENT = "BanAsyncViewModel";
        private const int MaxLogEntries = 500;

        public BanAsyncViewModel()
        {
            IsElevated = CheckElevated();
            RefreshAdapters();
        }

        // ---- elevation ----------------------------------------------------------------

        public bool IsElevated { get; }

        public Visibility ElevationWarningVisibility => IsElevated ? Visibility.Collapsed : Visibility.Visible;
        public Visibility AdminFeaturesVisibility => IsElevated ? Visibility.Visible : Visibility.Collapsed;

        public string ElevationStatusText =>
            IsElevated
                ? "Running with administrator privileges. All actions on this page are available."
                : "BeastStrap is NOT running as administrator. MAC spoofing, MachineGuid changes, and prefetch cleanup are disabled. Close BeastStrap and relaunch it with 'Run as administrator' to use them.";

        // ---- adapters -----------------------------------------------------------------

        public ObservableCollection<NetworkAdapter> Adapters { get; } = new();

        private NetworkAdapter? _selectedAdapter;
        public NetworkAdapter? SelectedAdapter
        {
            get => _selectedAdapter;
            set
            {
                _selectedAdapter = value;
                OnPropertyChanged(nameof(SelectedAdapter));
                OnPropertyChanged(nameof(CurrentMacFormatted));
                OnPropertyChanged(nameof(OriginalMacFormatted));
            }
        }

        public string CurrentMacFormatted =>
            SelectedAdapter == null ? "(no adapter selected)" : NetworkAdapter.FormatMac(SelectedAdapter.PhysicalAddress);

        // The pre-spoof hardware MAC for the selected adapter, if we captured one. Shown
        // next to the current MAC so the user can see what they spoofed away from and what
        // Revert will restore. Mirrors the MachineGuid backup display.
        public string OriginalMacFormatted =>
            SelectedAdapter != null
            && App.Settings.Prop.BanAsyncOriginalMacByGuid.TryGetValue(SelectedAdapter.Id, out var original)
                ? NetworkAdapter.FormatMac(original)
                : "(none saved yet)";

        // ---- bound settings -----------------------------------------------------------

        public bool PreserveInGameSettings
        {
            get => App.Settings.Prop.BanAsyncPreserveInGameSettings;
            set { App.Settings.Prop.BanAsyncPreserveInGameSettings = value; OnPropertyChanged(nameof(PreserveInGameSettings)); }
        }

        public bool PreserveFastFlags
        {
            get => App.Settings.Prop.BanAsyncPreserveFastFlags;
            set { App.Settings.Prop.BanAsyncPreserveFastFlags = value; OnPropertyChanged(nameof(PreserveFastFlags)); }
        }

        public bool IncludeStudioFolders
        {
            get => App.Settings.Prop.BanAsyncIncludeStudioFolders;
            set { App.Settings.Prop.BanAsyncIncludeStudioFolders = value; OnPropertyChanged(nameof(IncludeStudioFolders)); }
        }

        public bool ClearBrowserCookies
        {
            get => App.Settings.Prop.BanAsyncClearBrowserCookies;
            set { App.Settings.Prop.BanAsyncClearBrowserCookies = value; OnPropertyChanged(nameof(ClearBrowserCookies)); }
        }

        public bool CleanMrExVersions
        {
            get => App.Settings.Prop.BanAsyncCleanVersions;
            set { App.Settings.Prop.BanAsyncCleanVersions = value; OnPropertyChanged(nameof(CleanMrExVersions)); }
        }

        public bool DhcpRefreshAfterSpoof
        {
            get => App.Settings.Prop.BanAsyncDhcpRefreshAfterSpoof;
            set { App.Settings.Prop.BanAsyncDhcpRefreshAfterSpoof = value; OnPropertyChanged(nameof(DhcpRefreshAfterSpoof)); }
        }

        public bool Persistent
        {
            get => App.Settings.Prop.BanAsyncPersistent;
            set { App.Settings.Prop.BanAsyncPersistent = value; OnPropertyChanged(nameof(Persistent)); }
        }

        public bool AdvancedMode
        {
            get => App.Settings.Prop.BanAsyncAdvancedMode;
            set
            {
                App.Settings.Prop.BanAsyncAdvancedMode = value;
                OnPropertyChanged(nameof(AdvancedMode));
                OnPropertyChanged(nameof(AdvancedVisibility));
                OnPropertyChanged(nameof(SimpleVisibility));
            }
        }

        public Visibility AdvancedVisibility => AdvancedMode ? Visibility.Visible : Visibility.Collapsed;
        public Visibility SimpleVisibility => AdvancedMode ? Visibility.Collapsed : Visibility.Visible;

        public bool OuiMirror
        {
            get => App.Settings.Prop.BanAsyncOuiMirror;
            set { App.Settings.Prop.BanAsyncOuiMirror = value; OnPropertyChanged(nameof(OuiMirror)); }
        }

        public bool MachineGuidAcknowledged
        {
            get => App.Settings.Prop.BanAsyncMachineGuidAcknowledged;
            set
            {
                App.Settings.Prop.BanAsyncMachineGuidAcknowledged = value;
                OnPropertyChanged(nameof(MachineGuidAcknowledged));
                OnPropertyChanged(nameof(MachineGuidActionsEnabled));
            }
        }

        public bool MachineGuidActionsEnabled => IsElevated && MachineGuidAcknowledged;

        private string _customMac = "";
        public string CustomMac
        {
            get => _customMac;
            set { _customMac = value ?? ""; OnPropertyChanged(nameof(CustomMac)); }
        }

        public string CurrentMachineGuid => MachineGuidSpoofer.ReadCurrent() ?? "(unreadable)";

        public bool HasMachineGuidBackup => !string.IsNullOrEmpty(App.Settings.Prop.BanAsyncOriginalMachineGuid);

        public string MachineGuidBackupText =>
            HasMachineGuidBackup
                ? $"Original MachineGuid: {App.Settings.Prop.BanAsyncOriginalMachineGuid}"
                : "No original MachineGuid backed up yet — the first randomize will save the current value.";

        // ---- activity log -------------------------------------------------------------

        public ObservableCollection<string> ActivityLog { get; } = new();

        // Raised when an action (clean/spoof/revert) starts so the view can bring the
        // activity log into view and scroll it to the latest line.
        public event EventHandler? ScrollToLogRequested;
        private void RequestScrollToLog() => ScrollToLogRequested?.Invoke(this, EventArgs.Empty);

        private void Log(string line)
        {
            string stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
            App.Logger.WriteLine(LOG_IDENT, line);

            void apply()
            {
                ActivityLog.Add(stamped);
                while (ActivityLog.Count > MaxLogEntries)
                    ActivityLog.RemoveAt(0);
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.BeginInvoke(new Action(apply));
            else
                apply();
        }

        // ---- commands -----------------------------------------------------------------

        public ICommand RelaunchAsAdminCommand => new RelayCommand(RelaunchAsAdmin);

        public ICommand RefreshAdaptersCommand => new RelayCommand(RefreshAdapters);
        public ICommand CleanTracesCommand => new AsyncRelayCommand(CleanTracesAsync);
        public ICommand SpoofCommand => new AsyncRelayCommand(SpoofAsync);
        public ICommand RevertCommand => new AsyncRelayCommand(RevertAsync);
        public ICommand ShuffleMacCommand => new RelayCommand(ShuffleCustomMac);
        public ICommand RandomizeMachineGuidCommand => new AsyncRelayCommand(RandomizeMachineGuidAsync);
        public ICommand RestoreMachineGuidCommand => new AsyncRelayCommand(RestoreMachineGuidAsync);
        public ICommand ClearLogCommand => new RelayCommand(() => ActivityLog.Clear());

        private void RefreshAdapters()
        {
            string? previouslySelectedId = SelectedAdapter?.Id;
            Adapters.Clear();

            foreach (var a in MacSpoofer.EnumeratePhysicalAdapters())
                Adapters.Add(a);

            SelectedAdapter = Adapters.FirstOrDefault(a => a.Id == previouslySelectedId)
                              ?? Adapters.FirstOrDefault();
            Log($"Detected {Adapters.Count} physical adapter(s).");
        }

        private async Task CleanTracesAsync()
        {
            string prompt =
                "This will close Roblox and delete:\n" +
                "  • %LocalAppData%\\Roblox\n" +
                "  • %AppData%\\Roblox\\logs and \\http\n" +
                "  • %ProgramData%\\Roblox\n" +
                "  • %Temp%\\Roblox*\n" +
                "  • Prefetch entries for Roblox (admin only)\n" +
                "  • HKCU\\Software\\ROBLOX Corporation\n" +
                (CleanMrExVersions ? "  • BeastStrap's downloaded Roblox installs (Versions) — forces a full re-download next launch\n" : "") +
                (ClearBrowserCookies ? "  • Roblox cookies in any installed browsers (Chrome, Edge, Firefox, Brave, Opera, Vivaldi)\n" : "") +
                "\n" +
                (PreserveInGameSettings ? "In-game settings (GlobalBasicSettings_*.xml) will be preserved.\n" : "") +
                (PreserveFastFlags ? "Vanilla Roblox FastFlags JSON will be preserved.\n" : "") +
                (ClearBrowserCookies ? "Close any open browsers first or their cookie files will be locked and skipped.\n" : "") +
                "\nBeastStrap's own settings, FastFlags, and themes are NOT touched.\n\nContinue?";

            var confirm = Frontend.ShowMessageBox(prompt, MessageBoxImage.Warning,
                MessageBoxButton.YesNo, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes)
            {
                Log("Cleanup cancelled.");
                return;
            }

            RequestScrollToLog();
            Log("Starting trace cleanup…");
            var options = new CleanupEngine.CleanupOptions
            {
                PreserveInGameSettings = PreserveInGameSettings,
                PreserveFastFlags = PreserveFastFlags,
                IncludeStudioFolders = IncludeStudioFolders,
                CleanMrExVersions = CleanMrExVersions
            };

            CleanupEngine.CleanupResult result = await Task.Run(() => CleanupEngine.RunCleanup(options, Log));

            Log($"Cleanup done. Removed {result.DeletedDirectories} dir(s), {result.DeletedFiles} file(s), {result.RegistryKeysRemoved} registry key(s). Preserved {result.PreservedFiles} file(s). Skipped {result.Skipped.Count}.");

            if (ClearBrowserCookies)
            {
                Log("Clearing Roblox cookies from installed browsers…");
                var cookieResult = await Task.Run(() => BrowserCookieCleaner.ClearRobloxCookies(Log));
                Log($"Browser cookies: cleared {cookieResult.CookiesDeleted} across {cookieResult.BrowsersScanned} browser(s), checked {cookieResult.FilesScanned} profile file(s), skipped {cookieResult.Skipped.Count}.");
            }
        }

        private async Task SpoofAsync()
        {
            if (!IsElevated)
            {
                Log("Not elevated — spoof is disabled.");
                return;
            }

            RequestScrollToLog();

            var targets = AdvancedMode
                ? (SelectedAdapter == null ? new List<NetworkAdapter>() : new List<NetworkAdapter> { SelectedAdapter })
                : Adapters.ToList();

            if (targets.Count == 0)
            {
                Log("No adapters to spoof.");
                return;
            }

            string? customNormalized = null;
            if (AdvancedMode && !string.IsNullOrWhiteSpace(CustomMac))
            {
                if (!MacSpoofer.IsValidMacHex(CustomMac))
                {
                    Frontend.ShowMessageBox("That MAC address isn't valid. Use 12 hex characters (separators optional).",
                        MessageBoxImage.Warning);
                    return;
                }
                customNormalized = MacSpoofer.NormalizeMacHex(CustomMac);
            }

            await Task.Run(() =>
            {
                foreach (var adapter in targets)
                {
                    string newMac = customNormalized ??
                                    MacSpoofer.GenerateRandomMac(OuiMirror ? adapter.PhysicalAddress : null);

                    // Record the real hardware MAC once, before the first spoof. After the
                    // adapter restarts the OS reports the spoofed MAC as PhysicalAddress, so
                    // this is the only point we can capture the original for the UI / revert.
                    if (!string.IsNullOrEmpty(adapter.PhysicalAddress)
                        && !App.Settings.Prop.BanAsyncOriginalMacByGuid.ContainsKey(adapter.Id))
                    {
                        string original = adapter.PhysicalAddress;
                        Application.Current?.Dispatcher?.Invoke(() =>
                            App.Settings.Prop.BanAsyncOriginalMacByGuid[adapter.Id] = original);
                    }

                    Log($"Spoofing {adapter.Name} → {NetworkAdapter.FormatMac(newMac)}…");
                    bool ok = MacSpoofer.SpoofAdapter(adapter, newMac, Log);

                    if (ok && !App.Settings.Prop.BanAsyncSpoofedAdapterGuids.Contains(adapter.Id))
                    {
                        Application.Current?.Dispatcher?.Invoke(() =>
                            App.Settings.Prop.BanAsyncSpoofedAdapterGuids.Add(adapter.Id));
                    }

                    if (ok && DhcpRefreshAfterSpoof)
                        MacSpoofer.DhcpRefresh(adapter.Name, Log);
                }
            });

            Log("Spoof pass finished. Refreshing adapter list…");
            RefreshAdapters();
        }

        private async Task RevertAsync()
        {
            if (!IsElevated)
            {
                Log("Not elevated — revert is disabled.");
                return;
            }

            RequestScrollToLog();

            var ids = App.Settings.Prop.BanAsyncSpoofedAdapterGuids.ToList();
            // Include any adapter we can see, even if not in the tracked list — covers cases
            // where the user spoofed via another tool but wants to revert here.
            var toRevert = Adapters.Where(a => ids.Contains(a.Id) || HasNetworkAddressOverride(a)).ToList();

            if (toRevert.Count == 0)
            {
                Log("No adapters appear to be spoofed.");
                return;
            }

            await Task.Run(() =>
            {
                foreach (var adapter in toRevert)
                {
                    Log($"Reverting {adapter.Name}…");
                    bool ok = MacSpoofer.RevertAdapter(adapter, Log);
                    if (ok)
                    {
                        Application.Current?.Dispatcher?.Invoke(() =>
                        {
                            App.Settings.Prop.BanAsyncSpoofedAdapterGuids.Remove(adapter.Id);
                            App.Settings.Prop.BanAsyncOriginalMacByGuid.Remove(adapter.Id);
                        });
                    }
                }
            });

            Log("Revert pass finished.");
            RefreshAdapters();
        }

        private static bool HasNetworkAddressOverride(NetworkAdapter a)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(a.ClassRegistryPath, writable: false);
                return key?.GetValue("NetworkAddress") != null;
            }
            catch { return false; }
        }

        private void ShuffleCustomMac()
        {
            string seed = OuiMirror && SelectedAdapter != null ? SelectedAdapter.PhysicalAddress : null!;
            CustomMac = NetworkAdapter.FormatMac(MacSpoofer.GenerateRandomMac(seed));
        }

        private async Task RandomizeMachineGuidAsync()
        {
            if (!IsElevated)
            {
                Log("Not elevated — MachineGuid change is disabled.");
                return;
            }
            if (!MachineGuidAcknowledged)
            {
                Log("Tick the acknowledgement first.");
                return;
            }

            string prompt =
                "Changing MachineGuid is Windows-wide. Office activation, some app licenses, " +
                "and telemetry pairing key off this value. You can lose activation or break apps until you restore it.\n\n" +
                "The current value will be saved so you can restore it from this page. " +
                "Continue?";

            var confirm = Frontend.ShowMessageBox(prompt, MessageBoxImage.Warning,
                MessageBoxButton.YesNo, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes)
            {
                Log("MachineGuid randomize cancelled.");
                return;
            }

            await Task.Run(() =>
            {
                string? current = MachineGuidSpoofer.ReadCurrent();
                if (!string.IsNullOrEmpty(current) && string.IsNullOrEmpty(App.Settings.Prop.BanAsyncOriginalMachineGuid))
                {
                    App.Settings.Prop.BanAsyncOriginalMachineGuid = current!;
                    Log($"Saved original MachineGuid: {current}");
                }

                string newGuid = MachineGuidSpoofer.GenerateRandom();
                bool ok = MachineGuidSpoofer.Apply(newGuid, Log);
                if (ok)
                    Log("MachineGuid randomized. Some apps may need a relaunch to notice.");
            });

            OnPropertyChanged(nameof(CurrentMachineGuid));
            OnPropertyChanged(nameof(HasMachineGuidBackup));
            OnPropertyChanged(nameof(MachineGuidBackupText));
        }

        private async Task RestoreMachineGuidAsync()
        {
            if (!IsElevated)
            {
                Log("Not elevated — MachineGuid restore is disabled.");
                return;
            }

            string original = App.Settings.Prop.BanAsyncOriginalMachineGuid;
            if (string.IsNullOrEmpty(original))
            {
                Frontend.ShowMessageBox(
                    "No original MachineGuid is stored. There's nothing to restore from here.",
                    MessageBoxImage.Information);
                return;
            }

            await Task.Run(() =>
            {
                bool ok = MachineGuidSpoofer.Apply(original, Log);
                if (ok)
                {
                    App.Settings.Prop.BanAsyncOriginalMachineGuid = "";
                    Log("Restored MachineGuid to its original value.");
                }
            });

            OnPropertyChanged(nameof(CurrentMachineGuid));
            OnPropertyChanged(nameof(HasMachineGuidBackup));
            OnPropertyChanged(nameof(MachineGuidBackupText));
        }

        private static bool CheckElevated()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        // Re-launch the current BeastStrap exe with the "runas" verb so Windows pops a
        // UAC prompt. The new elevated process opens the menu directly (-menu) and the
        // current one exits, so the user can go straight to BanAsync with full privileges
        // instead of being told to "close BeastStrap and right-click the exe yourself".
        private void RelaunchAsAdmin()
        {
            const string LOG_IDENT = "BanAsyncViewModel::RelaunchAsAdmin";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Paths.Process,
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = "-menu",
                };
                Process.Start(psi);
                App.Logger.WriteLine(LOG_IDENT, "Spawned elevated instance, terminating current process.");
                App.Terminate();
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // 1223 = ERROR_CANCELLED. User dismissed the UAC prompt — stay on the
                // current non-elevated page instead of exiting on their behalf.
                App.Logger.WriteLine(LOG_IDENT, "UAC prompt was cancelled — staying on current process.");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                Frontend.ShowMessageBox(
                    $"Couldn't relaunch as administrator.\n\n" +
                    $"Reason: {ex.GetType().Name}: {ex.Message}\n\n" +
                    "Close BeastStrap and right-click the exe → 'Run as administrator' instead.",
                    MessageBoxImage.Warning);
            }
        }
    }
}
