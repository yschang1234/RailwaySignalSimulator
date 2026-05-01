using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace SSISimulator
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            // Auto-scroll the log DataGrid when new entries are added
            if (DataContext is ViewModels.MainViewModel vm)
                vm.LogEntries.CollectionChanged += LogEntries_CollectionChanged;

            // DataContext is set via XAML; wire up after DataContext is ready too
            DataContextChanged += (_, _) =>
            {
                if (DataContext is ViewModels.MainViewModel viewModel)
                    viewModel.LogEntries.CollectionChanged += LogEntries_CollectionChanged;
            };
        }

        private void LogEntries_CollectionChanged(object? sender,
            NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add && LogGrid.Items.Count > 0)
            {
                LogGrid.ScrollIntoView(LogGrid.Items[^1]);
            }
        }
    }
}
