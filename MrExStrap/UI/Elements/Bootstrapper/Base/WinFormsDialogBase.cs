using System.Windows.Forms;
using System.Windows.Shell;

using BeastStrap.UI.Utility;

namespace BeastStrap.UI.Elements.Bootstrapper.Base
{
    public class WinFormsDialogBase : Form, IBootstrapperDialog
    {
        public const int TaskbarProgressMaximum = 100;

        public BeastStrap.Bootstrapper? Bootstrapper { get; set; }

        private bool _isClosing;

        #region UI Elements
        protected virtual string _message { get; set; } = "Please wait...";
        protected virtual ProgressBarStyle _progressStyle { get; set; }
        protected virtual int _progressValue { get; set; }
        protected virtual int _progressMaximum { get; set; }
        protected virtual TaskbarItemProgressState _taskbarProgressState { get; set; }
        protected virtual double _taskbarProgressValue { get; set; }
        protected virtual bool _cancelEnabled { get; set; }

        public string Message
        {
            get => _message;
            set
            {
                if (InvokeRequired)
                    Invoke(() => _message = value);
                else
                    _message = value;
            }
        }

        public ProgressBarStyle ProgressStyle
        {
            get => _progressStyle;
            set
            {
                if (InvokeRequired)
                    Invoke(() => _progressStyle = value);
                else
                    _progressStyle = value;
            }
        }

        public int ProgressMaximum
        {
            get => _progressMaximum;
            set
            {
                if (InvokeRequired)
                    Invoke(() => _progressMaximum = value);
                else
                    _progressMaximum = value;
            }
        }

        public int ProgressValue
        {
            get => _progressValue;
            set
            {
                if (InvokeRequired)
                    Invoke(() => _progressValue = value);
                else
                    _progressValue = value;
            }
        }

        // Our own window handle, resolved once.
        //
        // Both setters below used to call Process.GetCurrentProcess().MainWindowHandle on EVERY
        // update. That allocates a fresh finalizable Process each time (never disposed) whose
        // main-window cache is always cold, so each call ran a full EnumWindows sweep over every
        // top-level window on the desktop — and these setters are driven from
        // Bootstrapper.UpdateProgressBar, which fires per download chunk across six concurrent
        // downloads. This is the same window, so ask once.
        private IntPtr TaskbarHandle => IsHandleCreated ? Handle : IntPtr.Zero;

        public TaskbarItemProgressState TaskbarProgressState
        {
            get => _taskbarProgressState;
            set
            {
                _taskbarProgressState = value;

                IntPtr hwnd = TaskbarHandle;
                if (hwnd != IntPtr.Zero)
                    TaskbarProgress.SetProgressState(hwnd, value);
            }
        }

        public double TaskbarProgressValue
        {
            get => _taskbarProgressValue;
            set
            {
                _taskbarProgressValue = value;

                IntPtr hwnd = TaskbarHandle;
                if (hwnd != IntPtr.Zero)
                    TaskbarProgress.SetProgressValue(hwnd, (int)value, TaskbarProgressMaximum);
            }
        }

        public bool CancelEnabled
        {
            get => _cancelEnabled;
            set
            {
                if (InvokeRequired)
                    Invoke(() => _cancelEnabled = value);
                else
                    _cancelEnabled = value;
            }
        }
        #endregion

        public void ScaleWindow()
        {
            Size = MinimumSize = MaximumSize = WindowScaling.GetScaledSize(Size);

            foreach (Control control in Controls)
            {
                control.Size = WindowScaling.GetScaledSize(control.Size);
                control.Location = WindowScaling.GetScaledPoint(control.Location);
                control.Padding = WindowScaling.GetScaledPadding(control.Padding);
            }
        }

        public void SetupDialog()
        {
            Text = App.Settings.Prop.BootstrapperTitle;
            Icon = App.Settings.Prop.BootstrapperIcon.GetIcon();

            if (Locale.RightToLeft)
            {
                this.RightToLeft = RightToLeft.Yes;
                this.RightToLeftLayout = true;
            }
        }

        #region WinForms event handlers
        public void ButtonCancel_Click(object? sender, EventArgs e) => Close();

        public void Dialog_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_isClosing)
                Bootstrapper?.Cancel();
        }
        #endregion

        #region IBootstrapperDialog Methods
        public void ShowBootstrapper() => ShowDialog();

        public virtual void CloseBootstrapper()
        {
            if (InvokeRequired)
            {
                Invoke(CloseBootstrapper);
            }
            else
            {
                _isClosing = true;
                Close();
            }
        }

        public virtual void ShowSuccess(string message, Action? callback) => BaseFunctions.ShowSuccess(message, callback);
        #endregion
    }
}
