using System.Windows;
using System.Windows.Input;

namespace BeastStrap.UI.Elements.Controls
{
    // Lets a script FILE be dropped onto a code box: controls:ScriptFileDrop.LoadCommand="{Binding LoadFileCommand}"
    // (the command gets the dropped file's path). Only file drops are taken. Dragging selected TEXT around
    // is left to AvalonEdit, which already handles it, so those events are never marked handled here.
    //
    // Hooked on the Preview (tunnelling) events because AvalonEdit's TextArea handles DragOver/Drop itself
    // and would otherwise refuse a file drop before we ever saw it.
    public static class ScriptFileDrop
    {
        public static readonly DependencyProperty LoadCommandProperty =
            DependencyProperty.RegisterAttached(
                "LoadCommand", typeof(ICommand), typeof(ScriptFileDrop),
                new PropertyMetadata(null, OnLoadCommandChanged));

        public static ICommand? GetLoadCommand(DependencyObject o) => (ICommand?)o.GetValue(LoadCommandProperty);
        public static void SetLoadCommand(DependencyObject o, ICommand? v) => o.SetValue(LoadCommandProperty, v);

        private static void OnLoadCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not UIElement el)
                return;

            el.PreviewDragEnter -= OnDragOver;
            el.PreviewDragOver -= OnDragOver;
            el.PreviewDrop -= OnDrop;

            if (e.NewValue is ICommand)
            {
                el.AllowDrop = true;
                el.PreviewDragEnter += OnDragOver;
                el.PreviewDragOver += OnDragOver;
                el.PreviewDrop += OnDrop;
            }
        }

        private static string? FirstFile(DragEventArgs e) =>
            e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files
                ? files[0]
                : null;

        private static void OnDragOver(object sender, DragEventArgs e)
        {
            string? file = FirstFile(e);
            if (file is null)
                return;

            var cmd = GetLoadCommand((DependencyObject)sender);
            e.Effects = cmd?.CanExecute(file) == true ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private static void OnDrop(object sender, DragEventArgs e)
        {
            string? file = FirstFile(e);
            if (file is null)
                return;

            e.Handled = true;

            var cmd = GetLoadCommand((DependencyObject)sender);
            if (cmd?.CanExecute(file) == true)
                cmd.Execute(file);
        }
    }
}
