using System.Windows.Threading;
using WinForms = System.Windows.Forms;

using BeastStrap.Utility;

namespace BeastStrap.UI
{
    // v420.28: persistent system-tray launcher. Lives in the Windows
    // notification area when BeastStrap was launched with -tray (set up
    // by StartupRegistration when EnableTrayLauncher is on).
    //
    // Right-click menu lets the user quick-switch the active Versions
    // Manager profile, open the launch menu, open settings, or exit the
    // tray. Background timer fires UpdateMonitor every 30 minutes so the
    // user gets toasts about LIVE / executor updates without having to
    // open the launcher first.
    //
    // Owns its own NotifyIcon directly (rather than going through
    // NotifyIconWrapper, which is tied to a Watcher + ActivityWatcher
    // and assumes a Roblox PID is being tracked).
    public class TrayLauncher : IDisposable
    {
        private const string LOG_IDENT = "TrayLauncher";
        private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromMinutes(30);

        private readonly WinForms.NotifyIcon _notifyIcon;
        private readonly WinForms.ContextMenuStrip _menu;
        private readonly DispatcherTimer _updateTimer;
        private bool _disposed;

        public TrayLauncher()
        {
            App.Logger.WriteLine(LOG_IDENT, "Initializing system tray launcher");

            _menu = new WinForms.ContextMenuStrip();

            _notifyIcon = new WinForms.NotifyIcon(new System.ComponentModel.Container())
            {
                Icon = BootstrapperIconEx.GetBrandIcon(),
                Visible = true,
                ContextMenuStrip = _menu,
            };
            _notifyIcon.MouseDoubleClick += (_, e) =>
            {
                if (e.Button == WinForms.MouseButtons.Left)
                    OpenLaunchMenu();
            };

            // Settings.json is written by the *separate* settings UI process,
            // not by the tray launcher itself. Reload our in-memory copy each
            // time the user opens the menu so profile labels / active-profile
            // marker stay current without needing a file-system watcher.
            _menu.Opening += (_, _) =>
            {
                try { App.Settings.Load(); }
                catch (Exception ex) { App.Logger.WriteException(LOG_IDENT + "::Reload", ex); }
                RebuildMenu();
                UpdateTooltip();
            };

            RebuildMenu();
            UpdateTooltip();

            _updateTimer = new DispatcherTimer { Interval = UpdateCheckInterval };
            _updateTimer.Tick += (_, _) =>
            {
                _ = RunUpdateCheckAsync();
            };
            _updateTimer.Start();

            // Fire one immediate check too so the user gets feedback within
            // a few seconds of the tray starting (rather than waiting 30 min).
            _ = RunUpdateCheckAsync();

            App.Logger.WriteLine(LOG_IDENT, "Tray launcher ready");
        }

        /// <summary>
        /// Reloads settings, then runs the update checks.
        /// </summary>
        /// <remarks>
        /// The tray can sit at the login screen for days. Without the reload it decides whether to
        /// toast using whatever the settings said at boot, so a user who turns the notifications
        /// off keeps getting them until they happen to open this menu. The writes on the other
        /// side of this go through JsonManager.SaveMerged for the same reason.
        /// </remarks>
        private static async Task RunUpdateCheckAsync()
        {
            // Fire-and-forget from a timer tick, so nothing above can catch anything this throws.
            try
            {
                App.Settings.Load(alertFailure: false);
                await UpdateMonitor.CheckAllAsync();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::RunUpdateCheck", ex);
            }
        }

        private void UpdateTooltip()
        {
            var active = GetActiveProfile();
            string label = active is null
                ? App.ProjectName
                : $"{App.ProjectName} — {active.Name}";
            // NotifyIcon.Text caps at ~63 characters on older Windows versions.
            if (label.Length > 63)
                label = label.Substring(0, 60) + "...";
            _notifyIcon.Text = label;
        }

        private void RebuildMenu()
        {
            _menu.Items.Clear();

            var active = GetActiveProfile();
            string activeLabel = active is null
                ? "Open launch menu"
                : $"Launch ({active.Name})";

            var launchItem = new WinForms.ToolStripMenuItem(activeLabel);
            launchItem.Click += (_, _) => OpenLaunchMenu();
            _menu.Items.Add(launchItem);

            var switchMenu = new WinForms.ToolStripMenuItem("Switch profile");
            foreach (var profile in App.Settings.Prop.VersionProfiles)
            {
                var item = new WinForms.ToolStripMenuItem(profile.Name)
                {
                    Checked = profile.Id == App.Settings.Prop.ActiveVersionProfileId,
                };
                string capturedId = profile.Id;
                item.Click += (_, _) => ActivateProfile(capturedId);
                switchMenu.DropDownItems.Add(item);
            }
            switchMenu.Enabled = switchMenu.DropDownItems.Count > 0;
            _menu.Items.Add(switchMenu);

            _menu.Items.Add(new WinForms.ToolStripSeparator());

            var settingsItem = new WinForms.ToolStripMenuItem("Open settings");
            settingsItem.Click += (_, _) => OpenSettings();
            _menu.Items.Add(settingsItem);

            _menu.Items.Add(new WinForms.ToolStripSeparator());

            var exitItem = new WinForms.ToolStripMenuItem("Exit tray launcher");
            exitItem.Click += (_, _) =>
            {
                App.Logger.WriteLine(LOG_IDENT, "Exit clicked");
                App.Terminate();
            };
            _menu.Items.Add(exitItem);
        }

        private Models.Persistable.VersionProfile? GetActiveProfile()
        {
            string activeId = App.Settings.Prop.ActiveVersionProfileId;
            if (string.IsNullOrEmpty(activeId))
                return null;
            return App.Settings.Prop.VersionProfiles.FirstOrDefault(p => p.Id == activeId);
        }

        private void ActivateProfile(string id)
        {
            try
            {
                var profile = App.Settings.Prop.VersionProfiles.FirstOrDefault(p => p.Id == id);
                if (profile is null)
                    return;

                // Merged: this is the tray process, which can have been running for days. The
                // reload on menu-open covers most of it, but the settings window is a separate
                // process and can save at any moment, including between opening this menu and
                // clicking an entry.
                bool useLive = profile.IsBuiltIn || string.IsNullOrEmpty(profile.VersionGuid);

                App.Settings.SaveMerged(s =>
                {
                    s.ActiveVersionProfileId = profile.Id;
                    s.UseCustomVersion = !useLive;
                    s.CustomVersionGuid = useLive ? "" : profile.VersionGuid;
                });
                App.Logger.WriteLine(LOG_IDENT, $"Activated profile '{profile.Name}' ({id})");

                RebuildMenu();
                UpdateTooltip();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::ActivateProfile", ex);
            }
        }

        private void OpenLaunchMenu()
        {
            try
            {
                Process.Start(Paths.Process, "-menu");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::OpenLaunchMenu", ex);
            }
        }

        private void OpenSettings()
        {
            try
            {
                Process.Start(Paths.Process, "-settings");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::OpenSettings", ex);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _updateTimer.Stop(); } catch { }
            try { _notifyIcon.Visible = false; } catch { }
            try { _notifyIcon.Dispose(); } catch { }
            try { _menu.Dispose(); } catch { }

            GC.SuppressFinalize(this);
        }
    }
}
