using System;
using System.Collections.Specialized;
using System.Windows.Controls;

using BeastStrap.UI.ViewModels.Settings;

namespace BeastStrap.UI.Elements.Settings.Pages
{
    public partial class BanAsyncPage
    {
        private readonly BanAsyncViewModel _viewModel;

        public BanAsyncPage()
        {
            _viewModel = new BanAsyncViewModel();
            DataContext = _viewModel;
            InitializeComponent();

            // Auto-scroll the activity log to the newest line as entries arrive.
            _viewModel.ActivityLog.CollectionChanged += OnActivityLogChanged;

            // When an action starts, bring the log into view so live progress is visible.
            _viewModel.ScrollToLogRequested += (_, _) =>
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ActivityLogBorder.BringIntoView();
                    ScrollLogToEnd();
                }));
        }

        private void OnActivityLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
                Dispatcher.BeginInvoke(new Action(ScrollLogToEnd));
        }

        private void ScrollLogToEnd()
        {
            if (ActivityLogList.Items.Count > 0)
                ActivityLogList.ScrollIntoView(ActivityLogList.Items[ActivityLogList.Items.Count - 1]);
        }
    }
}
