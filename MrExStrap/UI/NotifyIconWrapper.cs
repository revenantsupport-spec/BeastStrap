using BeastStrap.Integrations;
using BeastStrap.UI.Elements.About;
using BeastStrap.UI.Elements.ContextMenu;
using BeastStrap.Utility;

namespace BeastStrap.UI
{
    public class NotifyIconWrapper : IDisposable
    {
        // lol who needs properly structured mvvm and xaml when you have the absolute catastrophe that this is

        private bool _disposing = false;

        private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
        
        private readonly MenuContainer _menuContainer;
        
        private readonly Watcher _watcher;

        private ActivityWatcher? _activityWatcher => _watcher.ActivityWatcher;

        EventHandler? _alertClickHandler;

        public NotifyIconWrapper(Watcher watcher)
        {
            App.Logger.WriteLine("NotifyIconWrapper::NotifyIconWrapper", "Initializing notification area icon");

            _watcher = watcher;

            _notifyIcon = new(new System.ComponentModel.Container())
            {
                Icon = BootstrapperIconEx.GetBrandIcon(),
                Text = App.ProjectName,
                Visible = true
            };

            _notifyIcon.MouseClick += MouseClickEventHandler;

            if (_activityWatcher is not null && App.Settings.Prop.ShowServerDetails)
                _activityWatcher.OnGameJoin += OnGameJoin;

            _menuContainer = new(_watcher);
            _menuContainer.Show();
        }

        #region Context menu
        public void MouseClickEventHandler(object? sender, System.Windows.Forms.MouseEventArgs e)
        {
            if (e.Button != System.Windows.Forms.MouseButtons.Right)
                return;

            _menuContainer.Activate();
            _menuContainer.ContextMenu.IsOpen = true;
        }
        #endregion

        #region Activity handlers
        // Raised from the ActivityWatcher's log-reader thread, and it's async void, so
        // it needs two guards: (1) a catch around everything, since an unhandled exception
        // here tears the process down, and (2) ShowAlert must run on the tray's UI thread —
        // the NotifyIcon belongs to it, and after the await we're on a thread-pool thread.
        public async void OnGameJoin(object? sender, EventArgs e)
        {
            try
            {
                if (_activityWatcher is null)
                    return;

                var data = _activityWatcher.Data;

                // The server IP is already parsed out of the Roblox logs (no auth, no rate limit), so we
                // can resolve the datacenter region straight away. Best-effort — may be unavailable.
                string? region = data.MachineAddressValid
                    ? await RobloxDatacenters.ResolveRegionAsync(data.MachineAddress)
                    : null;

                string title = data.ServerType switch
                {
                    ServerType.Public => Strings.ContextMenu_ServerInformation_Notification_Title_Public,
                    ServerType.Private => Strings.ContextMenu_ServerInformation_Notification_Title_Private,
                    ServerType.Reserved => Strings.ContextMenu_ServerInformation_Notification_Title_Reserved,
                    _ => App.ProjectName
                };

                string regionText = string.IsNullOrEmpty(region) ? Strings.Common_NotAvailable : region;

                _menuContainer.Dispatcher.Invoke(() => ShowAlert(
                    title,
                    $"Region: {regionText}\nClick for more information",
                    10,
                    (_, _) => _menuContainer.ShowServerInformationWindow()
                ));
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("NotifyIconWrapper::OnGameJoin", ex);
            }
        }
        #endregion

        // we may need to create our own handler for this, because this sorta sucks
        public void ShowAlert(string caption, string message, int duration, EventHandler? clickHandler)
        {
            string id = Guid.NewGuid().ToString()[..8];

            string LOG_IDENT = $"NotifyIconWrapper::ShowAlert.{id}";

            App.Logger.WriteLine(LOG_IDENT, $"Showing alert for {duration} seconds (clickHandler={clickHandler is not null})");
            App.Logger.WriteLine(LOG_IDENT, $"{caption}: {message.Replace("\n", "\\n")}");

            _notifyIcon.BalloonTipTitle = caption;
            _notifyIcon.BalloonTipText = message;

            if (_alertClickHandler is not null)
            {
                App.Logger.WriteLine(LOG_IDENT, "Previous alert still present, erasing click handler");
                _notifyIcon.BalloonTipClicked -= _alertClickHandler;
            }

            _alertClickHandler = clickHandler;
            _notifyIcon.BalloonTipClicked += clickHandler;

            _notifyIcon.ShowBalloonTip(duration);

            Task.Run(async () =>
            {
                await Task.Delay(duration * 1000);
             
                _notifyIcon.BalloonTipClicked -= clickHandler;

                App.Logger.WriteLine(LOG_IDENT, "Duration over, erasing current click handler");

                if (_alertClickHandler == clickHandler)
                    _alertClickHandler = null;
                else
                    App.Logger.WriteLine(LOG_IDENT, "Click handler has been overridden by another alert");
            });
        }

        public void Dispose()
        {
            if (_disposing)
                return;

            _disposing = true;

            App.Logger.WriteLine("NotifyIconWrapper::Dispose", "Disposing NotifyIcon");

            _menuContainer.Dispatcher.Invoke(_menuContainer.Close);
            _notifyIcon.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}
