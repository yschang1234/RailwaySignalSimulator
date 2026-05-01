using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO.Ports;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using TFMUMSimulator.Commands;
using TFMUMSimulator.Core;
using TFMUMSimulator.Models;

namespace TFMUMSimulator.ViewModels
{
    /// <summary>
    /// WPF MVVM ViewModel for the TFM UM Simulator (field-device / slave side).
    ///
    /// This ViewModel wraps <see cref="TFMUMModule"/> (which holds all pure logic) and
    /// adds MVVM bindings, WPF commands, a DispatcherTimer-driven safety cycle, and a
    /// communication log.
    ///
    /// View responsibilities:
    ///   • Serial port settings UI
    ///   • Output0–Output7 indicators (receive-only, driven by SSI master commands)
    ///   • Input0–Input7 ToggleButtons (user-controlled field-status simulation)
    ///   • Config Board panel: ModuleAddress, ConfigNormalValue,
    ///     ConfigMaintenanceValue, IsFaultSimulated, IsShutDown
    /// </summary>
    public class TFMUMViewModel : INotifyPropertyChanged
    {
        // ── Dependencies ─────────────────────────────────────────────────────────
        private readonly ISerialCommunication _comm;
        private readonly TFMUMModule          _module;
        private readonly DispatcherTimer      _safetyTimer;

        // ── Backing fields ───────────────────────────────────────────────────────
        private string? _selectedComPort;
        private string  _selectedBaudRate = "9600";
        private bool    _isConnected;
        private string  _statusMessage   = "Disconnected";

        // ────────────────────────────────────────────────────────────────────────
        public TFMUMViewModel(ISerialCommunication comm)
        {
            _comm   = comm ?? throw new ArgumentNullException(nameof(comm));
            _module = new TFMUMModule(_comm);

            // Wire module events → UI updates
            _module.OutputsChanged      += (_, _) => RaiseOutputProperties();
            _module.ShutdownStateChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(IsShutDown));
                OnPropertyChanged(nameof(ConfigMismatchCycleCount));
                RelayCommand.RaiseCanExecuteChanged();
                if (_module.IsShutDown)
                    StatusMessage = "⚠ SHUTDOWN – config mismatch exceeded threshold";
            };
            _module.ConfigChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(ModuleAddress));
                OnPropertyChanged(nameof(ConfigNormalValue));
                OnPropertyChanged(nameof(ConfigMaintenanceValue));
                OnPropertyChanged(nameof(IsFaultSimulated));
                OnPropertyChanged(nameof(IsConfigValid));
            };
            _module.TelegramReceived += OnTelegramReceived;
            _module.TelegramSent     += OnTelegramSent;
            _module.SendError        += (_, ex) => AddLog("--", $"Tx error: {ex.Message}");

            RefreshComPorts();
            BaudRates  = new ObservableCollection<string>
            {
                "1200", "2400", "4800", "9600", "19200", "38400", "57600", "115200"
            };
            LogEntries = new ObservableCollection<LogEntry>();

            ConnectCommand       = new RelayCommand(Connect,    () => !IsConnected && SelectedComPort != null);
            DisconnectCommand    = new RelayCommand(Disconnect, () => IsConnected);
            RefreshPortsCommand  = new RelayCommand(RefreshComPorts);
            SimulateFaultCommand = new RelayCommand(ToggleFaultSimulation);
            ResetShutdownCommand = new RelayCommand(ResetShutdown, () => IsShutDown);

            _safetyTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(TFMUMModule.SafetyCycleMs)
            };
            _safetyTimer.Tick += (_, _) =>
            {
                _module.CheckConfigIntegrity();
                // Refresh counter display every tick
                OnPropertyChanged(nameof(ConfigMismatchCycleCount));
            };
            _safetyTimer.Start();
        }

        // ── Collections ──────────────────────────────────────────────────────────
        public ObservableCollection<string>   ComPorts   { get; } = new();
        public ObservableCollection<string>   BaudRates  { get; }
        public ObservableCollection<LogEntry> LogEntries { get; }

        // ── Commands ─────────────────────────────────────────────────────────────
        public ICommand ConnectCommand       { get; }
        public ICommand DisconnectCommand    { get; }
        public ICommand RefreshPortsCommand  { get; }
        public ICommand SimulateFaultCommand { get; }
        public ICommand ResetShutdownCommand { get; }

        // ── Serial port settings ─────────────────────────────────────────────────
        public string? SelectedComPort
        {
            get => _selectedComPort;
            set { _selectedComPort = value; OnPropertyChanged(); RelayCommand.RaiseCanExecuteChanged(); }
        }

        public string SelectedBaudRate
        {
            get => _selectedBaudRate;
            set { _selectedBaudRate = value; OnPropertyChanged(); }
        }

        public bool IsConnected
        {
            get => _isConnected;
            private set
            {
                _isConnected = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDisconnected));
                RelayCommand.RaiseCanExecuteChanged();
            }
        }

        public bool IsDisconnected => !IsConnected;

        public string StatusMessage
        {
            get => _statusMessage;
            private set { _statusMessage = value; OnPropertyChanged(); }
        }

        // ── Config Board (delegated to TFMUMModule) ──────────────────────────────
        public byte ModuleAddress
        {
            get => _module.ModuleAddress;
            set { _module.ModuleAddress = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsConfigValid)); }
        }

        public byte ConfigNormalValue
        {
            get => _module.ConfigNormalValue;
            set { _module.ConfigNormalValue = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsConfigValid)); }
        }

        public byte ConfigMaintenanceValue
        {
            get => _module.ConfigMaintenanceValue;
            set { _module.ConfigMaintenanceValue = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsConfigValid)); }
        }

        public bool IsFaultSimulated
        {
            get => _module.IsFaultSimulated;
            set { _module.IsFaultSimulated = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsConfigValid)); }
        }

        public bool IsConfigValid => _module.IsConfigValid;

        public int ConfigMismatchCycleCount => _module.MismatchCycleCount;

        public bool IsShutDown => _module.IsShutDown;

        // ── Output indicators O0–O7 (receive-only) ───────────────────────────────
        public bool Output0 => _module.Outputs[0];
        public bool Output1 => _module.Outputs[1];
        public bool Output2 => _module.Outputs[2];
        public bool Output3 => _module.Outputs[3];
        public bool Output4 => _module.Outputs[4];
        public bool Output5 => _module.Outputs[5];
        public bool Output6 => _module.Outputs[6];
        public bool Output7 => _module.Outputs[7];

        // ── Input ToggleButtons I0–I7 (two-way) ─────────────────────────────────
        public bool Input0
        {
            get => _module.Inputs[0];
            set { _module.Inputs[0] = value; OnPropertyChanged(); }
        }
        public bool Input1
        {
            get => _module.Inputs[1];
            set { _module.Inputs[1] = value; OnPropertyChanged(); }
        }
        public bool Input2
        {
            get => _module.Inputs[2];
            set { _module.Inputs[2] = value; OnPropertyChanged(); }
        }
        public bool Input3
        {
            get => _module.Inputs[3];
            set { _module.Inputs[3] = value; OnPropertyChanged(); }
        }
        public bool Input4
        {
            get => _module.Inputs[4];
            set { _module.Inputs[4] = value; OnPropertyChanged(); }
        }
        public bool Input5
        {
            get => _module.Inputs[5];
            set { _module.Inputs[5] = value; OnPropertyChanged(); }
        }
        public bool Input6
        {
            get => _module.Inputs[6];
            set { _module.Inputs[6] = value; OnPropertyChanged(); }
        }
        public bool Input7
        {
            get => _module.Inputs[7];
            set { _module.Inputs[7] = value; OnPropertyChanged(); }
        }

        // ── Connect / Disconnect ─────────────────────────────────────────────────
        private void Connect()
        {
            if (SelectedComPort is null) return;
            try
            {
                if (_comm is Services.SerialCommunicationService svc)
                    svc.ReceiveError += (_, ex) => AddLog("--", $"Rx error: {ex.Message}");

                _comm.Open(SelectedComPort, int.Parse(SelectedBaudRate));
                IsConnected   = true;
                StatusMessage = $"Connected – {SelectedComPort} @ {SelectedBaudRate} bps";
                AddLog("--", $"Port opened: {SelectedComPort} @ {SelectedBaudRate} bps");
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
                AddLog("--", $"Connect error: {ex.Message}");
            }
        }

        private void Disconnect()
        {
            try { _comm.Close(); }
            catch (Exception ex) { AddLog("--", $"Disconnect error: {ex.Message}"); }
            IsConnected   = false;
            StatusMessage = "Disconnected";
            AddLog("--", "Port closed");
        }

        private void RefreshComPorts()
        {
            var ports = SerialPort.GetPortNames().OrderBy(p => p).ToArray();
            ComPorts.Clear();
            foreach (var p in ports)
                ComPorts.Add(p);
            if (SelectedComPort == null || !ComPorts.Contains(SelectedComPort))
                SelectedComPort = ComPorts.FirstOrDefault();
        }

        private void ToggleFaultSimulation() => IsFaultSimulated = !IsFaultSimulated;

        private void ResetShutdown()
        {
            _module.ResetShutdown();
            OnPropertyChanged(nameof(IsShutDown));
            OnPropertyChanged(nameof(ConfigMismatchCycleCount));
            RelayCommand.RaiseCanExecuteChanged();
            StatusMessage = IsConnected
                ? $"Connected – {SelectedComPort} @ {SelectedBaudRate} bps"
                : "Disconnected";
            AddLog("--", "Shutdown state reset by operator");
        }

        // ── Module event handlers ─────────────────────────────────────────────────
        private void RaiseOutputProperties()
        {
            for (int i = 0; i < 8; i++)
                OnPropertyChanged($"Output{i}");
        }

        private void OnTelegramReceived(object? sender, TelegramEventArgs e) =>
            AddLog("RX", $"02 {e.Address:X2} {e.Data:X2} {(byte)(e.Address ^ e.Data):X2} 03",
                   $"Addr=0x{e.Address:X2} Data=0x{e.Data:X2}");

        private void OnTelegramSent(object? sender, TelegramEventArgs e) =>
            AddLog("TX", $"02 {e.Address:X2} {e.Data:X2} {(byte)(e.Address ^ e.Data):X2} 03",
                   IsShutDown ? "SHUTDOWN – data forced 0x00" : $"Input=0x{e.Data:X2}");

        // ── Log helper ───────────────────────────────────────────────────────────
        private void AddLog(string direction, string rawHex, string description = "")
        {
            var entry = new LogEntry
            {
                Timestamp   = DateTime.Now,
                Direction   = direction,
                RawHex      = rawHex,
                Description = description
            };
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                LogEntries.Add(entry);
                while (LogEntries.Count > 500)
                    LogEntries.RemoveAt(0);
            });
        }

        // ── INotifyPropertyChanged ────────────────────────────────────────────────
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
