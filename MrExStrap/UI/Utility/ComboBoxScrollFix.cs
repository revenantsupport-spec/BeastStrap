using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BeastStrap.UI.Utility
{
    // Fixes the long-standing WPF annoyance where mousewheeling over a ComboBox changes the
    // selected item even though the dropdown is closed. We swallow the wheel event on the
    // ComboBox and forward it to the nearest ancestor ScrollViewer so the page still scrolls
    // normally with the cursor wherever it happens to be.
    public static class ComboBoxScrollFix
    {
        public static void HandleWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not Control control)
                return;

            // Only intercept when the dropdown is closed. If the user opened it on purpose,
            // wheeling through items is the expected behaviour.
            if (sender is ComboBox cb && cb.IsDropDownOpen)
                return;

            e.Handled = true;

            DependencyObject? parent = VisualTreeHelper.GetParent(control);
            while (parent is not null && parent is not ScrollViewer)
                parent = VisualTreeHelper.GetParent(parent);

            if (parent is ScrollViewer sv)
            {
                var forwarded = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = UIElement.MouseWheelEvent,
                    Source = sender
                };
                sv.RaiseEvent(forwarded);
            }
        }
    }
}
