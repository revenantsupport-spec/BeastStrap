using System.Collections.ObjectModel;

namespace BeastStrap.Models.Persistable
{
    public class Settings
    {
        // bloxstrap configuration
        public BootstrapperStyle BootstrapperStyle { get; set; } = BootstrapperStyle.FluentAeroDialog;
        public BootstrapperIcon BootstrapperIcon { get; set; } = BootstrapperIcon.IconBloxstrap;
        public string BootstrapperTitle { get; set; } = App.ProjectName;
        public string BootstrapperIconCustomLocation { get; set; } = "";
        public Theme Theme { get; set; } = Theme.Default;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool DeveloperMode { get; set; } = false;
        public bool CheckForUpdates { get; set; } = true;
        public bool ConfirmLaunches { get; set; } = false;
        public string Locale { get; set; } = "nil";
        public bool UseFastFlagManager { get; set; } = true;
        public bool WPFSoftwareRender { get; set; } = false;
        public bool EnableAnalytics { get; set; } = true;
        public bool BackgroundUpdatesEnabled { get; set; } = false;
        public bool DebugDisableVersionPackageCleanup { get; set; } = false;
        public string? SelectedCustomTheme { get; set; } = null;
        public WebEnvironment WebEnvironment { get; set; } = WebEnvironment.Production;

        // user UI theming (BeastStrap fork feature) — a live-editable brand palette, effect toggles
        // and a custom app icon. Applied by Utility.ThemeManager.
        public ThemePalette Palette { get; set; } = new();
        public string SelectedThemePreset { get; set; } = "Default";
        public bool EnableAurora { get; set; } = true;
        public bool EnableGlass { get; set; } = true;
        public bool EnableGlow { get; set; } = true;
        public string CustomAppIconLocation { get; set; } = "";

        // user wallpaper (BeastStrap fork feature) — a background image behind the settings pages
        // and launch menu. When empty / disabled the animated aurora is used instead.
        public bool EnableWallpaper { get; set; } = false;
        public string WallpaperLocation { get; set; } = "";
        // 0..1 how strong the wallpaper shows through behind the glass content.
        public double WallpaperOpacity { get; set; } = 0.6;

        // user GIF / animated background (BeastStrap fork feature) — a looping animated
        // background behind the settings pages and launch menu, layered on top of the
        // static wallpaper when both are set. Rendered and animated by the GifBackground
        // control; the path / visibility / opacity are published as app resources.
        public bool EnableGifWallpaper { get; set; } = false;
        public string GifWallpaperLocation { get; set; } = "";
        // 0..1 how strongly the animation shows through behind the glass content.
        public double GifWallpaperOpacity { get; set; } = 0.4;

        // integration configuration
        public bool EnableActivityTracking { get; set; } = true;
        public bool UseDiscordRichPresence { get; set; } = true;

        // Window manipulation (ported from FishyStrap): rewrite the running Roblox window —
        // custom icon (RobloxIcon / RobloxIconCustomLocation), custom title (RobloxTitle)
        // and fake borderless fullscreen (FakeBorderlessFullscreen, requires Vulkan). The
        // window handle is captured at launch and passed to the watcher, which applies these
        // when the client's window shows. Off by default.
        public bool EnableWindowManipulation { get; set; } = false;
        public RobloxIcon RobloxIcon { get; set; } = RobloxIcon.IconDefault;
        public string RobloxTitle { get; set; } = "Roblox";
        public string RobloxIconCustomLocation { get; set; } = "";
        public bool FakeBorderlessFullscreen { get; set; } = false;

        // Show the "Roblox closed unexpectedly" crash dialog when a launch looks like it
        // crashed. Off by default: the exit code can't always be read reliably, so a clean
        // Alt+F4 quit from the home screen is frequently misread as a crash and annoys users.
        // Users who want the crash dialog can turn it back on in Settings -> Integrations.
        public bool EnableCrashNotifications { get; set; } = false;
        public bool HideRPCButtons { get; set; } = true;
        public bool ShowAccountOnRichPresence { get; set; } = false;
        public bool ShowServerDetails { get; set; } = false;
        public ObservableCollection<CustomIntegration> CustomIntegrations { get; set; } = new();

        // mod preset configuration
        public bool UseDisableAppPatch { get; set; } = false;

        // Removes the client's BRDF lookup texture so lighting flattens out. Can't live in the
        // Modifications overlay because that only copies files in, and this needs one taken out —
        // see Utility/FullBright.cs.
        public bool EnableFullBright { get; set; } = false;

        // version downgrade (BeastStrap fork feature)
        public bool UseCustomVersion { get; set; } = false;
        public string CustomVersionGuid { get; set; } = "";

        // Downgrade tab "Match your executor/exploit" list source. Default OFF = weao.xyz first,
        // robloxscripts.com mirror as fallback. ON = robloxscripts.com first (for users whose
        // network/ISP blocks weao.xyz, so they skip the dead attempt), weao.xyz as the fallback.
        // Either way both are tried before giving up. See WeaoClient.
        public bool PreferRobloxScriptsApi { get; set; } = false;

        // Versions Manager (v420.19+). Multiple named profiles each pointing at a
        // Roblox version hash. ActiveVersionProfileId picks which one applies on
        // launch — the legacy UseCustomVersion / CustomVersionGuid pair above
        // remains as a fallback only for users who never touched the new tab.
        public ObservableCollection<VersionProfile> VersionProfiles { get; set; } = new();
        public string ActiveVersionProfileId { get; set; } = "";

        // v420.22+: when ON, every Roblox launch through BeastStrap pops a small
        // version-picker dialog right before the bootstrapper starts (and after the
        // VIP server picker, when that's also enabled). Saves the user a trip to
        // the Versions Manager tab when they just want to switch executor on a
        // single launch.
        public bool ShowVersionPickerOnLaunch { get; set; } = false;

        // v420.22+: companion toggle. When ON and the user picks (or has already
        // active) a profile pinned to a non-LIVE Roblox build, prompt for explicit
        // confirmation. LIVE-channel launches never prompt. Default ON because a
        // downgrade launch is a meaningful event.
        public bool ConfirmNonLiveLaunch { get; set; } = true;

        // post-launch "Channel: LIVE" toast (BeastStrap fork feature)
        public bool ShowLiveChannelToast { get; set; } = true;

        // privacy mode — truncate RobloxCookies.dat before every launch (BeastStrap fork feature)
        public bool EnablePrivacyMode { get; set; } = false;

        // v420.28+: Stream Mode hides Roblox-account info from Discord Rich Presence,
        // the place ID from the bootstrapper dialog, and rewrites the Roblox window
        // title to a generic "Roblox" string. For users who stream / record / share
        // their screen and don't want viewers to see account-identifying info.
        public bool EnableStreamMode { get; set; } = false;

        // v420.28+: persistent system tray launcher. When ON, BeastStrap registers
        // itself to start with Windows and lives in the notification area with a
        // right-click menu for quick-launching the active profile or switching
        // profiles without opening the full settings UI.
        public bool EnableTrayLauncher { get; set; } = false;

        // v420.28+: opt-in Windows balloon-tip toasts.
        // NotifyOnLiveChange: pops a toast when Roblox's LIVE channel hash changes
        //   (polled on launcher open + every 30 min when the tray launcher is on).
        // NotifyOnExecutorUpdate: pops a toast when any tracked executor profile
        //   gets a new Roblox version on WEAO.
        // Both default off so the launcher stays quiet unless the user asks for it.
        public bool NotifyOnLiveChange { get; set; } = false;
        public bool NotifyOnExecutorUpdate { get; set; } = false;

        // v420.50.1+: auto-downgrade protection. When the active Versions Manager
        // profile tracks an executor on WEAO and that executor isn't updated for the
        // newest LIVE Roblox build yet, prompt once per Roblox update to pin the
        // profile to the version the executor still supports. Default ON; the prompt
        // always asks before acting (and the pin follows back to LIVE automatically
        // once the executor catches up).
        public bool AutoDowngradeExecutors { get; set; } = true;

        // v420.29.5+: pop a toast when a newer BeastStrap release is available on
        // GitHub. Default ON so users always find out about updates even if they never
        // open the launch menu (e.g. tray-only users). Independent of the existing
        // menu-open "install now?" prompt — this is the passive heads-up.
        public bool NotifyOnAppUpdate { get; set; } = true;

        // multi-instance: hold ROBLOX_singletonMutex while clients run so they can start side
        // by side instead of closing each other (BeastStrap fork feature, see Utility.MultiInstance)
        public bool MultiInstanceEnabled { get; set; } = false;

        // Multi Instance tab: when on, account launches open to the Roblox home screen instead
        // of joining a place, so no Place ID is needed. Place ID / Job ID stay available for
        // when this is off. Default off (join a game, the original behavior).
        public bool MultiInstanceLaunchToHome { get; set; } = false;

        // v420.30.3+: Froststrap-style memory saver. When ON, the watcher closes Roblox's
        // RobloxCrashHandler.exe background process while the game runs, freeing the memory it
        // holds. Default off. (With this on we can't use the crash handler to detect crashes.)
        public bool CloseRobloxCrashHandler { get; set; } = false;

        // Multi-instance RAM reducer (BeastStrap fork feature, see Utility.MultiInstanceRamReducer).
        // When ON, multi-instance launches layer lean FastFlags (FPS cap / low textures / no grass)
        // over the active profile's client settings, and the watcher trims non-focused clients'
        // working sets. Default off — it visibly cuts quality in every farm slot, which is the deal.
        public bool MultiInstanceRamReducerEnabled { get; set; } = false;

        // Framerate cap applied to farm clients. Render work is the other half of the RAM bill —
        // a background account doesn't need 60fps. Default 30.
        public int MultiInstanceRamReducerTargetFps { get; set; } = 30;

        // Clamp textures to the lowest level. Textures are the single biggest RAM consumer.
        public bool MultiInstanceRamReducerLowTextures { get; set; } = true;

        // Trim non-focused clients' working sets periodically (EmptyWorkingSet + LOW memory
        // priority). Reclaims physical RAM Windows would otherwise keep resident per instance.
        public bool MultiInstanceRamReducerTrimWorkingSet { get; set; } = true;

        // Multi Instance tab live preview refresh rate. Each running instance captures its
        // window on a background thread at this FPS. 30 gives smooth video previews; drop it
        // for big farms to save CPU. Clamped 1..60 on write.
        public int InstancePreviewFps { get; set; } = 30;

        // AltGen tab: the user's OWN BloxGen API key (https://bloxgen.net). Stored locally only —
        // we never ship a key. Each user supplies their own (signs up via the affiliate link on
        // the tab). Empty until entered.
        public string BloxGenApiKey { get; set; } = "";

        // auto-tile Roblox windows in a grid once they're visible (BeastStrap fork feature)
        public bool WindowTilingEnabled { get; set; } = false;
        public WindowTilingLayout WindowTilingLayout { get; set; } = WindowTilingLayout.Auto;

        // Multi Instance tab — bulk-launch preferences (not sensitive; the accounts themselves
        // live DPAPI-encrypted in Accounts.json, never here). BeastStrap fork feature.
        public string LastBulkPlaceId { get; set; } = "";
        public string LastBulkJobId { get; set; } = "";
        public int BulkLaunchDelaySeconds { get; set; } = 5;

        // user-visible debug mode — reveals the Run health check button (BeastStrap fork feature)
        public bool DebugModeEnabled { get; set; } = false;

        // VIP server picker — pop a WebView2 dialog before player launches and offer a free
        // shared VIP server pulled from rbxservers.xyz. Off by default. (BeastStrap fork feature)
        public bool EnableVipServerPrompt { get; set; } = false;

        // When on, a normal game launch auto-joins the least-populated public server of that game
        // (BeastStrap fork feature — see Bootstrapper.MaybeSelectEmptiestServerAsync).
        public bool JoinEmptiestServerOnLaunch { get; set; } = false;

        // BanAsync tab — trace cleaner + MAC/MachineGuid spoofer. (BeastStrap fork feature)
        public bool BanAsyncPreserveInGameSettings { get; set; } = true;
        public bool BanAsyncPreserveFastFlags { get; set; } = true;
        public bool BanAsyncIncludeStudioFolders { get; set; } = false;
        // Opt-in (default off): "Clean traces" also wipes BeastStrap's downloaded Roblox
        // installs under Versions\. Destructive — forces a full re-download next launch.
        public bool BanAsyncCleanVersions { get; set; } = false;
        public bool BanAsyncClearBrowserCookies { get; set; } = false;
        // Off by default in v420.11+: the netsh adapter cycle already releases the old DHCP
        // lease, so the extra ipconfig /release+/renew tends to do nothing useful and can
        // interrupt VPNs, voice chat, or captive-portal sessions. Existing users keep their
        // saved value — only the initial default changed.
        public bool BanAsyncDhcpRefreshAfterSpoof { get; set; } = false;

        // Default on. Spoofed MACs are written to HKLM\SYSTEM\...\NetworkAddress which the
        // Windows driver reads at every load, so the change naturally outlives BeastStrap
        // closing, being uninstalled, or the machine rebooting. Toggle off if you want
        // BeastStrap to clear the registry override on its own exit (the MAC stays applied
        // for the current Windows session and reverts on next reboot).
        public bool BanAsyncPersistent { get; set; } = true;
        public bool BanAsyncAdvancedMode { get; set; } = false;
        public bool BanAsyncOuiMirror { get; set; } = true;
        public bool BanAsyncMachineGuidAcknowledged { get; set; } = false;
        public string BanAsyncOriginalMachineGuid { get; set; } = "";
        public ObservableCollection<string> BanAsyncSpoofedAdapterGuids { get; set; } = new();

        // Original (pre-spoof) MAC per adapter, keyed by adapter Id. Captured the first
        // time an adapter is spoofed so the UI can show the real hardware MAC next to the
        // current spoofed one, and cleared on revert.
        public Dictionary<string, string> BanAsyncOriginalMacByGuid { get; set; } = new();

        // luaobfuscator.com obfuscation is opt-in and needs the user's own API key. Stored here (local
        // settings only — never shipped in the repo/binary); blank means the LuaObfuscator engine is off.
        public string LuaObfuscatorApiKey { get; set; } = "";

        // bypass.tools link-bypasser API key — the user's own, local settings only (never shipped). Blank
        // means the Link Bypasser is off; users get a key by signing up via the referral link in the tab.
        public string BypassToolsApiKey { get; set; } = "";

        // Simple mode — hides advanced TOOLS (Obfuscator/Deobfuscator/Link Bypasser) to keep nav clean.
        public bool SimpleMode { get; set; } = false;
    }
}
