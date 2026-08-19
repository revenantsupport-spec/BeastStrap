using System.Windows;
using System.Windows.Input;

using CommunityToolkit.Mvvm.Input;

using BeastStrap.Utility;

namespace BeastStrap.UI.ViewModels.Settings
{
    // Shared base for the Obfuscator + Deobfuscator tabs: a script input, an output box, busy/status state,
    // and copy/paste/clear plumbing. Subclasses add their own actions and route them through RunAsync so
    // busy state and success/error reporting live in one place.
    public abstract class ScriptToolViewModel : NotifyPropertyChangedViewModel
    {
        protected const string LOG_IDENT = "ScriptTool";

        private string _scriptInput = "";
        public string ScriptInput
        {
            get => _scriptInput;
            set { _scriptInput = value ?? ""; OnPropertyChanged(nameof(ScriptInput)); OnPropertyChanged(nameof(HasInput)); }
        }
        public bool HasInput => _scriptInput.Trim().Length > 0;

        private string _output = "";
        public string Output
        {
            get => _output;
            set { _output = value ?? ""; OnPropertyChanged(nameof(Output)); OnPropertyChanged(nameof(HasOutput)); }
        }
        public bool HasOutput => _output.Length > 0;

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(NotBusy)); }
        }
        public bool NotBusy => !_isBusy;

        private string _statusText = "";
        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(nameof(StatusText)); }
        }

        private bool _isError;
        public bool IsError
        {
            get => _isError;
            set { _isError = value; OnPropertyChanged(nameof(IsError)); }
        }

        public ICommand CopyOutputCommand => new RelayCommand(CopyOutput);
        public ICommand PasteCommand => new RelayCommand(PasteInput);
        public ICommand ClearCommand => new RelayCommand(Clear);

        private void CopyOutput()
        {
            if (string.IsNullOrEmpty(Output))
                return;
            try
            {
                Clipboard.SetDataObject(Output, true);
                IsError = false;
                StatusText = "Copied the result to your clipboard.";
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }
        }

        private void PasteInput()
        {
            try
            {
                if (Clipboard.ContainsText())
                    ScriptInput = Clipboard.GetText();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }
        }

        protected virtual void Clear()
        {
            ScriptInput = "";
            Output = "";
            StatusText = "";
            IsError = false;
        }

        // Runs an API action with shared busy + success/error handling; sets Output on success.
        protected async Task RunAsync(string busyLabel, Func<Task<LeakdClient.LeakdResult>> action)
        {
            if (!HasInput)
            {
                IsError = true;
                StatusText = "Paste a script into the box first.";
                return;
            }

            IsBusy = true;
            IsError = false;
            StatusText = busyLabel;
            try
            {
                var result = await action();
                if (result.Success)
                {
                    Output = result.Output;
                    IsError = false;
                    StatusText = BuildSuccessStatus(result);
                }
                else
                {
                    IsError = true;
                    StatusText = result.Error ?? "That didn't work.";
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        protected virtual string BuildSuccessStatus(LeakdClient.LeakdResult r) => "Done.";
    }
}
