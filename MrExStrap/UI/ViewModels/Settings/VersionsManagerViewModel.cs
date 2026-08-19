using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;

using BeastStrap.Models.Persistable;
using BeastStrap.Utility;

namespace BeastStrap.UI.ViewModels.Settings
{
    // ViewModel for the Versions Manager tab. Holds the tile list (one item per
    // VersionProfile), exposes commands for activating / editing / deleting /
    // adding profiles, and renders the executor logo for each tile via the
    // ExecutorLogoCache.
    public class VersionsManagerViewModel : NotifyPropertyChangedViewModel
    {
        private const string LOG_IDENT = "VersionsManagerViewModel";

        public ObservableCollection<VersionProfileTile> Tiles { get; } = new();

        public ICommand ActivateCommand => new RelayCommand<string>(Activate);
        public ICommand DeleteCommand => new RelayCommand<string>(DeleteProfile);
        public ICommand AddProfileCommand => new RelayCommand(AddProfile);
        public ICommand OpenVersionsFolderCommand => new RelayCommand(OpenVersionsFolder);
        // v420.27: explicit "redirect the install-target junction to this profile" action.
        // Distinct from Activate (which only changes which profile gets launched next).
        // Use case: you're about to run an executor installer (e.g. Synapse Z) that
        // writes files into Versions\version-<hash>\ — click this on the destination
        // profile first so the installer's files land in that profile, not whichever
        // one you last launched.
        public ICommand SetAsInstallTargetCommand => new RelayCommand<string>(SetAsInstallTarget);
        // v420.23: Refresh now pulls latest versions from WEAO for executor-tracked
        // profiles before rebuilding the tile list. 5s budget when the user explicitly
        // clicked Refresh (longer than the 3s budget used on the launch hot-path).
        public ICommand RefreshCommand => new AsyncRelayCommand(RefreshAsync);
        public ICommand ExecutorCheckerCommand => new RelayCommand(OpenExecutorChecker);

        private bool _isRefreshing;
        public bool IsRefreshing
        {
            get => _isRefreshing;
            private set { _isRefreshing = value; OnPropertyChanged(nameof(IsRefreshing)); OnPropertyChanged(nameof(IsNotRefreshing)); }
        }
        public bool IsNotRefreshing => !_isRefreshing;

        private string _refreshStatus = "";
        public string RefreshStatus
        {
            get => _refreshStatus;
            private set { _refreshStatus = value; OnPropertyChanged(nameof(RefreshStatus)); OnPropertyChanged(nameof(HasRefreshStatus)); }
        }
        public bool HasRefreshStatus => !string.IsNullOrEmpty(_refreshStatus);

        private string _activeName = "";
        public string ActiveName
        {
            get => _activeName;
            private set { _activeName = value; OnPropertyChanged(nameof(ActiveName)); }
        }

        private string _activeHash = "";
        public string ActiveHash
        {
            get => _activeHash;
            private set { _activeHash = value; OnPropertyChanged(nameof(ActiveHash)); }
        }

        private string _diskUsageText = "";
        public string DiskUsageText
        {
            get => _diskUsageText;
            private set { _diskUsageText = value; OnPropertyChanged(nameof(DiskUsageText)); }
        }

        // Banner: visible when the legacy single-pin is on AND a non-built-in Versions
        // Manager profile is also active. Tells the user the new tab wins.
        public Visibility SinglePinConflictVisibility =>
            App.Settings.Prop.UseCustomVersion
            && App.Settings.Prop.VersionProfiles
                .Any(p => p.Id == App.Settings.Prop.ActiveVersionProfileId && !p.IsBuiltIn && !string.IsNullOrEmpty(p.VersionGuid))
                ? Visibility.Visible : Visibility.Collapsed;

        public VersionsManagerViewModel()
        {
            RebuildTiles();
        }

        private async Task RefreshAsync()
        {
            if (IsRefreshing) return;

            bool anyExecutorTracked = App.Settings.Prop.VersionProfiles
                .Any(p => !string.IsNullOrWhiteSpace(p.ExecutorRefreshKey));

            if (!anyExecutorTracked)
            {
                // Pure UI refresh — no executor profiles to query for.
                RebuildTiles();
                RefreshStatus = "";
                return;
            }

            IsRefreshing = true;
            RefreshStatus = "Refreshing executor versions…";
            try
            {
                await ExecutorProfileRefresher.RefreshAllAsync(TimeSpan.FromSeconds(5));
                RefreshStatus = $"Refreshed at {DateTime.Now:HH:mm}.";
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Refresh", ex);
                RefreshStatus = $"Refresh failed: {ex.Message}";
            }
            finally
            {
                IsRefreshing = false;
                RebuildTiles();
            }
        }

        // Bumped on every rebuild so a disk scan started for an older tile set
        // can't write its stale results over a newer one. UI thread only.
        private int _scanGeneration;

        private void RebuildTiles()
        {
            Tiles.Clear();
            string activeId = App.Settings.Prop.ActiveVersionProfileId;

            foreach (var profile in App.Settings.Prop.VersionProfiles)
            {
                var tile = new VersionProfileTile(profile, profile.Id == activeId);
                Tiles.Add(tile);
                // Fire-and-forget the logo fetch.
                _ = tile.LoadLogoAsync();
            }

            RefreshActiveSummary();
            // Disk usage and junction resolution walk every install's file tree —
            // far too slow for the UI thread (multi-GB pinned installs freeze the
            // tab for seconds). Scan in the background and fill the tiles in after.
            _ = ScanTilesAsync(Tiles.ToArray(), ++_scanGeneration);
            OnPropertyChanged(nameof(SinglePinConflictVisibility));
        }

        private async Task ScanTilesAsync(VersionProfileTile[] tiles, int generation)
        {
            try
            {
                // Snapshot on the UI thread. The tiles don't carry InstalledVersionGuid, and reading
                // the profile list from the background scan would race the settings page editing it.
                var installedByProfileId = App.Settings.Prop.VersionProfiles
                    .GroupBy(p => p.Id)
                    .ToDictionary(g => g.Key, g => g.First().InstalledVersionGuid ?? "");

                var results = await Task.Run(() =>
                {
                    var scans = new (long Bytes, bool IsInstallTarget)[tiles.Length];

                    for (int i = 0; i < tiles.Length; i++)
                    {
                        var tile = tiles[i];

                        // Per PROFILE, not per version guid. Only the active profile's install sits
                        // at Versions\version-<hash>\ — every other one is parked under its own id,
                        // so measuring by guid reported 0 bytes for all of them, and 0 for the
                        // built-in LIVE profile too since its VersionGuid is deliberately empty.
                        // (The old dedupe-by-guid cache is gone with it: two profiles pinned to the
                        // same build own separate copies on disk and genuinely both count.)
                        long bytes = VersionsDiskUsage.GetProfileUsageBytes(
                            tile.Id, tile.VersionGuid, installedByProfileId.GetValueOrDefault(tile.Id));

                        scans[i] = (bytes, VersionProfileTile.ResolveIsInstallTarget(tile.VersionGuid, tile.Id));
                    }

                    return scans;
                });

                // Back on the UI thread (started from it). Drop stale scans.
                if (generation != _scanGeneration)
                    return;

                long total = 0;
                for (int i = 0; i < tiles.Length; i++)
                {
                    tiles[i].ApplyScan(results[i].Bytes, results[i].IsInstallTarget);
                    total += results[i].Bytes;
                }

                int profileCount = tiles.Length;
                DiskUsageText = $"Disk usage: {VersionsDiskUsage.FormatBytes(total)} across {profileCount} profile{(profileCount == 1 ? "" : "s")}";
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::ScanTiles", ex);
                if (generation == _scanGeneration)
                    DiskUsageText = "Disk usage: (unavailable)";
            }
        }

        private void RefreshActiveSummary()
        {
            var active = App.Settings.Prop.VersionProfiles
                .FirstOrDefault(p => p.Id == App.Settings.Prop.ActiveVersionProfileId);
            if (active == null)
            {
                ActiveName = "(none)";
                ActiveHash = "";
                return;
            }
            ActiveName = active.Name;
            ActiveHash = string.IsNullOrEmpty(active.VersionGuid) ? "(current LIVE)" : active.VersionGuid;
        }

        private void Activate(string? id)
        {
            if (string.IsNullOrEmpty(id)) return;
            var profile = App.Settings.Prop.VersionProfiles.FirstOrDefault(p => p.Id == id);
            if (profile == null) return;

            App.Settings.Prop.ActiveVersionProfileId = profile.Id;

            // Mirror into the legacy single-pin so any code path that still reads
            // CustomVersionGuid (e.g. log statements, third-party hooks) sees a
            // consistent value. For the built-in LIVE profile we clear the pin.
            if (profile.IsBuiltIn || string.IsNullOrEmpty(profile.VersionGuid))
            {
                App.Settings.Prop.UseCustomVersion = false;
                App.Settings.Prop.CustomVersionGuid = "";
            }
            else
            {
                App.Settings.Prop.UseCustomVersion = true;
                App.Settings.Prop.CustomVersionGuid = profile.VersionGuid;
            }

            App.Settings.Save();
            App.Logger.WriteLine(LOG_IDENT, $"Activated profile '{profile.Name}' ({profile.Id})");

            foreach (var tile in Tiles)
                tile.IsActive = tile.Id == id;

            RefreshActiveSummary();
            OnPropertyChanged(nameof(SinglePinConflictVisibility));
        }

        private void DeleteProfile(string? id)
        {
            if (string.IsNullOrEmpty(id)) return;
            var profile = App.Settings.Prop.VersionProfiles.FirstOrDefault(p => p.Id == id);
            if (profile == null || profile.IsBuiltIn) return;

            var confirm = Frontend.ShowMessageBox(
                $"Delete profile '{profile.Name}'?\n\nThe pinned Roblox install for this profile will be removed on next launch cleanup unless another profile references the same hash.",
                MessageBoxImage.Warning,
                MessageBoxButton.YesNo,
                MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes) return;

            App.Settings.Prop.VersionProfiles.Remove(profile);
            BeastStrap.Utility.FastFlagProfiles.Delete(id);

            // If we just deleted the active profile, fall back to the built-in LIVE one.
            if (App.Settings.Prop.ActiveVersionProfileId == id)
            {
                App.Settings.Prop.ActiveVersionProfileId = App.LiveBuiltInProfileId;
                App.Settings.Prop.UseCustomVersion = false;
                App.Settings.Prop.CustomVersionGuid = "";
            }

            App.Settings.Save();
            App.Logger.WriteLine(LOG_IDENT, $"Deleted profile '{profile.Name}' ({id})");
            RebuildTiles();
        }

        private void SetAsInstallTarget(string? id)
        {
            if (string.IsNullOrEmpty(id)) return;
            var profile = App.Settings.Prop.VersionProfiles.FirstOrDefault(p => p.Id == id);
            if (profile == null || string.IsNullOrEmpty(profile.VersionGuid)) return;

            var confirm = Frontend.ShowMessageBox(
                $"Make '{profile.Name}' the install target?\n\n" +
                "When you run an executor installer that writes files into the standard Roblox folder, the files will land in this profile.\n\n" +
                $"Moves it to Versions\\{profile.VersionGuid} and parks whichever profile is there now.",
                MessageBoxImage.Information,
                MessageBoxButton.YesNo,
                MessageBoxResult.Yes);
            if (confirm != MessageBoxResult.Yes) return;

            // Being the install target now just means being the unparked profile, so this is the
            // same park-and-rename the bootstrapper does at launch.
            string resolved = BeastStrap.Utility.VersionProfileLayout.EnsureActive(profile, profile.VersionGuid);

            if (BeastStrap.Utility.VersionProfileLayout.IsInstallTarget(profile.Id))
            {
                App.Logger.WriteLine(LOG_IDENT, $"Set '{profile.Name}' as install target: {resolved}");
                RebuildTiles();
            }
            else
            {
                Frontend.ShowMessageBox(
                    "Couldn't move that profile into place — if Roblox is running, close it and try again. Check the log for details.",
                    MessageBoxImage.Error);
            }
        }

        private void AddProfile()
        {
            var dialog = new UI.Elements.Dialogs.AddVersionProfileDialog();
            dialog.ShowDialog();
            if (dialog.CreatedProfile == null) return;

            App.Settings.Prop.VersionProfiles.Add(dialog.CreatedProfile);
            App.Settings.Save();
            App.Logger.WriteLine(LOG_IDENT, $"Added profile '{dialog.CreatedProfile.Name}' ({dialog.CreatedProfile.Id})");
            RebuildTiles();
        }

        private void OpenVersionsFolder()
        {
            try
            {
                if (!string.IsNullOrEmpty(Paths.Versions))
                {
                    Directory.CreateDirectory(Paths.Versions);
                    Process.Start(new ProcessStartInfo { FileName = Paths.Versions, UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::OpenVersionsFolder", ex);
            }
        }

        private void OpenExecutorChecker()
        {
            var dialog = new UI.Elements.Dialogs.ExecutorCheckerDialog { Owner = System.Windows.Application.Current?.MainWindow };
            dialog.ShowDialog();
        }
    }

    // One row per profile. Wraps the VersionProfile for binding plus the loaded
    // logo image / placeholder letter.
    public class VersionProfileTile : INotifyPropertyChanged
    {
        public string Id { get; }
        public string Name { get; }
        public string VersionGuid { get; }
        public string DisplayHash { get; }
        public string LetterPlaceholder { get; }
        public bool IsBuiltIn { get; }
        public bool CanDelete => !IsBuiltIn;
        public string? LogoUrl { get; }

        // Filled in by the view model's background disk scan — walking the install
        // tree is too slow for tile construction on the UI thread.
        private string _diskUsageText = "";
        public string DiskUsageText
        {
            get => _diskUsageText;
            private set { _diskUsageText = value; OnPropertyChanged(nameof(DiskUsageText)); }
        }

        // v420.23: surfaced on tiles for executor-tracked profiles. The badge tells
        // the user the version will auto-update from WEAO; the timestamp says when
        // that last happened so they can tell if the refresh is stuck.
        public bool IsExecutorTracked { get; }
        public string LastRefreshText { get; }

        // v420.27: "Set as install target" button gating + badge.
        // CanSetAsInstallTarget is false for the built-in Latest LIVE profile and
        // any other empty-VersionGuid case — there's no fixed version-<hash>\ name
        // to junction at. IsInstallTarget is set in the ctor based on whether the
        // junction at Versions\<this profile's VersionGuid>\ currently resolves to
        // this profile's per-profile dir.
        public bool CanSetAsInstallTarget => !string.IsNullOrEmpty(VersionGuid);

        private bool _isInstallTarget;
        public bool IsInstallTarget
        {
            get => _isInstallTarget;
            private set { _isInstallTarget = value; OnPropertyChanged(nameof(IsInstallTarget)); }
        }

        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            set { _isActive = value; OnPropertyChanged(nameof(IsActive)); }
        }

        private ImageSource? _logo;
        public ImageSource? Logo
        {
            get => _logo;
            private set { _logo = value; OnPropertyChanged(nameof(Logo)); OnPropertyChanged(nameof(HasLogo)); OnPropertyChanged(nameof(NoLogo)); }
        }
        public bool HasLogo => _logo != null;
        public bool NoLogo => _logo == null;

        public VersionProfileTile(VersionProfile profile, bool isActive)
        {
            Id = profile.Id;
            Name = profile.Name;
            VersionGuid = profile.VersionGuid;
            DisplayHash = string.IsNullOrEmpty(profile.VersionGuid) ? "(current LIVE)" : profile.VersionGuid;
            LetterPlaceholder = string.IsNullOrEmpty(profile.Name) ? "?" : profile.Name.Substring(0, 1).ToUpperInvariant();
            IsBuiltIn = profile.IsBuiltIn;
            LogoUrl = profile.ExecutorLogoUrl;
            _isActive = isActive;

            IsExecutorTracked = !string.IsNullOrWhiteSpace(profile.ExecutorRefreshKey);
            LastRefreshText = IsExecutorTracked
                ? FormatLastRefresh(profile.LastExecutorRefreshUtc)
                : "";

            // DiskUsageText and IsInstallTarget arrive later via ApplyScan — both need
            // filesystem walks that would freeze the UI thread if done here.

            // The built-in "Latest LIVE" profile has no executor logo to fetch, so instead of a bare
            // letter placeholder show the Roblox app icon.
            if (IsBuiltIn)
                _logo = LoadRobloxIcon();
        }

        // Called on the UI thread with results from the view model's background disk scan.
        public void ApplyScan(long diskUsageBytes, bool isInstallTarget)
        {
            DiskUsageText = diskUsageBytes > 0 ? VersionsDiskUsage.FormatBytes(diskUsageBytes) : "";
            IsInstallTarget = isInstallTarget;
        }

        private static ImageSource? LoadRobloxIcon()
        {
            try
            {
                using var stream = BeastStrap.Resource.GetStream("Icon2022.ico");
                var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                frame.Freeze();
                return frame;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("VersionProfileTile::LoadRobloxIcon", ex);
                return null;
            }
        }

        // True when this profile owns the unparked install at Versions\version-<hash>\, which is
        // what the tile's "Install target" badge means. Used to resolve a junction target; with the
        // junction gone (see Utility/VersionProfileLayout.cs) being unparked IS being the target,
        // since that's the folder an executor installer writing to the standard Roblox path lands
        // in. Now a State lookup rather than disk I/O, so it no longer needs the background scan.
        internal static bool ResolveIsInstallTarget(string versionGuid, string profileId)
            => BeastStrap.Utility.VersionProfileLayout.IsInstallTarget(profileId);

        private static string FormatLastRefresh(DateTime? lastUtc)
        {
            if (lastUtc is null)
                return "Auto-updates from WEAO";

            TimeSpan ago = DateTime.UtcNow - lastUtc.Value;
            string relative;
            if (ago.TotalSeconds < 60)
                relative = "just now";
            else if (ago.TotalMinutes < 60)
                relative = $"{(int)ago.TotalMinutes} min ago";
            else if (ago.TotalHours < 24)
                relative = $"{(int)ago.TotalHours} h ago";
            else if (ago.TotalDays < 14)
                relative = $"{(int)ago.TotalDays} d ago";
            else
                relative = lastUtc.Value.ToLocalTime().ToString("yyyy-MM-dd");

            return $"WEAO sync: {relative}";
        }

        public async Task LoadLogoAsync()
        {
            if (string.IsNullOrWhiteSpace(LogoUrl)) return;
            try
            {
                string? path = await ExecutorLogoCache.GetLogoAsync(LogoUrl);
                if (string.IsNullOrEmpty(path)) return;

                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher == null) return;

                dispatcher.Invoke(() =>
                {
                    try
                    {
                        var img = new BitmapImage();
                        img.BeginInit();
                        img.CacheOption = BitmapCacheOption.OnLoad;
                        img.UriSource = new Uri(path);
                        img.EndInit();
                        img.Freeze();
                        Logo = img;
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteException("VersionProfileTile::LoadLogoAsync::Bitmap", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("VersionProfileTile::LoadLogoAsync", ex);
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
