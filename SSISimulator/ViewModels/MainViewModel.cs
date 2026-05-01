using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO.Ports;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using SSISimulator.Commands;
using SSISimulator.Core;
using SSISimulator.Models;

namespace SSISimulator.ViewModels
{
    /// <summary>
    /// Main ViewModel for the SSI Simulator (master / controller side).
    ///
    /// Telegram frame format (TFM UM):
    ///   [STX=0x02] [ADDR] [DATA] [BCC] [ETX=0x03]
    ///
    ///   STX  – Start of text
    ///   ADDR – TFM UM unit address (configurable, default 0x01)
    ///   DATA – Control byte: bit 0 = D0 … bit 7 = D7
    ///   BCC  – XOR checksum of ADDR and DATA bytes
    ///   ETX  – End of text
    ///
    /// The DispatcherTimer fires every ~850 ms (major cycle). On each tick:
    ///   1. A control telegram is built from the current D0–D7 state.
    ///   2. The telegram is transmitted via the open serial port (TX).
    ///
    /// The response telegram is received asynchronously and processed by
    /// <see cref="SSIMasterModule"/>; the DATA byte is mapped to the eight
    /// Status0–Status7 indicator properties.
    /// </summary>
    public class MainViewModel : INotifyPropertyChanged
    {
        private const int MajorCycleMs = 850;

        // ── Dependencies ─────────────────────────────────────────────────────────
        private readonly ISerialCommunication _comm;
        private readonly SSIMasterModule      _module;
        private readonly DispatcherTimer      _timer;

        // ── Backing fields ───────────────────────────────────────────────────────
        private string? _selectedComPort;
        private string  _selectedBaudRate = "9600";
        private bool    _isConnected;
        private string  _statusMessage    = "Disconnected";

        // Control bits D0–D7
        private bool _d0, _d1, _d2, _d3, _d4, _d5, _d6, _d7;

        // ────────────────────────────────────────────────────────────────────────
        public MainViewModel(ISerialCommunication comm)
        {
            _comm   = comm ?? throw new ArgumentNullException(nameof(comm));
            _module = new SSIMasterModule(_comm);

            // Wire module events → UI updates
            _module.StatusChanged    += (_, _) => RaiseStatusProperties();
            _module.ResponseReceived += OnResponseReceived;
            _module.ControlSent      += OnControlSent;
            _module.SendError        += (_, ex) => AddLog("--", $"Tx error: {ex.Message}");

            if (_comm is Services.SerialCommunicationService svc)
                svc.ReceiveError += (_, ex) => AddLog("--", $"Rx error: {ex.Message}");

            // Populate available COM ports
            RefreshComPorts();

            // Supported baud rates
            BaudRates = new ObservableCollection<string>
            {
                "1200", "2400", "4800", "9600", "19200", "38400", "57600", "115200"
            };

            // Log collection
            LogEntries = new ObservableCollection<LogEntry>();

            // Commands
            ConnectCommand      = new RelayCommand(Connect,    () => !IsConnected && SelectedComPort != null);
            DisconnectCommand   = new RelayCommand(Disconnect, () => IsConnected);
            RefreshPortsCommand = new RelayCommand(RefreshComPorts);

            // Major-cycle timer
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(MajorCycleMs) };
            _timer.Tick += OnMajorCycleTick;
        }

        // ── Collections ──────────────────────────────────────────────────────────
        public ObservableCollection<string> ComPorts { get; } = new();
        public ObservableCollection<string> BaudRates { get; }
        public ObservableCollection<LogEntry> LogEntries { get; }

        // ── Commands ─────────────────────────────────────────────────────────────
        public ICommand ConnectCommand { get; }
        public ICommand DisconnectCommand { get; }
        public ICommand RefreshPortsCommand { get; }

        // ── Serial-port settings ─────────────────────────────────────────────────
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

        /// <summary>TFM UM unit address used in every outgoing control telegram.</summary>
        public byte TfmUmAddress
        {
            get => _module.TfmUmAddress;
            set { _module.TfmUmAddress = value; OnPropertyChanged(); }
        }

        // ── Status ───────────────────────────────────────────────────────────────
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

        // ── Control bits D0–D7 (two-way bound to ToggleButtons) ──────────────────
        public bool D0 { get => _d0; set { _d0 = value; OnPropertyChanged(); } }
        public bool D1 { get => _d1; set { _d1 = value; OnPropertyChanged(); } }
        public bool D2 { get => _d2; set { _d2 = value; OnPropertyChanged(); } }
        public bool D3 { get => _d3; set { _d3 = value; OnPropertyChanged(); } }
        public bool D4 { get => _d4; set { _d4 = value; OnPropertyChanged(); } }
        public bool D5 { get => _d5; set { _d5 = value; OnPropertyChanged(); } }
        public bool D6 { get => _d6; set { _d6 = value; OnPropertyChanged(); } }
        public bool D7 { get => _d7; set { _d7 = value; OnPropertyChanged(); } }

        // ── Status indicator bits S0–S7 (received from field, read-only) ─────────
        public bool S0 { get => _module.StatusBits[0]; }
        public bool S1 { get => _module.StatusBits[1]; }
        public bool S2 { get => _module.StatusBits[2]; }
        public bool S3 { get => _module.StatusBits[3]; }
        public bool S4 { get => _module.StatusBits[4]; }
        public bool S5 { get => _module.StatusBits[5]; }
        public bool S6 { get => _module.StatusBits[6]; }
        public bool S7 { get => _module.StatusBits[7]; }

        // ── Connect / Disconnect ─────────────────────────────────────────────────
        private void Connect()
        {
            if (SelectedComPort is null) return;

            try
            {
                _comm.Open(SelectedComPort, int.Parse(SelectedBaudRate));
                IsConnected   = true;
                StatusMessage = $"Connected – {SelectedComPort} @ {SelectedBaudRate} bps";
                AddLog("--", $"Port opened: {SelectedComPort} @ {SelectedBaudRate} bps");

                _timer.Start();
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
                AddLog("--", $"Connect error: {ex.Message}");
            }
        }

        private void Disconnect()
        {
            _timer.Stop();

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

        // ── Major-cycle tick ─────────────────────────────────────────────────────
        private void OnMajorCycleTick(object? sender, EventArgs e)
        {
            if (!_comm.IsOpen) return;
            _module.SendControlTelegram(new[] { _d0, _d1, _d2, _d3, _d4, _d5, _d6, _d7 });
        }

        // ── Module event handlers ─────────────────────────────────────────────────
        private void RaiseStatusProperties()
        {
            for (int i = 0; i < 8; i++)
                OnPropertyChanged($"S{i}");
        }

        private void OnControlSent(object? sender, SsiTelegramEventArgs e)
        {
            byte[] frame = SSIMasterModule.BuildTelegram(e.Address, e.Data);
            AddLog("TX", BytesToHex(frame));
        }

        private void OnResponseReceived(object? sender, SsiTelegramEventArgs e)
        {
            byte[] frame = SSIMasterModule.BuildTelegram(e.Address, e.Data);
            AddLog("RX", BytesToHex(frame), $"Status=0x{e.Data:X2}");
        }

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

            // Must update ObservableCollection on the UI thread
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                LogEntries.Add(entry);
                // Keep the log from growing indefinitely
                while (LogEntries.Count > 500)
                    LogEntries.RemoveAt(0);
            });
        }

        private static string BytesToHex(byte[] bytes) =>
            string.Join(" ", bytes.Select(b => b.ToString("X2")));

        // ── INotifyPropertyChanged ────────────────────────────────────────────────
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
