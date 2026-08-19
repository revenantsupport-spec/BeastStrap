using System.Windows;
using Microsoft.Web.WebView2.Core;

using BeastStrap.Utility;

namespace BeastStrap.UI.Elements.Dialogs
{
    public partial class VipServerDialog
    {
        private const string LOG_IDENT = "VipServerDialog";

        private readonly long _placeId;
        private bool _navigationHooked;

        public string? PickedAccessCode { get; private set; }

        // Set once the window has actually closed, so delayed continuations know not to touch it.
        private bool _closed;

        public VipServerDialog(long placeId)
        {
            _placeId = placeId;

            InitializeComponent();

            HeaderText.Text = $"Pick a free VIP server for place #{_placeId}, or skip to launch normally.";

            SkipButton.Click += (_, _) => Close();

            // async void handler — nothing above catches, so wrap it or an unhandled throw here
            // reaches the global handler and terminates the launch.
            Loaded += async (_, _) =>
            {
                try { await InitializeWebViewAsync(); }
                catch (Exception ex) { App.Logger.WriteException(LOG_IDENT + "::Loaded", ex); }
            };

            Closing += OnClosing;
            Closed += (_, _) => _closed = true;
        }

        private async Task InitializeWebViewAsync()
        {
            try
            {
                // EnsureCoreWebView2Async throws if the WebView2 runtime is missing. Edge runtime
                // ships pre-installed on Win10/11 so this is rare in practice, but we fail gracefully:
                // log, leave PickedAccessCode null, auto-close so the launch path proceeds normally.
                await WebView.EnsureCoreWebView2Async();

                // Block navigation to non-rbxservers / non-Roblox URLs so the user can't wander off
                // into the open web inside our launcher process.
                WebView.CoreWebView2.NavigationStarting += OnNavigationStarting;
                WebView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
                _navigationHooked = true;

                StatusOverlay.Visibility = Visibility.Collapsed;

                string url = $"https://rbxservers.xyz/embedded/game/{_placeId}";
                App.Logger.WriteLine(LOG_IDENT, $"Navigating to {url}");
                WebView.CoreWebView2.Navigate(url);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "WebView2 runtime unavailable; closing dialog without VIP pick.");
                App.Logger.WriteException(LOG_IDENT, ex);

                if (_closed)
                    return;

                StatusOverlay.Text = "Couldn't load the VIP server picker. Continuing with a normal launch.";

                // Brief delay so the message is readable, then close. Best-effort UX — if the
                // WebView2 init failed, the user shouldn't sit here for long.
                //
                // The _closed guard matters: Skip stays clickable for this whole 1500 ms, and
                // calling Close() on an already-closed Window throws InvalidOperationException.
                // This is an async void Loaded handler, so by then ShowDialog has returned and the
                // caller's try/catch is long gone — the throw reaches the global handler, which
                // shows a crash dialog and terminates the launch. Clicking Skip during init also
                // disposes the WebView, which lands us in this same catch on a machine where
                // WebView2 works perfectly.
                await Task.Delay(1500);

                if (!_closed)
                    Close();
            }
        }

        private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            string uri = e.Uri ?? "";

            // Fast path: clicking a server in the embed navigates to /embedded/quicklaunch/{GUID},
            // which is rbxservers' click-tracking interstitial with a 7-second countdown. The GUID
            // in that URL IS the accessCode, so we capture it on click and bail out — no countdown,
            // no roblox.com bounce, instant launch.
            string? quickCode = LaunchArgsUtility.TryExtractRbxServersQuickLaunchCode(uri);
            if (quickCode is not null)
            {
                e.Cancel = true;
                App.Logger.WriteLine(LOG_IDENT, $"Captured accessCode from quicklaunch URL (skipped countdown).");
                PickedAccessCode = quickCode;
                Dispatcher.BeginInvoke(new Action(Close));
                return;
            }

            // Any navigation that leaves rbxservers and heads to Roblox is the user's pick.
            // The redirect target depends on rbxservers' current routing — could be:
            //   roblox://experiences/start?placeId=X&accessCode=Y     (deep link)
            //   roblox-player:1+launchmode:play+...accessCode=Y...    (BeastStrap-style)
            //   https://www.roblox.com/games/start?placeId=X&accessCode=Y     (legacy web)
            //   https://www.roblox.com/games/{placeId}?accessCode=Y           (modern detail)
            //   https://www.roblox.com/games/{placeId}/{slug}?accessCode=Y    (with slug)
            // We cancel the navigation in every case (we don't want to launch via the
            // browser — the bootstrapper will do that with our injected launch args).
            // If the URL carries an accessCode we capture it; otherwise we close as if Skip.
            bool isRobloxNav = uri.StartsWith("roblox://", StringComparison.OrdinalIgnoreCase)
                || uri.StartsWith("roblox-player:", StringComparison.OrdinalIgnoreCase)
                || uri.StartsWith("https://www.roblox.com/", StringComparison.OrdinalIgnoreCase)
                || uri.StartsWith("https://roblox.com/", StringComparison.OrdinalIgnoreCase);

            if (!isRobloxNav)
                return;

            e.Cancel = true;

            string? code = LaunchArgsUtility.TryExtractAccessCode(uri);
            if (code is not null)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Captured accessCode from {uri.Substring(0, Math.Min(uri.Length, 120))}");
                PickedAccessCode = code;
            }
            else
            {
                App.Logger.WriteLine(LOG_IDENT, $"Roblox navigation without accessCode; treating as Skip. uri={uri}");
            }

            Dispatcher.BeginInvoke(new Action(Close));
        }

        private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            // The embed sometimes tries to open links in a new tab/window (about pages, ads).
            // We don't host a tab UI — block those and force them through the standard
            // navigation path so our filter above sees them too.
            e.Handled = true;
            try { WebView.CoreWebView2.Navigate(e.Uri); } catch { /* best-effort */ }
        }

        private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_navigationHooked)
            {
                try { WebView.CoreWebView2.NavigationStarting -= OnNavigationStarting; } catch { }
                try { WebView.CoreWebView2.NewWindowRequested -= OnNewWindowRequested; } catch { }
            }
            try { WebView.Dispose(); } catch { }
        }
    }
}
