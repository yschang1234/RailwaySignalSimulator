using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using SSISimulator.Services;
using SSISimulator.ViewModels;

namespace SSISimulator
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel(new SerialCommunicationService());

            // Auto-scroll the log DataGrid when new entries are added
            if (DataContext is MainViewModel vm)
                vm.LogEntries.CollectionChanged += LogEntries_CollectionChanged;
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
