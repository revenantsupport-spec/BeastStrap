using System.Windows.Controls;
using System.Windows.Input;

using BeastStrap.Utility;

namespace BeastStrap.UI.Elements.Dialogs
{
    public partial class FlagSearchDialog
    {
        // Set to the chosen flag name when the user adds one; null if they just closed.
        public string? SelectedFlag { get; private set; }

        public FlagSearchDialog()
        {
            InitializeComponent();
            RefreshList();
        }

        private void RefreshList()
        {
            var results = KnownFlags.Search(SearchBox?.Text ?? "");
            FlagList.ItemsSource = results;
            CountText.Text = $"{results.Count} shown ({KnownFlags.Names.Count} known)";
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshList();

        private void FlagList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Accept();

        private void AddButton_Click(object sender, System.Windows.RoutedEventArgs e) => Accept();

        private void CloseButton_Click(object sender, System.Windows.RoutedEventArgs e) => Close();

        private void Accept()
        {
            if (FlagList.SelectedItem is string flag && !string.IsNullOrEmpty(flag))
            {
                SelectedFlag = flag;
                Close();
            }
        }
    }
}
