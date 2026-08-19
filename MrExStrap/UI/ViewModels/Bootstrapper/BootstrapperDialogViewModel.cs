using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

using CommunityToolkit.Mvvm.Input;

namespace BeastStrap.UI.ViewModels.Bootstrapper
{
    public class BootstrapperDialogViewModel : NotifyPropertyChangedViewModel
    {
        private readonly IBootstrapperDialog _dialog;

        public ICommand CancelInstallCommand => new RelayCommand(CancelInstall);

        public string Title => App.Settings.Prop.BootstrapperTitle;
        public ImageSource Icon { get; set; } = App.Settings.Prop.BootstrapperIcon.GetIcon().GetImageSource();
        public string Message { get; set; } = "Please wait...";
        public bool ProgressIndeterminate { get; set; } = true;
        public int ProgressMaximum { get; set; } = 0;
        public int ProgressValue { get; set; } = 0;

        public TaskbarItemProgressState TaskbarProgressState { get; set; } = TaskbarItemProgressState.Indeterminate;
        public double TaskbarProgressValue { get; set; } = 0;

        public bool CancelEnabled { get; set; } = false;
        public Visibility CancelButtonVisibility => CancelEnabled ? Visibility.Visible : Visibility.Collapsed;

        // --- BeastStrap fork: extended loading-screen info ---

        private string _versionInfoText = "";
        public string VersionInfoText
        {
            get => _versionInfoText;
            set
            {
                _versionInfoText = value ?? "";
                OnPropertyChanged(nameof(VersionInfoText));
                OnPropertyChanged(nameof(VersionInfoVisibility));
            }
        }
        public Visibility VersionInfoVisibility =>
            string.IsNullOrEmpty(_versionInfoText) ? Visibility.Collapsed : Visibility.Visible;

        private bool _isDowngraded;
        public bool IsDowngraded
        {
            get => _isDowngraded;
            set
            {
                _isDowngraded = value;
                OnPropertyChanged(nameof(IsDowngraded));
                OnPropertyChanged(nameof(DowngradedBadgeVisibility));
            }
        }
        public Visibility DowngradedBadgeVisibility => _isDowngraded ? Visibility.Visible : Visibility.Collapsed;

        private string _downloadSizeText = "";
        public string DownloadSizeText
        {
            get => _downloadSizeText;
            set
            {
                _downloadSizeText = value ?? "";
                OnPropertyChanged(nameof(DownloadSizeText));
                OnPropertyChanged(nameof(DownloadSizeVisibility));
            }
        }
        public Visibility DownloadSizeVisibility =>
            string.IsNullOrEmpty(_downloadSizeText) ? Visibility.Collapsed : Visibility.Visible;

        // BeastStrap fork: smoothed "X MB/s · ~30s remaining" line under the size text.
        // Hidden until we have a sample to base a rate on (avoids "0 B/s · forever").
        private string _downloadSpeedText = "";
        public string DownloadSpeedText
        {
            get => _downloadSpeedText;
            set
            {
                _downloadSpeedText = value ?? "";
                OnPropertyChanged(nameof(DownloadSpeedText));
                OnPropertyChanged(nameof(DownloadSpeedVisibility));
            }
        }
        public Visibility DownloadSpeedVisibility =>
            string.IsNullOrEmpty(_downloadSpeedText) ? Visibility.Collapsed : Visibility.Visible;

        private string _placeInfoText = "";
        public string PlaceInfoText
        {
            get => _placeInfoText;
            set
            {
                _placeInfoText = value ?? "";
                OnPropertyChanged(nameof(PlaceInfoText));
                OnPropertyChanged(nameof(PlaceInfoVisibility));
            }
        }
        public Visibility PlaceInfoVisibility =>
            string.IsNullOrEmpty(_placeInfoText) ? Visibility.Collapsed : Visibility.Visible;

        [Obsolete("Do not use this! This is for the designer only.", true)]
        public BootstrapperDialogViewModel()
        {
            _dialog = null!;
        }

        public BootstrapperDialogViewModel(IBootstrapperDialog dialog)
        {
            _dialog = dialog;
        }

        private void CancelInstall()
        {
            _dialog.Bootstrapper?.Cancel();
            _dialog.CloseBootstrapper();
        }
    }
}
