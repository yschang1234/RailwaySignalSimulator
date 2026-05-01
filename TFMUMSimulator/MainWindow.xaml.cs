using System.Windows;
using TFMUMSimulator.Services;
using TFMUMSimulator.ViewModels;

namespace TFMUMSimulator
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new TFMUMViewModel(new SerialCommunicationService());
        }
    }
}
