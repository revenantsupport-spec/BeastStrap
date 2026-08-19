using System.Windows;
using System.Windows.Controls.Primitives;

namespace BeastStrap.UI
{
    // Fork feature: one-click "export my logs and tell me where to send them" flow,
    // surfaced on every crash/error dialog so users never have to dig through Settings.
    //
    // Wraps DiagnosticBundle in quick mode (no slow network probes at crash time) and
    // points the user at the support email / Discord. Everything here is best-effort and
    // never throws — if the export fails we still show the user where to reach us.
    public static class SupportActions
    {
        private const string LOG_IDENT = "SupportActions";

        // Help line shown in the rich error dialogs. Rendered as markdown, so the email
        // and Discord links are clickable (MarkdownTextBlock handles bold + links).
        public static string ContactMarkdown =>
            "Want us to take a look? Click **Export logs**, then send the zip to "
            + $"[{App.ProjectSupportEmail}](mailto:{App.ProjectSupportEmail}) "
            + $"or drop it in our [Discord]({App.ProjectDiscordLink}).";

        // Builds a quick diagnostic bundle, reveals it in Explorer, and tells the user
        // where to send it. Safe to await on the UI thread, and safe to block on
        // (GetAwaiter().GetResult()) from a background thread.
        public static async Task ExportLogsAsync()
        {
            try
            {
                string zipPath = await DiagnosticBundle.CreateAsync(quick: true);
                App.Logger.WriteLine(LOG_IDENT, $"Crash export saved to {zipPath}");

                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{zipPath}\""
                    });
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException(LOG_IDENT + "::Reveal", ex);
                }

                Frontend.ShowMessageBox(
                    "Your logs were saved to:\n\n"
                    + zipPath
                    + "\n\nSend this zip to us so we can look into it:\n\n"
                    + $"•  Email it to [{App.ProjectSupportEmail}](mailto:{App.ProjectSupportEmail})\n"
                    + $"•  Or post it in our [Discord]({App.ProjectDiscordLink})\n\n"
                    + "We opened the folder for you. Just attach the highlighted file.",
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);

                Frontend.ShowMessageBox(
                    "Couldn't export your logs automatically.\n\n"
                    + $"Reason: {ex.GetType().Name}: {ex.Message}\n\n"
                    + $"You can still reach us — email [{App.ProjectSupportEmail}](mailto:{App.ProjectSupportEmail}) "
                    + $"or join our [Discord]({App.ProjectDiscordLink}).",
                    MessageBoxImage.Error);
            }
        }

        // Wires an error-dialog button so clicking it runs the export, disabling and
        // relabelling the button while it works. For buttons clicked on the UI thread.
        public static void WireExportButton(ButtonBase button)
        {
            button.Click += async (_, _) =>
            {
                object? original = button.Content;
                button.IsEnabled = false;
                button.Content = "Exporting...";

                try
                {
                    await ExportLogsAsync();
                }
                finally
                {
                    button.Content = original;
                    button.IsEnabled = true;
                }
            };
        }
    }
}
