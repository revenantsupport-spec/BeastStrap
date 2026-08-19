using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

using BeastStrap.AppData;
using BeastStrap.Utility;
using BeastStrap.Utility.Accounts;

namespace BeastStrap.UI.ViewModels.Settings
{
    // Server Browser tab. Lists an experience's public servers (players / ping / fps), lets you jump
    // straight into a chosen one, and — best-effort — tells you which Roblox datacenter each server is
    // in. See ServerBrowserClient for why region detection is opt-in and per-server.
    public class ServerBrowserViewModel : NotifyPropertyChangedViewModel
    {
        private const string LOG_IDENT = "ServerBrowserViewModel";
        private const string AllRegions = "All regions";

        // place id of the currently-listed servers (set on a successful load), used for joins + lookups.
        private long _placeId;
        private string? _cursor;
        private bool _detectingAll;

        public ServerBrowserViewModel()
        {
            ServersView = CollectionViewSource.GetDefaultView(Servers);
            ServersView.Filter = FilterRow;
            RegionFilters.Add(AllRegions);
            ApplySort();
        }

        public ObservableCollection<ServerRowViewModel> Servers { get; } = new();

        // Filtered view bound by the list — the region dropdown narrows it down.
        public ICollectionView ServersView { get; }

        public ObservableCollection<string> RegionFilters { get; } = new();

        private string _selectedRegionFilter = AllRegions;
        public string SelectedRegionFilter
        {
            get => _selectedRegionFilter;
            set
            {
                _selectedRegionFilter = string.IsNullOrEmpty(value) ? AllRegions : value;
                OnPropertyChanged(nameof(SelectedRegionFilter));
                ServersView.Refresh();
            }
        }

        public IReadOnlyList<string> SortOptions { get; } = new[] { "Lowest ping", "Fewest players", "Most players", "Highest FPS" };

        private string _selectedSort = "Lowest ping";
        public string SelectedSort
        {
            get => _selectedSort;
            set
            {
                _selectedSort = string.IsNullOrEmpty(value) ? "Lowest ping" : value;
                OnPropertyChanged(nameof(SelectedSort));
                ApplySort();
            }
        }

        // Fetch emptiest-first when the user wants fewest players, so the loaded page actually holds the
        // quiet servers; otherwise the standard fullest-first page. (Ping/FPS just sort what's loaded.)
        private int FetchSortOrder => _selectedSort == "Fewest players" ? 1 : 2;

        private void ApplySort()
        {
            ServersView.SortDescriptions.Clear();
            switch (_selectedSort)
            {
                case "Fewest players":
                    ServersView.SortDescriptions.Add(new SortDescription(nameof(ServerRowViewModel.Playing), ListSortDirection.Ascending));
                    break;
                case "Most players":
                    ServersView.SortDescriptions.Add(new SortDescription(nameof(ServerRowViewModel.Playing), ListSortDirection.Descending));
                    break;
                case "Highest FPS":
                    ServersView.SortDescriptions.Add(new SortDescription(nameof(ServerRowViewModel.Fps), ListSortDirection.Descending));
                    break;
                default: // Lowest ping
                    ServersView.SortDescriptions.Add(new SortDescription(nameof(ServerRowViewModel.PingSortKey), ListSortDirection.Ascending));
                    break;
            }
        }

        private string _placeIdInput = "";
        public string PlaceIdInput
        {
            get => _placeIdInput;
            set { _placeIdInput = value; OnPropertyChanged(nameof(PlaceIdInput)); }
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                _isBusy = value;
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(NotBusy));
            }
        }
        public bool NotBusy => !_isBusy;

        private string _status = "";
        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(HasStatus)); }
        }
        public bool HasStatus => !string.IsNullOrEmpty(_status);

        public bool HasServers => Servers.Count > 0;
        public bool CanLoadMore => !string.IsNullOrEmpty(_cursor);

        public ICommand LoadCommand => new AsyncRelayCommand(() => LoadServersAsync(false));
        public ICommand LoadMoreCommand => new AsyncRelayCommand(() => LoadServersAsync(true));
        public ICommand DetectRegionCommand => new AsyncRelayCommand<ServerRowViewModel?>(DetectRegionAsync);
        public ICommand DetectAllRegionsCommand => new AsyncRelayCommand(DetectAllRegionsAsync);
        public ICommand JoinCommand => new RelayCommand<ServerRowViewModel?>(JoinServer);
        public ICommand JoinEmptiestCommand => new AsyncRelayCommand(JoinEmptiestAsync);

        // Accepts a raw place id, or a roblox.com game link (…/games/1234567/Name), and pulls the id out.
        private static long ParsePlaceId(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return 0;

            input = input.Trim();

            if (long.TryParse(input, out long direct) && direct > 0)
                return direct;

            var match = Regex.Match(input, @"games/(\d{3,19})", RegexOptions.IgnoreCase);
            if (match.Success && long.TryParse(match.Groups[1].Value, out long fromUrl))
                return fromUrl;

            // last resort: any long run of digits in the string
            var digits = Regex.Match(input, @"\d{3,19}");
            return digits.Success && long.TryParse(digits.Value, out long any) ? any : 0;
        }

        private async Task LoadServersAsync(bool append)
        {
            if (IsBusy)
                return;

            long placeId = append ? _placeId : ParsePlaceId(PlaceIdInput);
            if (placeId <= 0)
            {
                Status = "Enter a Roblox place ID or paste a game link first.";
                return;
            }

            IsBusy = true;
            Status = append ? "Loading more servers…" : "Loading servers…";

            try
            {
                var resp = await ServerBrowserClient.FetchServersAsync(placeId, append ? _cursor : null, FetchSortOrder);

                if (resp is null)
                {
                    Status = "Couldn't load servers — Roblox's API didn't answer (it may be rate-limiting you). Try again in a moment.";
                    return;
                }

                if (!append)
                {
                    Servers.Clear();
                    ResetRegionFilters();
                }

                foreach (var server in resp.Data)
                    Servers.Add(new ServerRowViewModel(placeId, server));

                _placeId = placeId;
                _cursor = resp.NextPageCursor;

                OnPropertyChanged(nameof(HasServers));
                OnPropertyChanged(nameof(CanLoadMore));

                Status = Servers.Count == 0
                    ? "No public servers are listed for this experience right now."
                    : $"Showing {Servers.Count} server{(Servers.Count == 1 ? "" : "s")}.";
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                Status = $"Couldn't load servers — {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task DetectRegionAsync(ServerRowViewModel? row)
        {
            if (row is null || row.IsDetecting || row.HasRegion)
                return;

            if (!ServerBrowserClient.CanResolveRegions)
            {
                Status = "Region detection needs at least one saved account (add one on the Multi Instance tab). BeastStrap uses it to ask Roblox where a server is.";
                return;
            }

            row.IsDetecting = true;
            try
            {
                await ResolveRowRegionAsync(row);
                if (row.HasRegion)
                    Status = $"Server is in {row.Region}.";
                else
                    Status = "Couldn't detect that server's region — Roblox declined the lookup (rate limit or expired cookie). This is best-effort, try again shortly.";
            }
            finally
            {
                row.IsDetecting = false;
                RebuildRegionFilters();
            }
        }

        // Resolve the loaded page's regions, throttled and capped so we don't hammer the gamejoin API
        // (which would risk the saved account). Whatever we skip is reported, never hidden.
        private async Task DetectAllRegionsAsync()
        {
            if (_detectingAll || IsBusy)
                return;

            if (!ServerBrowserClient.CanResolveRegions)
            {
                Status = "Region detection needs at least one saved account (add one on the Multi Instance tab).";
                return;
            }

            const int cap = 15;
            var pending = Servers.Where(s => !s.HasRegion && !s.IsDetecting).ToList();
            int skipped = Math.Max(0, pending.Count - cap);
            pending = pending.Take(cap).ToList();

            if (pending.Count == 0)
            {
                Status = "Every loaded server already has a region.";
                return;
            }

            _detectingAll = true;
            try
            {
                int done = 0;
                foreach (var row in pending)
                {
                    row.IsDetecting = true;
                    try { await ResolveRowRegionAsync(row); }
                    finally { row.IsDetecting = false; }

                    done++;
                    Status = $"Detecting regions… {done}/{pending.Count}";
                    RebuildRegionFilters();

                    // Throttle: spacing the gamejoin calls keeps Roblox from rate-limiting (or flagging)
                    // the account. Skip the wait after the final one.
                    if (done < pending.Count)
                        await Task.Delay(700);
                }

                Status = skipped > 0
                    ? $"Detected {done} region{(done == 1 ? "" : "s")}. Stopped at {cap} to avoid rate-limiting your account — {skipped} left undetected. Use the per-row Detect for the rest."
                    : $"Detected {done} region{(done == 1 ? "" : "s")}.";
            }
            finally
            {
                _detectingAll = false;
                RebuildRegionFilters();
            }
        }

        private static async Task ResolveRowRegionAsync(ServerRowViewModel row)
        {
            string? ip = await ServerBrowserClient.ResolveServerIpAsync(row.PlaceId, row.JobId);
            if (string.IsNullOrEmpty(ip))
                return;

            string? region = await RobloxDatacenters.ResolveRegionAsync(ip);
            row.Region = string.IsNullOrEmpty(region) ? "Unknown" : region;
        }

        private void JoinServer(ServerRowViewModel? row)
        {
            if (row is null)
                return;

            JoinInstance(row.PlaceId, row.JobId);
        }

        // Same path the "rejoin server" feature uses: hand Roblox the deep link and let it join the
        // specific instance as your currently signed-in account. It inherits the FastFlags and mods
        // BeastStrap already wrote to the installed client.
        private void JoinInstance(long placeId, string jobId)
        {
            try
            {
                string playerPath = new RobloxPlayerData().ExecutablePath;
                string deeplink = $"roblox://experiences/start?placeId={placeId}&gameInstanceId={jobId}";

                Process.Start(playerPath, deeplink);

                string shortId = jobId.Length > 8 ? jobId[..8] : jobId;
                Status = $"Joining server {shortId}… Roblox is launching.";
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::JoinInstance", ex);
                Status = $"Couldn't launch Roblox — {ex.GetType().Name}: {ex.Message}";
            }
        }

        // Find + join the least-populated public server of the entered place, in one shot (no need to
        // Load the list first). Needs no saved account.
        private async Task JoinEmptiestAsync()
        {
            if (IsBusy)
                return;

            long placeId = ParsePlaceId(PlaceIdInput);
            if (placeId <= 0)
            {
                Status = "Enter a Roblox place ID or paste a game link first.";
                return;
            }

            IsBusy = true;
            Status = "Finding the emptiest server…";
            try
            {
                var server = await ServerBrowserClient.GetEmptiestServerAsync(placeId);
                if (server is null || string.IsNullOrEmpty(server.Id))
                {
                    Status = "Couldn't find a joinable public server right now — try again, or Load servers to browse.";
                    return;
                }

                Status = $"Joining the emptiest server ({server.Playing}/{server.MaxPlayers})…";
                JoinInstance(placeId, server.Id);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::JoinEmptiest", ex);
                Status = $"Couldn't find a server — {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private bool FilterRow(object item)
        {
            if (_selectedRegionFilter == AllRegions)
                return true;

            return item is ServerRowViewModel row && row.Region == _selectedRegionFilter;
        }

        private void ResetRegionFilters()
        {
            RegionFilters.Clear();
            RegionFilters.Add(AllRegions);
            _selectedRegionFilter = AllRegions;
            OnPropertyChanged(nameof(SelectedRegionFilter));
        }

        private void RebuildRegionFilters()
        {
            var distinct = Servers
                .Where(s => s.HasRegion)
                .Select(s => s.Region)
                .Distinct()
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            string keep = _selectedRegionFilter;

            RegionFilters.Clear();
            RegionFilters.Add(AllRegions);
            foreach (var region in distinct)
                RegionFilters.Add(region);

            if (!RegionFilters.Contains(keep))
                keep = AllRegions;

            _selectedRegionFilter = keep;
            OnPropertyChanged(nameof(SelectedRegionFilter));
            ServersView.Refresh();
        }
    }

    // One server row. Wraps the API model and adds the lazily-resolved region + its in-flight state.
    public class ServerRowViewModel : NotifyPropertyChangedViewModel
    {
        public long PlaceId { get; }
        public string JobId { get; }
        public int Playing { get; }
        public int MaxPlayers { get; }
        public int Ping { get; }
        public double Fps { get; }

        public ServerRowViewModel(long placeId, GameServer server)
        {
            PlaceId = placeId;
            JobId = server.Id;
            Playing = server.Playing;
            MaxPlayers = server.MaxPlayers;
            Ping = server.Ping;
            Fps = server.Fps;
        }

        public string PlayersText => MaxPlayers > 0 ? $"{Playing}/{MaxPlayers}" : Playing.ToString();
        public string PingText => Ping > 0 ? $"{Ping} ms" : "—";
        public string FpsText => Fps > 0 ? $"{Fps:0} FPS" : "—";
        public int PingSortKey => Ping > 0 ? Ping : int.MaxValue;

        private string _region = "";
        public string Region
        {
            get => _region;
            set
            {
                _region = value ?? "";
                OnPropertyChanged(nameof(Region));
                OnPropertyChanged(nameof(RegionDisplay));
                OnPropertyChanged(nameof(HasRegion));
                OnPropertyChanged(nameof(CanDetect));
            }
        }
        public bool HasRegion => !string.IsNullOrEmpty(_region);
        public string RegionDisplay => string.IsNullOrEmpty(_region) ? "—" : _region;

        private bool _isDetecting;
        public bool IsDetecting
        {
            get => _isDetecting;
            set
            {
                _isDetecting = value;
                OnPropertyChanged(nameof(IsDetecting));
                OnPropertyChanged(nameof(CanDetect));
            }
        }

        // Show the Detect button only when there's something to detect and nothing in flight.
        public bool CanDetect => !_isDetecting && !HasRegion;
    }
}
