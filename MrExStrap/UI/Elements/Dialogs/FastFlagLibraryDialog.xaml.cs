using System.Windows;
using System.Windows.Controls;

using BeastStrap.Models;
using BeastStrap.UI.ViewModels.Settings;

namespace BeastStrap.UI.Elements.Dialogs
{
    // Community fast-flag library dialog: browse bundled presets and apply them to the profile
    // being edited (App.FastFlags), and snapshot / restore named backups of that profile's flag set.
    // The FastFlags page owns App.FastFlags.EditingProfileId, so "the profile you're editing"
    // is whatever profile the page selected — the dialog never switches profiles.
    public partial class FastFlagLibraryDialog
    {
        // The page reads this after ShowDialog to know whether it needs to reload its grid/VM.
        public bool FlagsChanged { get; private set; }

        private readonly List<FastFlagPreset> _presets = new();

        public FastFlagLibraryDialog()
        {
            InitializeComponent();

            _presets.AddRange(FastFlagLibrary.BundledPresets);

            CategoryFilter.ItemsSource = new[] { "All categories" }
                .Concat(_presets.Select(p => p.Category).Distinct().OrderBy(c => c))
                .ToList();

            CategoryFilter.SelectedIndex = 0;
            RefreshPresetList();
            RefreshBackupList();

            ApplyPresetButton.IsEnabled = false;
            RestoreBackupButton.IsEnabled = false;
            DeleteBackupButton.IsEnabled = false;
        }

        // ---- presets tab ----

        private void PresetSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshPresetList();

        private void CategoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshPresetList();

        private void RefreshPresetList()
        {
            string search = (PresetSearchBox?.Text ?? "").Trim().ToLowerInvariant();
            string category = CategoryFilter?.SelectedItem as string ?? "All categories";

            IEnumerable<FastFlagPreset> filtered = _presets;

            if (category != "All categories")
                filtered = filtered.Where(p => p.Category == category);

            if (search.Length > 0)
                filtered = filtered.Where(p =>
                    p.Name.ToLowerInvariant().Contains(search) ||
                    p.Description.ToLowerInvariant().Contains(search) ||
                    p.Flags.Keys.Any(k => k.ToLowerInvariant().Contains(search)));

            var items = filtered.ToList();
            PresetList.ItemsSource = items;

            PresetDetailText.Text = items.Count == 1
                ? "1 preset"
                : $"{items.Count} presets";
        }

        private void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var preset = PresetList.SelectedItem as FastFlagPreset;

            ApplyPresetButton.IsEnabled = preset is not null;

            if (preset is null)
            {
                PresetDetailText.Text = "";
                return;
            }

            string preview = string.Join(", ", preset.Flags.Keys.OrderBy(k => k));
            PresetDetailText.Text = $"Will apply: {preview}";
        }

        private void ApplyPresetButton_Click(object sender, RoutedEventArgs e)
        {
            if (PresetList.SelectedItem is not FastFlagPreset preset)
                return;

            if (preset.Flags.Count == 0)
            {
                Frontend.ShowMessageBox("This preset is empty — nothing to apply.", MessageBoxImage.Information);
                return;
            }

            // Count how many flags will actually change so we can confirm when it's meaningful.
            int newCount = 0;
            int overwriteCount = 0;

            foreach (var pair in preset.Flags)
            {
                if (App.FastFlags.GetValue(pair.Key) is null)
                    newCount++;
                else
                    overwriteCount++;
            }

            if (newCount == 0 && overwriteCount == 0)
            {
                Frontend.ShowMessageBox("Everything in this preset is already set — nothing to do.", MessageBoxImage.Information);
                return;
            }

            string body = $"Apply the '{preset.Name}' preset to the profile you're editing?\n\n";

            if (newCount > 0)
                body += $"\u2022 Adds {newCount} new flag{(newCount == 1 ? "" : "s")}\n";

            if (overwriteCount > 0)
                body += $"\u2022 Overwrites {overwriteCount} existing flag{(overwriteCount == 1 ? "" : "s")}\n";

            body += "\nThis changes the in-memory flag set — Save on the Fast Flags page writes it to the profile.";

            var result = Frontend.ShowMessageBox(body, MessageBoxImage.Question, MessageBoxButton.YesNo, MessageBoxResult.Yes);

            if (result != MessageBoxResult.Yes)
                return;

            foreach (var pair in preset.Flags)
                App.FastFlags.SetValue(pair.Key, pair.Value);

            App.FastFlags.Save();
            FlagsChanged = true;
            Frontend.ShowMessageBox($"Applied '{preset.Name}' ({preset.Flags.Count} flags) and saved to the profile.", MessageBoxImage.Information);
        }

        // ---- backups tab ----

        private void RefreshBackupList()
        {
            var backups = FastFlagLibrary.ListBackups();
            BackupList.ItemsSource = backups;
        }

        private void SaveBackupButton_Click(object sender, RoutedEventArgs e)
        {
            string name = BackupNameBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                Frontend.ShowMessageBox("Give the backup a name first.", MessageBoxImage.Information);
                return;
            }

            if (App.FastFlags.Prop.Count == 0)
            {
                Frontend.ShowMessageBox("The profile you're editing has no flags to back up.", MessageBoxImage.Information);
                return;
            }

            string? saved = FastFlagLibrary.SaveBackup(name);

            if (saved is null)
            {
                Frontend.ShowMessageBox("Couldn't write the backup — check the logs for details.", MessageBoxImage.Error);
                return;
            }

            BackupNameBox.Text = "";
            RefreshBackupList();

            var savedEntry = (BackupList.ItemsSource as List<FastFlagBackupEntry>)?
                .FirstOrDefault(b => b.Name == saved);

            if (savedEntry is not null)
                BackupList.SelectedItem = savedEntry;

            Frontend.ShowMessageBox($"Backed up {App.FastFlags.Prop.Count} flags as '{saved}'.", MessageBoxImage.Information);
        }

        private void BackupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool hasSelection = BackupList.SelectedItem is not null;
            RestoreBackupButton.IsEnabled = hasSelection;
            DeleteBackupButton.IsEnabled = hasSelection;
        }

        private void RestoreBackupButton_Click(object sender, RoutedEventArgs e)
        {
            if (BackupList.SelectedItem is not FastFlagBackupEntry entry)
                return;

            var flags = FastFlagLibrary.LoadBackup(entry.Name);
            if (flags is null)
            {
                Frontend.ShowMessageBox($"Couldn't read backup '{entry.Name}'.", MessageBoxImage.Error);
                return;
            }

            int overwriteCount = flags.Keys.Count(k => App.FastFlags.GetValue(k) is not null);
            int newCount = flags.Count - overwriteCount;

            string body =
                $"Restore '{entry.Name}' onto the profile you're editing?\n\n" +
                $"It contains {flags.Count} flags — {newCount} new, {overwriteCount} would overwrite existing values.\n\n" +
                "This replaces the matching flags and saves them to the profile.";

            var result = Frontend.ShowMessageBox(body, MessageBoxImage.Question, MessageBoxButton.YesNo, MessageBoxResult.Yes);

            if (result != MessageBoxResult.Yes)
                return;

            foreach (var pair in flags)
                App.FastFlags.SetValue(pair.Key, pair.Value);

            App.FastFlags.Save();
            FlagsChanged = true;
            Frontend.ShowMessageBox($"Restored '{entry.Name}' ({flags.Count} flags) and saved to the profile.", MessageBoxImage.Information);
        }

        private void DeleteBackupButton_Click(object sender, RoutedEventArgs e)
        {
            if (BackupList.SelectedItem is not FastFlagBackupEntry entry)
                return;

            var result = Frontend.ShowMessageBox(
                $"Delete backup '{entry.Name}'? This only removes the saved snapshot — your current flags are untouched.",
                MessageBoxImage.Warning,
                MessageBoxButton.YesNo,
                MessageBoxResult.No);

            if (result != MessageBoxResult.Yes)
                return;

            FastFlagLibrary.DeleteBackup(entry.Name);
            RefreshBackupList();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}