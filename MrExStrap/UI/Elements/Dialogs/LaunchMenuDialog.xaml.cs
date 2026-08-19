using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using BeastStrap.UI.ViewModels.Dialogs;
using BeastStrap.UI.ViewModels.Installer;
using Wpf.Ui.Mvvm.Interfaces;

namespace BeastStrap.UI.Elements.Dialogs
{
    /// <summary>
    /// Interaction logic for LaunchMenuDialog.xaml
    /// </summary>
    public partial class LaunchMenuDialog
    {
        public NextAction CloseAction = NextAction.Terminate;

        public LaunchMenuDialog()
        {
            var viewModel = new LaunchMenuViewModel();
            viewModel.CloseWindowRequest += (_, closeAction) =>
            {
                CloseAction = closeAction;
                Close();
            };

            DataContext = viewModel;

            InitializeComponent();

            // v420.28: fire-and-forget LIVE-change + executor-update toast checks
            // when the launcher opens. UpdateMonitor honours its own toggles and
            // budgets so this is safe to call without gating here.
            _ = BeastStrap.Utility.UpdateMonitor.CheckAllAsync();
        }
    }
}
