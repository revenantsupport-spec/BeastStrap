using System.Media;
using System.Windows.Interop;

using Windows.Win32;
using Windows.Win32.Foundation;

namespace BeastStrap.UI.Elements.Dialogs
{
    // hmm... do i use MVVM for this?
    // this is entirely static, so i think im fine without it, and this way is just so much more efficient

    /// <summary>
    /// Interaction logic for ExceptionDialog.xaml
    /// </summary>
    public partial class ExceptionDialog
    {
        public ExceptionDialog(Exception exception)
        {
            InitializeComponent();
            AddException(exception);

            // Crash support flow: let the user export their logs in one click and tell
            // them where to send the zip (email / Discord) instead of filing a GitHub issue.
            HelpMessageMDTextBlock.MarkdownText = SupportActions.ContactMarkdown;
            VersionText.Text = String.Format(Strings.Dialog_Exception_Version, App.Version);

            SupportActions.WireExportButton(ExportLogsButton);
            JoinDiscordButton.Click += (_, _) => Utilities.ShellExecute(App.ProjectDiscordLink);

            CloseButton.Click += delegate
            {
                Close();
            };

            SystemSounds.Hand.Play();

            Loaded += delegate
            {
                IntPtr hWnd = new WindowInteropHelper(this).Handle;
                PInvoke.FlashWindow((HWND)hWnd, true);
            };
        }

        private void AddException(Exception exception, bool inner = false)
        {
            if (!inner)
                ErrorRichTextBox.Selection.Text = $"{exception.GetType()}: {exception.Message}";

            if (exception.InnerException is null)
                return;

            ErrorRichTextBox.Selection.Text += $"\n\n[Inner Exception]\n{exception.InnerException.GetType()}: {exception.InnerException.Message}";

            AddException(exception.InnerException, true);
        }
    }
}
