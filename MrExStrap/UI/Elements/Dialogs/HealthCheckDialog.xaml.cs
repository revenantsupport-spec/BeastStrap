using System.Windows;
using Microsoft.Win32;

using BeastStrap.UI.Elements.Base;
using BeastStrap.Utility;

namespace BeastStrap.UI.Elements.Dialogs
{
    public partial class HealthCheckDialog : WpfUiWindow
    {
        private const string LOG_IDENT = "HealthCheckDialog";
        private string _renderedText = "";

        public HealthCheckDialog()
        {
            InitializeComponent();
            Loaded += async (_, _) => await RunAsync();
        }

        private async Task RunAsync()
        {
            SummaryTextBlock.Text = "Running...";
            ResultsTextBox.Text = "Running checks, this takes a few seconds...";
            RerunButton.IsEnabled = false;
            CopyButton.IsEnabled = false;
            SaveButton.IsEnabled = false;

            try
            {
                var results = await HealthCheck.RunAllAsync();
                _renderedText = HealthCheck.Render(results);
                ResultsTextBox.Text = _renderedText;

                int ok = results.Count(r => r.Status == CheckStatus.Ok);
                int warn = results.Count(r => r.Status == CheckStatus.Warn);
                int fail = results.Count(r => r.Status == CheckStatus.Fail);
                SummaryTextBlock.Text = $"{ok} OK  ·  {warn} warn  ·  {fail} fail  ·  {results.Count} checks total";
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                _renderedText = "Health check threw an exception: " + ex;
                ResultsTextBox.Text = _renderedText;
                SummaryTextBlock.Text = "Aborted — see text for details.";
            }
            finally
            {
                RerunButton.IsEnabled = true;
                CopyButton.IsEnabled = true;
                SaveButton.IsEnabled = true;
            }
        }

        private async void RerunButton_Click(object sender, RoutedEventArgs e) => await RunAsync();

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetDataObject(_renderedText, true);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Copy", ex);
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // In portable mode, default to the exe folder so the file travels with the install.
                // Otherwise default to the user's Desktop.
                string defaultDir = App.IsPortableMode
                    ? Paths.Base
                    : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

                string defaultName = $"BeastStrap-healthcheck-{DateTime.Now:yyyyMMdd-HHmmss}.txt";

                var dialog = new SaveFileDialog
                {
                    InitialDirectory = defaultDir,
                    FileName = defaultName,
                    Filter = "Text file (*.txt)|*.txt"
                };

                if (dialog.ShowDialog() != true)
                    return;

                File.WriteAllText(dialog.FileName, _renderedText);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Save", ex);
                MessageBox.Show("Couldn't save. Check the log.", "Save failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
