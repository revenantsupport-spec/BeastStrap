using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

using BeastStrap.Models.APIs;
using BeastStrap.Models.Persistable;
using BeastStrap.Utility;

namespace BeastStrap.UI.ViewModels.Dialogs
{
    public class AddVersionProfileViewModel : NotifyPropertyChangedViewModel
    {
        private const string LOG_IDENT = "AddVersionProfileViewModel";

        public event EventHandler<VersionProfile?>? CloseRequested;

        // Two creation paths: from a WEAO executor dropdown (which fills in name, hash,
        // and logo URL automatically) OR a manual name + hash entry with optional verify.
        public ObservableCollection<WeaoExploit> Exploits { get; } = new();

        private bool _isLoadingExploits;
        public bool IsLoadingExploits
        {
            get => _isLoadingExploits;
            private set { _isLoadingExploits = value; OnPropertyChanged(nameof(IsLoadingExploits)); OnPropertyChanged(nameof(IsNotLoadingExploits)); }
        }
        public bool IsNotLoadingExploits => !_isLoadingExploits;

        private WeaoExploit? _selectedExploit;
        public WeaoExploit? SelectedExploit
        {
            get => _selectedExploit;
            set
            {
                _selectedExploit = value;
                OnPropertyChanged(nameof(SelectedExploit));
                if (value != null && VersionGuidValidator.IsWellFormed(value.RbxVersion))
                {
                    // Pre-fill the manual fields so the user can review/edit before OK.
                    ProfileName = value.Title;
                    VersionHash = value.RbxVersion;
                    _executorLogoUrl = value.Slug?.Logo;
                    _executorTitle = value.Title;
                    SetStatus($"Will create '{value.Title}' pinned to {value.RbxVersion}.", null);
                }
            }
        }

        private string _profileName = "";
        public string ProfileName
        {
            get => _profileName;
            set { _profileName = value ?? ""; OnPropertyChanged(nameof(ProfileName)); }
        }

        private string _versionHash = "";
        public string VersionHash
        {
            get => _versionHash;
            set { _versionHash = (value ?? "").Trim(); OnPropertyChanged(nameof(VersionHash)); }
        }

        private string? _executorTitle;
        private string? _executorLogoUrl;

        private string _statusMessage = "";
        public string StatusMessage
        {
            get => _statusMessage;
            private set { _statusMessage = value; OnPropertyChanged(nameof(StatusMessage)); OnPropertyChanged(nameof(HasStatus)); }
        }
        public bool HasStatus => !string.IsNullOrEmpty(_statusMessage);

        private bool _isVerifying;
        public bool IsVerifying
        {
            get => _isVerifying;
            private set { _isVerifying = value; OnPropertyChanged(nameof(IsVerifying)); OnPropertyChanged(nameof(IsNotVerifying)); }
        }
        public bool IsNotVerifying => !_isVerifying;

        public ICommand VerifyCommand { get; }
        public ICommand RefreshExploitsCommand { get; }
        public ICommand CreateCommand { get; }
        public ICommand CancelCommand { get; }

        public AddVersionProfileViewModel()
        {
            VerifyCommand = new AsyncRelayCommand(VerifyAsync);
            RefreshExploitsCommand = new AsyncRelayCommand(LoadExploitsAsync);
            CreateCommand = new RelayCommand(Create);
            CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, null));

            _ = LoadExploitsAsync();
        }

        private async Task LoadExploitsAsync()
        {
            if (IsLoadingExploits) return;
            IsLoadingExploits = true;
            try
            {
                var result = await WeaoClient.GetWindowsExploitsAsync();
                Exploits.Clear();
                if (result.Success)
                {
                    foreach (var e in result.Exploits)
                        Exploits.Add(e);
                }
                else
                {
                    SetStatus($"Couldn't load executor list: {result.Error}", warning: true);
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::LoadExploits", ex);
                SetStatus($"Couldn't load executor list ({ex.GetType().Name}).", warning: true);
            }
            finally
            {
                IsLoadingExploits = false;
            }
        }

        private async Task VerifyAsync()
        {
            if (IsVerifying) return;
            if (string.IsNullOrWhiteSpace(VersionHash))
            {
                SetStatus("Enter a version hash first.", warning: true);
                return;
            }
            if (!VersionGuidValidator.IsWellFormed(VersionHash))
            {
                SetStatus("Hash format is invalid (expected version-xxxxxxxxxxxxxxxx).", warning: true);
                return;
            }

            IsVerifying = true;
            try
            {
                SetStatus($"Checking Roblox CDN for {VersionHash}…", null);
                var details = await RobloxDeploymentClient.InspectAsync(VersionHash);
                if (details.NetworkError)
                    SetStatus("Couldn't reach Roblox CDN — check your connection and retry.", warning: true);
                else if (!details.Exists)
                    SetStatus("Not found on CDN. The build may have been purged.", warning: true);
                else
                    SetStatus($"Verified: {details.Hash} exists on CDN.", null);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Verify", ex);
                SetStatus($"Verify failed ({ex.GetType().Name}: {ex.Message}).", warning: true);
            }
            finally
            {
                IsVerifying = false;
            }
        }

        private void Create()
        {
            if (string.IsNullOrWhiteSpace(ProfileName))
            {
                SetStatus("Give the profile a name.", warning: true);
                return;
            }
            if (string.IsNullOrWhiteSpace(VersionHash))
            {
                SetStatus("Enter a version hash, or pick an executor above.", warning: true);
                return;
            }
            if (!VersionGuidValidator.IsWellFormed(VersionHash))
            {
                SetStatus("Hash format is invalid (expected version-xxxxxxxxxxxxxxxx).", warning: true);
                return;
            }

            var profile = new VersionProfile
            {
                Name = ProfileName.Trim(),
                VersionGuid = VersionHash.Trim(),
                ExecutorTitle = _executorTitle,
                ExecutorLogoUrl = _executorLogoUrl,
                // v420.23: flag the profile as executor-tracked so the launch path
                // refreshes its VersionGuid from WEAO when the executor pushes a new
                // build. Empty for manual-hash entries (no _executorTitle means the
                // user didn't pick from the dropdown).
                ExecutorRefreshKey = _executorTitle ?? ""
            };
            CloseRequested?.Invoke(this, profile);
        }

        private void SetStatus(string message, bool? warning)
        {
            StatusMessage = message;
            _ = warning; // colour handled via XAML triggers if we add them later
        }
    }
}
