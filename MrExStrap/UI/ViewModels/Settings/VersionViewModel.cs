using System.Collections.ObjectModel;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;

using BeastStrap.Models.APIs;
using BeastStrap.Utility;

namespace BeastStrap.UI.ViewModels.Settings
{
    public class VersionViewModel : NotifyPropertyChangedViewModel
    {
        private static readonly Brush NeutralBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0xA8, 0x4A));
        private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xA3, 0x22));
        private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xD1, 0x3B, 0x3B));

        private string _statusMessage = "";
        private Brush _statusColor = NeutralBrush;
        private string _liveHash = "";
        private string _liveVersion = "";
        private string _liveReleasedText = "";
        private bool _isFetchingLive;
        private bool _isVerifying;
        private bool _isLoadingExploits;
        private WeaoExploit? _selectedExploit;

        public bool UseCustomVersion
        {
            get => App.Settings.Prop.UseCustomVersion;
            set
            {
                App.Settings.Prop.UseCustomVersion = value;
                OnPropertyChanged(nameof(UseCustomVersion));
                OnPropertyChanged(nameof(ShowLiveUpdateBanner));
                OnPropertyChanged(nameof(ShowResetAction));
                OnPropertyChanged(nameof(ShowDowngradeActiveBanner));
                RefreshStatusFromCurrentInput();
            }
        }

        public string CustomVersionGuid
        {
            get => App.Settings.Prop.CustomVersionGuid;
            set
            {
                App.Settings.Prop.CustomVersionGuid = (value ?? "").Trim();
                OnPropertyChanged(nameof(CustomVersionGuid));
                OnPropertyChanged(nameof(ShowLiveUpdateBanner));
                RefreshStatusFromCurrentInput();
            }
        }

        public string LiveHash
        {
            get => _liveHash;
            private set
            {
                _liveHash = value;
                OnPropertyChanged(nameof(LiveHash));
                OnPropertyChanged(nameof(HasLiveHash));
                OnPropertyChanged(nameof(ShowLiveUpdateBanner));
            }
        }

        public string LiveVersion
        {
            get => _liveVersion;
            private set { _liveVersion = value; OnPropertyChanged(nameof(LiveVersion)); OnPropertyChanged(nameof(HasLiveMeta)); }
        }

        public string LiveReleasedText
        {
            get => _liveReleasedText;
            private set { _liveReleasedText = value; OnPropertyChanged(nameof(LiveReleasedText)); OnPropertyChanged(nameof(HasLiveMeta)); }
        }

        public bool HasLiveHash => VersionGuidValidator.IsWellFormed(_liveHash);

        public bool HasLiveMeta => !string.IsNullOrEmpty(_liveVersion) || !string.IsNullOrEmpty(_liveReleasedText);

        public bool IsFetchingLive
        {
            get => _isFetchingLive;
            private set { _isFetchingLive = value; OnPropertyChanged(nameof(IsFetchingLive)); OnPropertyChanged(nameof(IsNotFetchingLive)); }
        }

        public bool IsNotFetchingLive => !_isFetchingLive;

        public bool IsVerifying
        {
            get => _isVerifying;
            private set { _isVerifying = value; OnPropertyChanged(nameof(IsVerifying)); OnPropertyChanged(nameof(IsNotVerifying)); }
        }

        public bool IsNotVerifying => !_isVerifying;

        public ObservableCollection<WeaoExploit> Exploits { get; } = new();

        public WeaoExploit? SelectedExploit
        {
            get => _selectedExploit;
            set
            {
                _selectedExploit = value;
                OnPropertyChanged(nameof(SelectedExploit));

                if (value != null && VersionGuidValidator.IsWellFormed(value.RbxVersion))
                {
                    CustomVersionGuid = value.RbxVersion;
                    UseCustomVersion = true;
                    RememberHash(value.RbxVersion);
                    SyncDowngradePinToVersionsManager(value.RbxVersion, value.Title, value.Title, value.Slug?.Logo);

                    string upToDate = value.UpdateStatus ? "up-to-date" : "OUT OF DATE";
                    string detection = value.Detected ? "DETECTED" : "undetected";
                    string cost = value.Free ? "free" : "paid";
                    SetStatus(
                        $"Matched {value.Title} v{value.Version} ({cost}, {upToDate}, {detection}). Pinned to {value.RbxVersion}.",
                        value.UpdateStatus ? OkBrush : WarnBrush);
                }
            }
        }

        public bool IsLoadingExploits
        {
            get => _isLoadingExploits;
            private set { _isLoadingExploits = value; OnPropertyChanged(nameof(IsLoadingExploits)); OnPropertyChanged(nameof(IsNotLoadingExploits)); }
        }

        public bool IsNotLoadingExploits => !_isLoadingExploits;

        public string StatusMessage
        {
            get => _statusMessage;
            private set { _statusMessage = value; OnPropertyChanged(nameof(StatusMessage)); }
        }

        public Brush StatusColor
        {
            get => _statusColor;
            private set { _statusColor = value; OnPropertyChanged(nameof(StatusColor)); OnPropertyChanged(nameof(ShowResetAction)); }
        }

        // Inline "Reset to latest" appears next to the status only when the pinned hash is the
        // problem (bad format, purged from CDN). Transient issues like network errors don't show
        // it because retrying is the right action, not resetting.
        public bool ShowResetAction => ReferenceEquals(_statusColor, ErrorBrush) && UseCustomVersion;

        public ICommand FetchLiveCommand { get; }
        public ICommand PinLiveCommand { get; }
        public ICommand CopyLiveHashCommand { get; }
        public ICommand VerifyCommand { get; }
        public ICommand ResetCommand { get; }
        public ICommand RefreshExploitsCommand { get; }

        public ObservableCollection<string> RecentHashes { get; } = new();

        private string? _selectedRecentHash;
        public string? SelectedRecentHash
        {
            get => _selectedRecentHash;
            set
            {
                _selectedRecentHash = value;
                OnPropertyChanged(nameof(SelectedRecentHash));

                if (!string.IsNullOrWhiteSpace(value))
                {
                    CustomVersionGuid = value;
                    UseCustomVersion = true;
                    SyncDowngradePinToVersionsManager(value, "Recent pin", executorTitle: null, logoUrl: null);
                    SetStatus($"Loaded {value} from recent list. Click Verify to confirm it still exists.", NeutralBrush);
                }
            }
        }

        public bool HasRecentHashes => RecentHashes.Count > 0;

        public bool ShowLiveUpdateBanner =>
            HasLiveHash
            && App.Settings.Prop.UseCustomVersion
            && !string.IsNullOrEmpty(App.Settings.Prop.CustomVersionGuid)
            && LiveHash != App.Settings.Prop.CustomVersionGuid
            && LiveHash != App.State.Prop.DismissedLiveHash;

        // Always-visible banner shown whenever a custom version is in effect, so users can
        // get back to auto-updating LIVE in one click without scrolling to the version-hash
        // textbox at the bottom. Hidden once Reset is invoked (UseCustomVersion flips false).
        public bool ShowDowngradeActiveBanner => App.Settings.Prop.UseCustomVersion;

        public ICommand UpdatePinToLiveCommand { get; }
        public ICommand DismissLiveBannerCommand { get; }
        public ICommand CopyDiagnosticsCommand { get; }

        public VersionViewModel()
        {
            FetchLiveCommand = new AsyncRelayCommand(FetchLiveAsync);
            PinLiveCommand = new RelayCommand(PinLive);
            CopyLiveHashCommand = new RelayCommand(CopyLiveHash);
            VerifyCommand = new AsyncRelayCommand(VerifyAsync);
            ResetCommand = new RelayCommand(Reset);
            RefreshExploitsCommand = new AsyncRelayCommand(LoadExploitsAsync);
            UpdatePinToLiveCommand = new RelayCommand(UpdatePinToLive);
            DismissLiveBannerCommand = new RelayCommand(DismissLiveBanner);
            CopyDiagnosticsCommand = new AsyncRelayCommand(CopyDiagnosticsAsync);

            LoadRecentHashesFromState();
            RefreshStatusFromCurrentInput();

            // Fire-and-forget: auto-detect LIVE hash + populate exploit list on page open.
            _ = FetchLiveAsync();
            _ = LoadExploitsAsync();
        }

        private void LoadRecentHashesFromState()
        {
            RecentHashes.Clear();
            foreach (var h in App.State.Prop.RecentCustomVersionHashes)
            {
                if (VersionGuidValidator.IsWellFormed(h))
                    RecentHashes.Add(h);
            }
            OnPropertyChanged(nameof(HasRecentHashes));
        }

        private void RememberHash(string hash)
        {
            if (!VersionGuidValidator.IsWellFormed(hash))
                return;

            // Dedupe: remove any existing copy, then push to front. Cap at 10.
            var list = App.State.Prop.RecentCustomVersionHashes;
            list.RemoveAll(h => string.Equals(h, hash, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, hash);
            while (list.Count > 10)
                list.RemoveAt(list.Count - 1);

            LoadRecentHashesFromState();
        }

        private void UpdatePinToLive()
        {
            if (!HasLiveHash)
                return;

            CustomVersionGuid = LiveHash;
            UseCustomVersion = true;
            RememberHash(LiveHash);
            SyncDowngradePinToVersionsManager(LiveHash, "Pinned LIVE", executorTitle: null, logoUrl: null);
            OnPropertyChanged(nameof(ShowLiveUpdateBanner));
            SetStatus($"Pin updated to current LIVE: {LiveHash}.", OkBrush);
        }

        private void DismissLiveBanner()
        {
            App.State.Prop.DismissedLiveHash = LiveHash ?? "";
            OnPropertyChanged(nameof(ShowLiveUpdateBanner));
        }

        private async Task FetchLiveAsync()
        {
            if (IsFetchingLive)
                return;

            IsFetchingLive = true;
            try
            {
                var info = await RobloxDeploymentClient.GetCurrentLiveAsync();
                if (info is null)
                {
                    LiveHash = "";
                    LiveVersion = "";
                    LiveReleasedText = "";
                    SetStatus("Couldn't reach Roblox to fetch the current LIVE version. Check your connection and click Refresh.", WarnBrush);
                    return;
                }

                LiveHash = info.Hash;
                LiveVersion = string.IsNullOrEmpty(info.Version) ? "" : $"Roblox v{info.Version}";

                // Record the first time this installation observes a hash as LIVE. The CDN
                // Last-Modified timestamp reflects package upload, which can be many hours
                // before the LIVE pointer actually flips — first-seen gives a locally-true
                // "when did Roblox switch?" signal.
                var firstSeenDict = App.State.Prop.LiveHashFirstSeenUtc;
                if (!firstSeenDict.ContainsKey(info.Hash))
                {
                    firstSeenDict[info.Hash] = DateTime.UtcNow;
                    TrimFirstSeenDict(firstSeenDict, keepMostRecent: 30);
                }
                DateTime? firstSeenUtc = firstSeenDict.TryGetValue(info.Hash, out var ts) ? ts : null;

                LiveReleasedText = FormatReleased(info.LastModifiedUtc, firstSeenUtc);

                if (!UseCustomVersion)
                    SetStatus($"Current LIVE version: {LiveHash}. Click \"Pin this version\" or pick an executor below to lock it in.", NeutralBrush);
                else
                    RefreshStatusFromCurrentInput();
            }
            catch (Exception ex)
            {
                // Belt-and-suspenders: the Utility layer catches everything today, but don't let
                // the spinner hang forever if that ever regresses.
                App.Logger.WriteException("VersionViewModel::FetchLiveAsync", ex);
                LiveHash = "";
                LiveVersion = "";
                LiveReleasedText = "";
                SetStatus("Something went wrong fetching the LIVE version. Click Refresh to try again.", ErrorBrush);
            }
            finally
            {
                IsFetchingLive = false;
            }
        }

        private string _exploitsLoadError = "";
        public string ExploitsLoadError
        {
            get => _exploitsLoadError;
            private set
            {
                _exploitsLoadError = value;
                OnPropertyChanged(nameof(ExploitsLoadError));
                OnPropertyChanged(nameof(HasExploitsLoadError));
            }
        }

        public bool HasExploitsLoadError => !string.IsNullOrEmpty(_exploitsLoadError);

        private string _exploitsSourceNote = "";
        public string ExploitsSourceNote
        {
            get => _exploitsSourceNote;
            private set
            {
                _exploitsSourceNote = value;
                OnPropertyChanged(nameof(ExploitsSourceNote));
                OnPropertyChanged(nameof(HasExploitsSourceNote));
            }
        }

        public bool HasExploitsSourceNote => !string.IsNullOrEmpty(_exploitsSourceNote);

        // Downgrade-tab toggle: prefer the robloxscripts.com mirror over weao.xyz as the primary
        // source. Flipping it re-pulls the list from the newly-preferred source right away.
        public bool PreferRobloxScriptsApi
        {
            get => App.Settings.Prop.PreferRobloxScriptsApi;
            set
            {
                if (App.Settings.Prop.PreferRobloxScriptsApi == value)
                    return;

                App.Settings.Prop.PreferRobloxScriptsApi = value;
                OnPropertyChanged(nameof(PreferRobloxScriptsApi));
                _ = LoadExploitsAsync();
            }
        }

        private async Task LoadExploitsAsync()
        {
            if (IsLoadingExploits)
                return;

            IsLoadingExploits = true;
            ExploitsLoadError = "";
            ExploitsSourceNote = "";
            try
            {
                var result = await WeaoClient.GetWindowsExploitsAsync();
                Exploits.Clear();

                if (!result.Success)
                {
                    ExploitsLoadError = result.Error!;
                    return;
                }

                foreach (var e in result.Exploits)
                    Exploits.Add(e);

                // If we ended up on the NON-preferred source, a fallback happened — tell the user
                // which source was down and which one supplied the list (and credit it).
                var preferredSource = App.Settings.Prop.PreferRobloxScriptsApi ? WeaoSource.Mirror : WeaoSource.Weao;
                if (result.Source != WeaoSource.None && result.Source != preferredSource)
                {
                    ExploitsSourceNote = result.Source == WeaoSource.Mirror
                        ? "weao.xyz wasn't reachable, so this list loaded from the robloxscripts.com backup."
                        : "robloxscripts.com wasn't reachable, so this list loaded from weao.xyz instead.";
                }

                if (Exploits.Count == 0)
                {
                    ExploitsLoadError =
                        "weao.xyz returned no Windows executors. This is unusual — the list may be temporarily empty " +
                        "or every entry was hidden. You can still paste a version hash manually below.";
                }
            }
            catch (Exception ex)
            {
                // WeaoClient catches its own failures. This only fires on a surprise regression.
                App.Logger.WriteException("VersionViewModel::LoadExploitsAsync", ex);
                Exploits.Clear();
                ExploitsLoadError = $"Unexpected error loading executor list ({ex.GetType().Name}). " +
                                    "Check the log file for details. You can still paste a version hash manually below.";
            }
            finally
            {
                IsLoadingExploits = false;
            }
        }

        private void PinLive()
        {
            if (!HasLiveHash)
                return;

            CustomVersionGuid = LiveHash;
            UseCustomVersion = true;
            RememberHash(LiveHash);
            SyncDowngradePinToVersionsManager(LiveHash, "Pinned LIVE", executorTitle: null, logoUrl: null);
            SetStatus($"Pinned to current LIVE: {LiveHash}.", OkBrush);
        }

        // v420.19+: when the Downgrading tab pins a version, mirror it into Versions Manager
        // as an auto-named profile and set it active. Keeps the two tabs from getting out of
        // sync (and from the launch path quietly preferring one over the other).
        private static void SyncDowngradePinToVersionsManager(string versionGuid, string defaultName, string? executorTitle, string? logoUrl)
        {
            try
            {
                if (!VersionGuidValidator.IsWellFormed(versionGuid))
                    return;

                var existing = App.Settings.Prop.VersionProfiles
                    .FirstOrDefault(p => string.Equals(p.VersionGuid, versionGuid, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    existing = new VersionProfile
                    {
                        Name = string.IsNullOrEmpty(executorTitle) ? $"{defaultName} ({versionGuid.Substring(0, 12)}…)" : executorTitle!,
                        VersionGuid = versionGuid,
                        ExecutorTitle = executorTitle,
                        ExecutorLogoUrl = logoUrl
                    };
                    App.Settings.Prop.VersionProfiles.Add(existing);
                }
                else if (!string.IsNullOrEmpty(logoUrl) && string.IsNullOrEmpty(existing.ExecutorLogoUrl))
                {
                    existing.ExecutorLogoUrl = logoUrl;
                    existing.ExecutorTitle ??= executorTitle;
                }

                App.Settings.Prop.ActiveVersionProfileId = existing.Id;
                App.Logger.WriteLine("VersionViewModel::SyncDowngradePinToVersionsManager", $"Mirrored Downgrading pin into profile '{existing.Name}' ({existing.Id})");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("VersionViewModel::SyncDowngradePinToVersionsManager", ex);
            }
        }

        private static void ResetVersionsManagerToLive()
        {
            try
            {
                var live = App.Settings.Prop.VersionProfiles.FirstOrDefault(p => p.Id == App.LiveBuiltInProfileId);
                if (live != null)
                    App.Settings.Prop.ActiveVersionProfileId = live.Id;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("VersionViewModel::ResetVersionsManagerToLive", ex);
            }
        }

        private void CopyLiveHash()
        {
            if (!HasLiveHash)
                return;

            try
            {
                // SetDataObject with copy=true flushes to the clipboard so the value survives
                // after this process exits. Retrying on COMException covers the case where
                // another app has the clipboard open for an instant.
                Clipboard.SetDataObject(LiveHash, true);
                SetStatus($"Copied {LiveHash} to clipboard.", OkBrush);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("VersionViewModel::CopyLiveHash", ex);
                SetStatus("Couldn't copy — clipboard was busy. Try again.", WarnBrush);
            }
        }

        private async Task VerifyAsync()
        {
            if (IsVerifying)
                return;

            if (string.IsNullOrWhiteSpace(CustomVersionGuid))
            {
                SetStatus("Enter a version hash first.", WarnBrush);
                return;
            }

            if (!VersionGuidValidator.IsWellFormed(CustomVersionGuid))
            {
                SetStatus("Hash format is invalid (expected version-xxxxxxxxxxxxxxxx).", ErrorBrush);
                return;
            }

            IsVerifying = true;
            try
            {
                SetStatus($"Checking Roblox CDN for {CustomVersionGuid}...", NeutralBrush);
                var details = await RobloxDeploymentClient.InspectAsync(CustomVersionGuid);

                if (details.NetworkError)
                {
                    SetStatus("Couldn't reach the Roblox CDN to verify. Check your connection and try again.", WarnBrush);
                    return;
                }

                if (!details.Exists)
                {
                    SetStatus("Not found on CDN. This build may have been purged by Roblox, or the hash is wrong.", ErrorBrush);
                    return;
                }

                string summary = $"Verified: {details.Hash} exists on CDN";
                if (details.PackageCount > 0)
                    summary += $" ({details.PackageCount} packages, {FormatBytes(details.TotalCompressedBytes)} to download)";

                DateTime? verifiedFirstSeen = App.State.Prop.LiveHashFirstSeenUtc.TryGetValue(details.Hash, out var vts) ? vts : (DateTime?)null;
                string when = FormatReleased(details.LastModifiedUtc, verifiedFirstSeen);
                if (!string.IsNullOrEmpty(when))
                    summary += ". " + when;
                else
                    summary += ".";
                SetStatus(summary, OkBrush);

                RememberHash(details.Hash);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("VersionViewModel::VerifyAsync", ex);
                SetStatus("Something went wrong verifying that hash. Check the log file for details.", ErrorBrush);
            }
            finally
            {
                IsVerifying = false;
            }
        }

        private void Reset()
        {
            SelectedExploit = null;
            UseCustomVersion = false;
            CustomVersionGuid = "";
            ResetVersionsManagerToLive();
        }

        private void RefreshStatusFromCurrentInput()
        {
            if (!UseCustomVersion)
            {
                if (HasLiveHash)
                    SetStatus($"Latest LIVE build ({LiveHash}) will be used on next launch.", NeutralBrush);
                else
                    SetStatus("Latest LIVE build will be used on next launch.", NeutralBrush);
                return;
            }

            if (string.IsNullOrWhiteSpace(CustomVersionGuid))
            {
                SetStatus("Paste a version hash, pick an executor, or click \"Pin this version\".", NeutralBrush);
                return;
            }

            if (!VersionGuidValidator.IsWellFormed(CustomVersionGuid))
            {
                SetStatus("Hash format is invalid (expected version-xxxxxxxxxxxxxxxx).", ErrorBrush);
                return;
            }

            SetStatus($"Pinned to {CustomVersionGuid}. Click Verify to confirm it exists on CDN.", WarnBrush);
        }

        private void SetStatus(string text, Brush color)
        {
            StatusMessage = text;
            StatusColor = color;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "unknown size";
            string[] units = { "B", "KB", "MB", "GB" };
            double n = bytes;
            int u = 0;
            while (n >= 1024 && u < units.Length - 1) { n /= 1024; u++; }
            return $"{n:0.#} {units[u]}";
        }

        // Compose the "when" line. We show two distinct facts because they mean different things:
        //   * CDN uploaded = Last-Modified on the package manifest. Roblox often pre-stages packages
        //                    on the CDN hours before flipping the LIVE pointer, so this lags reality.
        //   * First seen LIVE = the first moment this installation observed this hash as the LIVE
        //                       pointer. This is an accurate "when did Roblox switch?" for you.
        private static string FormatReleased(DateTime? lastModifiedUtc, DateTime? firstSeenLiveUtc)
        {
            var parts = new List<string>();

            if (lastModifiedUtc.HasValue)
            {
                DateTime utc = lastModifiedUtc.Value;
                DateTime local = utc.ToLocalTime();
                parts.Add($"CDN uploaded {local:yyyy-MM-dd HH:mm} UTC{local:zzz} ({FormatRelative(DateTime.UtcNow - utc)})");
            }

            if (firstSeenLiveUtc.HasValue)
            {
                TimeSpan age = DateTime.UtcNow - firstSeenLiveUtc.Value;
                // Emphasise fresh observations — if we saw this LIVE less than 15 min ago it almost
                // certainly means Roblox just pushed it as the production build.
                if (age.TotalMinutes < 15)
                    parts.Add($"First seen LIVE {FormatRelative(age)} — likely a fresh production push");
                else
                    parts.Add($"First seen LIVE {FormatRelative(age)}");
            }

            return string.Join("  ·  ", parts);
        }

        private static string FormatRelative(TimeSpan age)
        {
            if (age.TotalSeconds < 0) return "in the future?";
            if (age.TotalMinutes < 1) return "just now";
            if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes} min ago";
            if (age.TotalHours < 24) return $"{(int)age.TotalHours}h ago";
            if (age.TotalDays < 30) return $"{(int)age.TotalDays}d ago";
            if (age.TotalDays < 365) return $"~{(int)(age.TotalDays / 30)}mo ago";
            return $"~{(int)(age.TotalDays / 365)}y ago";
        }

        private static void TrimFirstSeenDict(Dictionary<string, DateTime> dict, int keepMostRecent)
        {
            if (dict.Count <= keepMostRecent) return;

            var drop = dict.OrderByDescending(kv => kv.Value)
                           .Skip(keepMostRecent)
                           .Select(kv => kv.Key)
                           .ToList();

            foreach (var k in drop)
                dict.Remove(k);
        }

        // Bundles static environment info + a fresh probe of weao.xyz into a single block
        // the user can paste into a bug report. The probe re-runs the same request the
        // dropdown does so the report carries the exact failure mode they're seeing right now,
        // not a cached or stale one.
        private async Task CopyDiagnosticsAsync()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{App.ProjectName} diagnostic info");
            sb.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine("=====================================");
            sb.AppendLine($"App version      : v{App.Version}");
            try
            {
                sb.AppendLine($"Build commit     : {App.BuildMetadata.CommitHash} ({App.BuildMetadata.CommitRef})");
                sb.AppendLine($"Build timestamp  : {App.BuildMetadata.Timestamp:yyyy-MM-dd HH:mm:ss} UTC");
            }
            catch { sb.AppendLine("Build commit     : (unavailable)"); }
            sb.AppendLine($"OS               : {Environment.OSVersion}");
            sb.AppendLine($"OS architecture  : {RuntimeInformation.OSArchitecture}");
            sb.AppendLine($"Process arch     : {RuntimeInformation.ProcessArchitecture}");
            sb.AppendLine($"Runtime          : {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"Locale           : {CultureInfo.CurrentCulture.Name}");
            sb.AppendLine($"UI culture       : {CultureInfo.CurrentUICulture.Name}");
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                sb.AppendLine($"Elevated         : {new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator)}");
            }
            catch { sb.AppendLine("Elevated         : (unknown)"); }
            sb.AppendLine();
            sb.AppendLine("WEAO state");
            sb.AppendLine($"  Loaded count   : {Exploits.Count}");
            sb.AppendLine($"  Last error     : {(string.IsNullOrEmpty(ExploitsLoadError) ? "(none)" : ExploitsLoadError)}");
            sb.AppendLine($"  Live hash      : {(HasLiveHash ? LiveHash : "(not fetched)")}");
            sb.AppendLine();
            sb.AppendLine("WEAO live probe (re-run just now)");

            string endpoint = "https://weao.xyz/api/status/exploits";
            sb.AppendLine($"  Endpoint       : {endpoint}");

            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
                req.Headers.UserAgent.ParseAdd("WEAO-3PService");
                using var resp = await App.HttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                stopwatch.Stop();
                sb.AppendLine($"  Status         : {(int)resp.StatusCode} {resp.ReasonPhrase}");
                sb.AppendLine($"  Elapsed        : {stopwatch.ElapsedMilliseconds} ms");
                if (resp.Headers.TryGetValues("cf-ray", out var cfRay))
                    sb.AppendLine($"  cf-ray         : {string.Join(",", cfRay)}");
                if (resp.Headers.TryGetValues("server", out var server))
                    sb.AppendLine($"  server         : {string.Join(",", server)}");
                if (resp.Content.Headers.ContentLength is long len)
                    sb.AppendLine($"  Content-Length : {len}");
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                sb.AppendLine($"  Status         : (exception after {stopwatch.ElapsedMilliseconds} ms)");
                sb.AppendLine($"  Error class    : {ex.GetType().FullName}");
                sb.AppendLine($"  Error message  : {ex.Message}");
                if (ex.InnerException != null)
                {
                    sb.AppendLine($"  Inner class    : {ex.InnerException.GetType().FullName}");
                    sb.AppendLine($"  Inner message  : {ex.InnerException.Message}");
                }
            }

            string text = sb.ToString();
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                SetStatus("Diagnostic info copied to clipboard. Paste it into your bug report.", OkBrush);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("VersionViewModel::CopyDiagnosticsAsync", ex);
                SetStatus("Couldn't copy — clipboard was busy. Try again.", WarnBrush);
            }
        }
    }
}
