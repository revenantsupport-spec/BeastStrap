using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Collections.ObjectModel;

using System.Threading.Tasks;

using BeastStrap.Models.Persistable;
using BeastStrap.UI.Elements.Dialogs;
using BeastStrap.UI.ViewModels.Settings;
using BeastStrap.Utility;

namespace BeastStrap.UI.Elements.Settings.Pages
{
    /// <summary>
    /// Interaction logic for FastFlagsPage.xaml
    ///
    /// Unified Fast Flags section: friendly presets (MVVM via FastFlagsViewModel) plus the
    /// raw key/value editor (code-behind — a DataGrid genuinely can't be done cleanly in MVVM)
    /// plus a per-profile selector. The editor operates on App.FastFlags, which is repointed
    /// at the selected Versions Manager profile's flag file via EditingProfileId.
    /// </summary>
    public partial class FastFlagsPage
    {
        private FastFlagsViewModel _viewModel = null!;

        // ---- raw editor state (ported from the old FastFlagEditorPage) ----
        private readonly ObservableCollection<FastFlag> _fastFlagList = new();
        private readonly List<string> _validPrefixes = new()
        {
            "FFlag", "DFFlag", "SFFlag", "FInt", "DFInt", "FString", "DFString", "FLog", "DFLog"
        };

        // values must match the entire string to avoid cases where half the string
        // matches but the filter would still be invalid
        private readonly Regex _boolFilterPattern = new("^(?:true|false)(;[\\d]{1,})+$", RegexOptions.IgnoreCase);
        private readonly Regex _intFilterPattern = new("^([\\d]{1,})?(;[\\d]{1,})+$", RegexOptions.IgnoreCase);
        private readonly Regex _stringFilterPattern = new("^[^;]*(;[\\d]{1,})+$", RegexOptions.IgnoreCase);

        private bool _showPresets = false;
        private string _searchFilter = "";

        // ---- per-profile selector state ----
        private bool _suppressProfileChange = false;
        private string _currentProfileId = "";

        public FastFlagsPage()
        {
            InitializeComponent();
            SetupViewModel();
            InitProfileSelector();
            ReloadList();

            // Pull Roblox's live known-flags list in the background; refresh the grid once it lands so
            // the "Known" column fills in.
            _ = LoadKnownFlagsAsync();
        }

        private async Task LoadKnownFlagsAsync()
        {
            await KnownFlags.LoadAsync();

            if (KnownFlags.Loaded)
                Dispatcher.Invoke(ReloadList);
        }

        private void SetupViewModel()
        {
            _viewModel = new FastFlagsViewModel();
            _viewModel.RequestPageReloadEvent += (_, _) =>
            {
                // Reset toggle swapped the flag set — rebuild the presets VM so the controls
                // re-read App.FastFlags, then refresh the raw grid.
                SetupViewModel();
                ReloadList();
            };
            DataContext = _viewModel;
        }

        private void InitProfileSelector()
        {
            _suppressProfileChange = true;

            ProfileSelector.ItemsSource = App.Settings.Prop.VersionProfiles;

            string activeId = App.Settings.Prop.ActiveVersionProfileId;
            var active = App.Settings.Prop.VersionProfiles.FirstOrDefault(p => p.Id == activeId)
                         ?? App.Settings.Prop.VersionProfiles.FirstOrDefault();

            _currentProfileId = active?.Id ?? "";
            App.FastFlags.EditingProfileId = _currentProfileId;
            App.FastFlags.Load();

            ProfileSelector.SelectedItem = active;

            _suppressProfileChange = false;
        }

        private void ProfileSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressProfileChange)
                return;

            if (ProfileSelector.SelectedItem is not VersionProfile profile)
                return;

            if (profile.Id == _currentProfileId)
                return;

            // Offer to save edits to the profile we're leaving.
            if (App.FastFlags.Changed)
            {
                var choice = Frontend.ShowMessageBox(
                    "Save changes to the current profile's fast flags before switching?",
                    MessageBoxImage.Question, MessageBoxButton.YesNoCancel, MessageBoxResult.Yes);

                if (choice == MessageBoxResult.Cancel)
                {
                    _suppressProfileChange = true;
                    ProfileSelector.SelectedItem = App.Settings.Prop.VersionProfiles
                        .FirstOrDefault(p => p.Id == _currentProfileId);
                    _suppressProfileChange = false;
                    return;
                }

                if (choice == MessageBoxResult.Yes)
                    App.FastFlags.Save();
            }

            _currentProfileId = profile.Id;
            App.FastFlags.EditingProfileId = profile.Id;
            App.FastFlags.Load();

            SetupViewModel();
            ReloadList();
        }

        // ---- raw editor (verbatim from the old FastFlagEditorPage, App.FastFlags is now per-profile) ----

        private void ReloadList()
        {
            var selectedEntry = DataGrid.SelectedItem as FastFlag;

            _fastFlagList.Clear();

            var presetFlags = FastFlagManager.PresetFlags.Values;

            foreach (var pair in App.FastFlags.Prop.OrderBy(x => x.Key))
            {
                if (!_showPresets && presetFlags.Contains(pair.Key))
                    continue;

                if (!pair.Key.ToLower().Contains(_searchFilter.ToLower()))
                    continue;

                var entry = new FastFlag
                {
                    Name = pair.Key,
                    Value = pair.Value.ToString()!
                };

                _fastFlagList.Add(entry);
            }

            if (DataGrid.ItemsSource is null)
                DataGrid.ItemsSource = _fastFlagList;

            if (selectedEntry is null)
                return;

            var newSelectedEntry = _fastFlagList.Where(x => x.Name == selectedEntry.Name).FirstOrDefault();

            if (newSelectedEntry is null)
                return;

            DataGrid.SelectedItem = newSelectedEntry;
            DataGrid.ScrollIntoView(newSelectedEntry);
        }

        private void ClearSearch(bool refresh = true)
        {
            SearchTextBox.Text = "";
            _searchFilter = "";

            if (refresh)
                ReloadList();
        }

        private void ShowAddDialog()
        {
            var dialog = new AddFastFlagDialog();
            dialog.ShowDialog();

            if (dialog.Result != MessageBoxResult.OK)
                return;

            if (dialog.Tabs.SelectedIndex == 0)
                AddSingle(dialog.FlagNameTextBox.Text.Trim(), dialog.FlagValueTextBox.Text);
            else if (dialog.Tabs.SelectedIndex == 1)
                ImportJSON(dialog.JsonTextBox.Text);
        }

        private void AddSingle(string name, string value)
        {
            FastFlag? entry;

            if (App.FastFlags.GetValue(name) is null)
            {
                if (!ValidateFlagEntry(name, value))
                {
                    ShowAddDialog();
                    return;
                }

                entry = new FastFlag
                {
                    Name = name,
                    Value = value
                };

                if (!name.Contains(_searchFilter))
                    ClearSearch();

                _fastFlagList.Add(entry);

                App.FastFlags.SetValue(entry.Name, entry.Value);
            }
            else
            {
                Frontend.ShowMessageBox(Strings.Menu_FastFlagEditor_AlreadyExists, MessageBoxImage.Information);

                bool refresh = false;

                if (!_showPresets && FastFlagManager.PresetFlags.Values.Contains(name))
                {
                    TogglePresetsButton.IsChecked = true;
                    _showPresets = true;
                    refresh = true;
                }

                if (!name.Contains(_searchFilter))
                {
                    ClearSearch(false);
                    refresh = true;
                }

                if (refresh)
                    ReloadList();

                entry = _fastFlagList.Where(x => x.Name == name).FirstOrDefault();
            }

            DataGrid.SelectedItem = entry;
            DataGrid.ScrollIntoView(entry);
        }

        private void ImportJSON(string json)
        {
            Dictionary<string, object>? list = null;

            json = json.Trim();

            // autocorrect where possible
            if (!json.StartsWith('{'))
                json = '{' + json;

            if (!json.EndsWith('}'))
            {
                int lastIndex = json.LastIndexOf('}');

                if (lastIndex == -1)
                    json += '}';
                else
                    json = json.Substring(0, lastIndex + 1);
            }

            try
            {
                var options = new JsonSerializerOptions
                {
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };

                list = JsonSerializer.Deserialize<Dictionary<string, object>>(json, options);

                if (list is null)
                    throw new Exception("JSON deserialization returned null");
            }
            catch (Exception ex)
            {
                Frontend.ShowMessageBox(
                    String.Format(Strings.Menu_FastFlagEditor_InvalidJSON, ex.Message),
                    MessageBoxImage.Error
                );

                ShowAddDialog();

                return;
            }

            if (list.Count > 16)
            {
                var result = Frontend.ShowMessageBox(
                    Strings.Menu_FastFlagEditor_LargeConfig,
                    MessageBoxImage.Warning,
                    MessageBoxButton.YesNo
                );

                if (result != MessageBoxResult.Yes)
                    return;
            }

            var conflictingFlags = App.FastFlags.Prop.Where(x => list.ContainsKey(x.Key)).Select(x => x.Key);
            bool overwriteConflicting = false;

            if (conflictingFlags.Any())
            {
                int count = conflictingFlags.Count();

                string message = String.Format(
                    Strings.Menu_FastFlagEditor_ConflictingImport,
                    count,
                    String.Join(", ", conflictingFlags.Take(25))
                );

                if (count > 25)
                    message += "...";

                var result = Frontend.ShowMessageBox(message, MessageBoxImage.Question, MessageBoxButton.YesNo);

                overwriteConflicting = result == MessageBoxResult.Yes;
            }

            foreach (var pair in list)
            {
                if (App.FastFlags.Prop.ContainsKey(pair.Key) && !overwriteConflicting)
                    continue;

                if (pair.Value is null)
                    continue;

                var val = pair.Value.ToString();

                if (val is null)
                    continue;

                if (!ValidateFlagEntry(pair.Key, val))
                    continue;

                App.FastFlags.SetValue(pair.Key, pair.Value);
            }

            ClearSearch();
        }

        private bool ValidateFlagEntry(string name, string value)
        {
            string lowerValue = value.ToLowerInvariant();
            string errorMessage = "";

            if (!_validPrefixes.Any(name.StartsWith))
                errorMessage = Strings.Menu_FastFlagEditor_InvalidPrefix;
            else if (!name.All(x => char.IsLetterOrDigit(x) || x == '_'))
                errorMessage = Strings.Menu_FastFlagEditor_InvalidCharacter;

            if (name.EndsWith("_PlaceFilter") || name.EndsWith("_DataCenterFilter"))
                errorMessage = !ValidateFilter(name, value) ? Strings.Menu_FastFlagEditor_InvalidPlaceFilter : "";
            else if ((name.StartsWith("FInt") || name.StartsWith("DFInt")) && !Int32.TryParse(value, out _))
                errorMessage = Strings.Menu_FastFlagEditor_InvalidNumberValue;
            else if ((name.StartsWith("FFlag") || name.StartsWith("DFFlag")) && lowerValue != "true" && lowerValue != "false")
                errorMessage = Strings.Menu_FastFlagEditor_InvalidBoolValue;

            if (!String.IsNullOrEmpty(errorMessage))
            {
                Frontend.ShowMessageBox(String.Format(errorMessage, name), MessageBoxImage.Error);
                return false;
            }

            return true;
        }

        private bool ValidateFilter(string name, string value)
        {
            if (name.StartsWith("FFlag") || name.StartsWith("DFFlag"))
                return _boolFilterPattern.IsMatch(value);
            if (name.StartsWith("FInt") || name.StartsWith("DFInt"))
                return _intFilterPattern.IsMatch(value);
            if (name.StartsWith("FString") || name.StartsWith("DFString") || name.StartsWith("FLog") || name.StartsWith("DFLog"))
                return _stringFilterPattern.IsMatch(value);

            return false;
        }

        private void Page_Loaded(object sender, RoutedEventArgs e) => ReloadList();

        private void DataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.Row.DataContext is not FastFlag entry)
                return;

            if (e.EditingElement is not TextBox textbox)
                return;

            switch (e.Column.Header)
            {
                case "Name":
                    string oldName = entry.Name;
                    string newName = textbox.Text;

                    if (newName == oldName)
                        return;

                    if (App.FastFlags.GetValue(newName) is not null)
                    {
                        Frontend.ShowMessageBox(Strings.Menu_FastFlagEditor_AlreadyExists, MessageBoxImage.Information);
                        e.Cancel = true;
                        textbox.Text = oldName;
                        return;
                    }

                    App.FastFlags.SetValue(oldName, null);
                    App.FastFlags.SetValue(newName, entry.Value);

                    if (!newName.Contains(_searchFilter))
                        ClearSearch();

                    entry.Name = newName;

                    break;

                case "Value":
                    string oldValue = entry.Value;
                    string newValue = textbox.Text;

                    if (!ValidateFlagEntry(entry.Name, newValue))
                    {
                        e.Cancel = true;
                        textbox.Text = oldValue;
                        return;
                    }

                    App.FastFlags.SetValue(entry.Name, newValue);

                    break;
            }
        }

        private void AddButton_Click(object sender, RoutedEventArgs e) => ShowAddDialog();

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var tempList = new List<FastFlag>();

            foreach (FastFlag entry in DataGrid.SelectedItems)
                tempList.Add(entry);

            foreach (FastFlag entry in tempList)
            {
                _fastFlagList.Remove(entry);
                App.FastFlags.SetValue(entry.Name, null);
            }
        }

        private void ToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton button)
                return;

            _showPresets = button.IsChecked ?? false;
            ReloadList();
        }

        private void ExportJSONButton_Click(object sender, RoutedEventArgs e)
        {
            string json = JsonSerializer.Serialize(App.FastFlags.Prop, new JsonSerializerOptions { WriteIndented = true });

            if (!Utilities.TrySetClipboardText(json, "FastFlagsPage::ExportJSON"))
            {
                Frontend.ShowMessageBox(
                    "Couldn't copy to the clipboard — something else on your PC is holding it open. Close any clipboard manager and try again.",
                    MessageBoxImage.Warning);
                return;
            }

            Frontend.ShowMessageBox(Strings.Menu_FastFlagEditor_JsonCopiedToClipboard, MessageBoxImage.Information);
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox textbox)
                return;

            _searchFilter = textbox.Text;
            ReloadList();
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            if (!KnownFlags.Loaded)
            {
                Frontend.ShowMessageBox("The flag list is still loading — give it a second and try again.", MessageBoxImage.Information);
                return;
            }

            var dialog = new FlagSearchDialog { Owner = Window.GetWindow(this) };
            dialog.ShowDialog();

            if (!string.IsNullOrEmpty(dialog.SelectedFlag))
                AddSingle(dialog.SelectedFlag, DefaultFlagValue(dialog.SelectedFlag));
        }

        private void LibraryButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new FastFlagLibraryDialog { Owner = Window.GetWindow(this) };
            dialog.ShowDialog();

            if (!dialog.FlagsChanged)
                return;

            // The library/backups wrote into App.FastFlags — refresh the raw grid so the edits
            // show up, and rebuild the presets VM so the preset controls re-read the new flags.
            ReloadList();
            SetupViewModel();
        }

        private static string DefaultFlagValue(string name)
        {
            if (name.StartsWith("FFlag") || name.StartsWith("DFFlag") || name.StartsWith("SFFlag"))
                return "True";

            if (name.StartsWith("FInt") || name.StartsWith("DFInt"))
                return "0";

            return "";
        }
    }
}
