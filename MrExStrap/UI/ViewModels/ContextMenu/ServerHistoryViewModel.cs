using System.Windows.Input;
using BeastStrap.Integrations;
using CommunityToolkit.Mvvm.Input;

namespace BeastStrap.UI.ViewModels.ContextMenu
{
    internal class ServerHistoryViewModel : NotifyPropertyChangedViewModel
    {
        private readonly ActivityWatcher _activityWatcher;

        public List<ActivityData>? GameHistory { get; private set; }

        public GenericTriState LoadState { get; private set; } = GenericTriState.Unknown;

        public string Error { get; private set; } = String.Empty;

        public ICommand CloseWindowCommand => new RelayCommand(RequestClose);
        
        public EventHandler? RequestCloseEvent;

        private readonly EventHandler _onGameLeave;

        public ServerHistoryViewModel(ActivityWatcher activityWatcher)
        {
            _activityWatcher = activityWatcher;

            // Kept in a field so it can actually be detached. The subscription used to be an
            // inline lambda with no matching -=, and ActivityWatcher lives for the whole session —
            // so every closed history window stayed rooted through this handler and reloaded
            // itself on every disconnect, all of them racing to FetchBulk at once.
            _onGameLeave = (_, _) => LoadData();
            _activityWatcher.OnGameLeave += _onGameLeave;

            LoadData();
        }

        /// <summary>Detaches from the activity watcher. Called when the window closes.</summary>
        public void Detach() => _activityWatcher.OnGameLeave -= _onGameLeave;

        private async void LoadData()
        {
            // async void off a background event: nothing above us catches, so a throw here would
            // take the process down.
            try
            {
                await LoadDataAsync();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("ServerHistoryViewModel::LoadData", ex);
            }
        }

        private async Task LoadDataAsync()
        {
            LoadState = GenericTriState.Unknown;
            OnPropertyChanged(nameof(LoadState));

            // Snapshot once, up front. History is a bare List<ActivityData> that the log-reader
            // thread does History.Insert(0, ...) into, and this method used to hold a DEFERRED
            // LINQ query over it and re-enumerate that query after an await spanning two HTTP
            // round trips — an insert in that window throws InvalidOperationException ("collection
            // was modified") out of an async void.
            var history = _activityWatcher.SnapshotHistory();

            var entries = history.Where(x => x.UniverseDetails is null).ToList();

            if (entries.Count > 0)
            {
                string universeIds = String.Join(',', entries.Select(x => x.UniverseId).Distinct());

                try
                {
                    await UniverseDetails.FetchBulk(universeIds);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException("ServerHistoryViewModel::LoadData", ex);
                    
                    Error = ex.Message;
                    OnPropertyChanged(nameof(Error));

                    LoadState = GenericTriState.Failed;
                    OnPropertyChanged(nameof(LoadState));

                    return;
                }

                foreach (var entry in entries)
                    entry.UniverseDetails = UniverseDetails.LoadFromCache(entry.UniverseId);
            }

            GameHistory = new(history);

            var consolidatedJobIds = new List<ActivityData>();

            // consolidate activity entries from in-universe teleports
            // the time left of the latest activity gets moved to the root activity
            // the job id of the latest public server activity gets moved to the root activity
            foreach (var entry in history)
            {
                if (entry.RootActivity is not null)
                {
                    if (entry.RootActivity.TimeLeft < entry.TimeLeft)
                        entry.RootActivity.TimeLeft = entry.TimeLeft;

                    if (entry.ServerType == ServerType.Public && !consolidatedJobIds.Contains(entry))
                    {
                        entry.RootActivity.JobId = entry.JobId;
                        consolidatedJobIds.Add(entry);
                    }

                    GameHistory.Remove(entry);
                }
            }

            OnPropertyChanged(nameof(GameHistory));

            LoadState = GenericTriState.Successful;
            OnPropertyChanged(nameof(LoadState));
        }

        private void RequestClose() => RequestCloseEvent?.Invoke(this, EventArgs.Empty);
    }
}
