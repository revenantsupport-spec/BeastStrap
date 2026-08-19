using System.IO;
using System.Windows;
using System.Xml;

using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace BeastStrap.UI.Elements.Controls
{
    // Bridges AvalonEdit's TextEditor (whose Text is not a DependencyProperty) into MVVM. Set
    // controls:CodeEditor.BoundText="{Binding ...}" on a TextEditor for a two-way text binding; on first
    // attach it also loads and applies the embedded Lua/Luau syntax highlighting.
    public static class CodeEditor
    {
        // Default is null (not "") on purpose: binding an empty ScriptInput at load must count as a change
        // ("" != null) so the callback below runs once to install the highlighting + TextChanged sync. With
        // a "" default, an empty-string bind is a no-op and neither would ever get wired up.
        public static readonly DependencyProperty BoundTextProperty =
            DependencyProperty.RegisterAttached(
                "BoundText", typeof(string), typeof(CodeEditor),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundTextChanged));

        public static string GetBoundText(DependencyObject o) => (string)o.GetValue(BoundTextProperty);
        public static void SetBoundText(DependencyObject o, string v) => o.SetValue(BoundTextProperty, v);

        // Marks an editor whose TextChanged we've already wired, so we only subscribe once.
        private static readonly DependencyProperty HookedProperty =
            DependencyProperty.RegisterAttached("Hooked", typeof(bool), typeof(CodeEditor), new PropertyMetadata(false));

        private static IHighlightingDefinition? _lua;

        private static void OnBoundTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TextEditor editor)
                return;

            if (!(bool)editor.GetValue(HookedProperty))
            {
                editor.SetValue(HookedProperty, true);

                var lua = LoadLua();
                if (lua != null)
                    editor.SyntaxHighlighting = lua;

                editor.TextChanged += (s, _) =>
                {
                    var ed = (TextEditor)s!;
                    // Only push back if it actually differs — prevents a set→TextChanged→set loop.
                    if (GetBoundText(ed) != ed.Text)
                        SetBoundText(ed, ed.Text);
                };
            }

            string newText = e.NewValue as string ?? "";
            if (editor.Text != newText)
                editor.Text = newText;
        }

        private static IHighlightingDefinition? LoadLua()
        {
            if (_lua != null)
                return _lua;
            try
            {
                using Stream stream = Resource.GetStream("Lua.xshd");
                using XmlReader reader = XmlReader.Create(stream);
                _lua = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("CodeEditor", ex);
            }
            return _lua;
        }
    }
}
