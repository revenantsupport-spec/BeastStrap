using System.ComponentModel;
using System.Windows.Media;

namespace BeastStrap.Models
{
    public class BootstrapperIconEntry : INotifyPropertyChanged
    {
        public BootstrapperIcon IconType { get; set; }
        public ImageSource ImageSource => IconType.GetIcon().GetImageSource();

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ImageSource)));
    }
}