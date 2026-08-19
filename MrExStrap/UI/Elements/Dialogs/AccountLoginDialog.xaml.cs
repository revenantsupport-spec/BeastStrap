using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace BeastStrap.UI.Elements.Dialogs
{
    // Embedded Roblox login that captures the .ROBLOSECURITY cookie for a new account. Mirrors the
    // WebView2 approach in VipServerDialog. Starts from a cleared cookie jar so the user logs into
    // the exact account they want to add (rather than being silently signed in as the last one).
    public partial class AccountLoginDialog
    {
        private const string LOG_IDENT = "AccountLoginDialog";

        private bool _hooked;
        private bool _captured;

        public string? Cookie { get; private set; }

        public AccountLoginDialog()
        {
            InitializeComponent();

            CancelButton.Click += (_, _) => Close();

            Loaded += async (_, _) => await InitializeWebViewAsync();
            Closing += OnClosing;
        }

        private async Task InitializeWebViewAsync()
        {
            try
            {
                // Dedicated user-data folder keeps this login session out of any other WebView2 use.
                string userDataFolder = Path.Combine(Paths.Base, "AccountLoginWebView");
                Directory.CreateDirectory(userDataFolder);

                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, null);
                await WebView.EnsureCoreWebView2Async(env);

                WebView.CoreWebView2.CookieManager.DeleteAllCookies();

                WebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                WebView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
                _hooked = true;

                StatusOverlay.Visibility = Visibility.Collapsed;
                WebView.CoreWebView2.Navigate("https://www.roblox.com/login");
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "WebView2 runtime unavailable; closing login dialog.");
                App.Logger.WriteException(LOG_IDENT, ex);

                StatusOverlay.Visibility = Visibility.Visible;
                StatusOverlay.Text = "Couldn't open the login window. The Microsoft Edge WebView2 runtime may be missing — paste a cookie instead.";
                await Task.Delay(2200);
                Close();
            }
        }

        private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (_captured)
                return;

            string url = WebView.CoreWebView2.Source ?? "";

            // Wait until we're off the login/2FA pages and back on roblox.com before reading cookies.
            if (!url.Contains("roblox.com", StringComparison.OrdinalIgnoreCase))
                return;
            if (url.Contains("/login", StringComparison.OrdinalIgnoreCase)
                || url.Contains("/auth", StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                var cookies = await WebView.CoreWebView2.CookieManager.GetCookiesAsync("https://www.roblox.com");
                var security = cookies.FirstOrDefault(c => c.Name == ".ROBLOSECURITY");

                if (security != null && !string.IsNullOrEmpty(security.Value) && security.Value.Length > 100)
                {
                    _captured = true;
                    Cookie = security.Value;
                    App.Logger.WriteLine(LOG_IDENT, "Captured a .ROBLOSECURITY cookie from the login window.");
                    // Defer the close until this async handler returns. Discard the operation —
                    // we're not awaiting it (this method is async, so an un-discarded call warns).
                    _ = Dispatcher.BeginInvoke(new Action(Close));
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Cookies", ex);
            }
        }

        private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            // No tab UI — force any popup through the main frame so our handlers still see it.
            e.Handled = true;
            try { WebView.CoreWebView2.Navigate(e.Uri); } catch { /* best-effort */ }
        }

        private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_hooked)
            {
                try { WebView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted; } catch { }
                try { WebView.CoreWebView2.NewWindowRequested -= OnNewWindowRequested; } catch { }

                // Clear the jar on the way out, while CoreWebView2 is still alive.
                //
                // This folder is persistent, and the only other clear happens at the START of the
                // next login. Add one account and never add another, and the raw .ROBLOSECURITY
                // sat unencrypted in AccountLoginWebView\EBWebView\...\Network\Cookies for good —
                // directly next to the Accounts.json that SecureStore takes care to DPAPI-encrypt.
                // Anything able to read the install folder had the account.
                //
                // The clear on open stays as the backstop for a process killed mid-login.
                try
                {
                    WebView.CoreWebView2.CookieManager.DeleteAllCookies();
                    App.Logger.WriteLine(LOG_IDENT, "Cleared the login window's cookie jar.");
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException(LOG_IDENT + "::ClearCookies", ex);
                }
            }

            try { WebView.Dispose(); } catch { }
        }
    }
}
