using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO.Ports;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Input;
using System.Windows.Threading;
using SSISimulator.Commands;
using SSISimulator.Models;

namespace SSISimulator.ViewModels
{
    /// <summary>
    /// Main ViewModel for the SSI Simulator.
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
    ///   3. Any bytes available in the receive buffer are read (RX) and parsed.
    ///
    /// The response telegram is expected in the same format; the DATA byte of the
    /// response is mapped to the eight Status0–Status7 indicator properties.
    /// </summary>
    public class MainViewModel : INotifyPropertyChanged
    {
        // ── Telegram constants ──────────────────────────────────────────────────
        private const byte STX = 0x02;
        private const byte ETX = 0x03;
        private const int MajorCycleMs = 850;

        // ── Serial port ─────────────────────────────────────────────────────────
        private SerialPort? _port;
        private readonly DispatcherTimer _timer;
        private readonly List<byte> _rxBuffer = new();

        // ── Backing fields ───────────────────────────────────────────────────────
        private string? _selectedComPort;
        private string _selectedBaudRate = "9600";
        private bool _isConnected;
        private byte _tfmUmAddress = 0x01;
        private string _statusMessage = "Disconnected";

        // Control bits D0–D7
        private bool _d0, _d1, _d2, _d3, _d4, _d5, _d6, _d7;

        // Status indicator bits S0–S7 (received from field)
        private bool _s0, _s1, _s2, _s3, _s4, _s5, _s6, _s7;

        // ────────────────────────────────────────────────────────────────────────
        public MainViewModel()
        {
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
            ConnectCommand    = new RelayCommand(Connect,    () => !IsConnected && SelectedComPort != null);
            DisconnectCommand = new RelayCommand(Disconnect, () => IsConnected);
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
            get => _tfmUmAddress;
            set { _tfmUmAddress = value; OnPropertyChanged(); }
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
        public bool S0 { get => _s0; private set { _s0 = value; OnPropertyChanged(); } }
        public bool S1 { get => _s1; private set { _s1 = value; OnPropertyChanged(); } }
        public bool S2 { get => _s2; private set { _s2 = value; OnPropertyChanged(); } }
        public bool S3 { get => _s3; private set { _s3 = value; OnPropertyChanged(); } }
        public bool S4 { get => _s4; private set { _s4 = value; OnPropertyChanged(); } }
        public bool S5 { get => _s5; private set { _s5 = value; OnPropertyChanged(); } }
        public bool S6 { get => _s6; private set { _s6 = value; OnPropertyChanged(); } }
        public bool S7 { get => _s7; private set { _s7 = value; OnPropertyChanged(); } }

        // ── Connect / Disconnect ─────────────────────────────────────────────────
        private void Connect()
        {
            if (SelectedComPort is null) return;

            try
            {
                _port = new SerialPort(
                    SelectedComPort,
                    int.Parse(SelectedBaudRate),
                    Parity.None,
                    8,
                    StopBits.One)
                {
                    ReadTimeout  = 500,
                    WriteTimeout = 500,
                    ReceivedBytesThreshold = 1
                };
                _port.DataReceived += OnDataReceived;
                _port.Open();

                IsConnected = true;
                StatusMessage = $"Connected – {SelectedComPort} @ {SelectedBaudRate} bps";
                AddLog("--", $"Port opened: {SelectedComPort} @ {SelectedBaudRate} bps");

                _timer.Start();
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
                AddLog("--", $"Connect error: {ex.Message}");
                _port?.Dispose();
                _port = null;
            }
        }

        private void Disconnect()
        {
            _timer.Stop();

            if (_port is { IsOpen: true })
            {
                _port.DataReceived -= OnDataReceived;
                _port.Close();
            }
            _port?.Dispose();
            _port = null;

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
            if (_port is not { IsOpen: true }) return;

            try
            {
                // Build and send control telegram
                byte[] telegram = BuildControlTelegram();
                _port.Write(telegram, 0, telegram.Length);
                AddLog("TX", BytesToHex(telegram));

                // Drain any bytes already available in the input buffer
                ReadAndProcessResponse();
            }
            catch (Exception ex)
            {
                AddLog("--", $"Tx error: {ex.Message}");
            }
        }

        // ── Serial data received event ────────────────────────────────────────────
        private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            // Read bytes into local buffer; actual processing happens on the UI thread
            // to keep the dispatcher-timer flow coherent.
            if (_port is not { IsOpen: true }) return;
            try
            {
                int available = _port.BytesToRead;
                if (available <= 0) return;
                byte[] buf = new byte[available];
                _port.Read(buf, 0, available);
                lock (_rxBuffer) { _rxBuffer.AddRange(buf); }
            }
            catch (Exception ex)
            {
                // Log the error; the next major-cycle tick will attempt recovery
                AddLog("--", $"Rx read error: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads accumulated RX bytes (thread-safe) and attempts to parse a complete
        /// response telegram.  Called from the UI-thread major-cycle tick.
        /// </summary>
        private void ReadAndProcessResponse()
        {
            byte[] pending;
            lock (_rxBuffer)
            {
                if (_rxBuffer.Count == 0) return;
                pending = _rxBuffer.ToArray();
                _rxBuffer.Clear();
            }

            // Log raw bytes
            AddLog("RX", BytesToHex(pending));

            // Search for a complete frame: STX … ETX (minimum 5 bytes)
            int start = Array.IndexOf(pending, STX);
            if (start < 0) return;

            int end = Array.IndexOf(pending, ETX, start + 1);
            if (end < 0 || end - start < 4) return; // incomplete frame

            // Expected layout: [STX] [ADDR] [DATA] [BCC] [ETX]
            byte addr = pending[start + 1];
            byte data = pending[start + 2];
            byte bcc  = pending[start + 3];

            byte expectedBcc = (byte)(addr ^ data);
            if (bcc != expectedBcc)
            {
                AddLog("RX", $"BCC mismatch (got 0x{bcc:X2}, expected 0x{expectedBcc:X2})");
                return;
            }

            // Update status indicators on the UI thread
            ApplyStatusByte(data);
        }

        // ── Telegram helpers ─────────────────────────────────────────────────────
        /// <summary>
        /// Builds a 5-byte TFM UM control telegram:
        ///   [STX] [ADDR] [DATA] [BCC=ADDR^DATA] [ETX]
        /// </summary>
        private byte[] BuildControlTelegram()
        {
            byte addr = _tfmUmAddress;
            byte data = BuildControlByte();
            byte bcc  = (byte)(addr ^ data);
            return new byte[] { STX, addr, data, bcc, ETX };
        }

        /// <summary>
        /// Packs D0–D7 into a single byte (D0 = LSB, D7 = MSB).
        /// </summary>
        private byte BuildControlByte()
        {
            byte b = 0;
            if (_d0) b |= 0x01;
            if (_d1) b |= 0x02;
            if (_d2) b |= 0x04;
            if (_d3) b |= 0x08;
            if (_d4) b |= 0x10;
            if (_d5) b |= 0x20;
            if (_d6) b |= 0x40;
            if (_d7) b |= 0x80;
            return b;
        }

        /// <summary>
        /// Unpacks a received status byte into S0–S7 properties.
        /// </summary>
        private void ApplyStatusByte(byte data)
        {
            S0 = (data & 0x01) != 0;
            S1 = (data & 0x02) != 0;
            S2 = (data & 0x04) != 0;
            S3 = (data & 0x08) != 0;
            S4 = (data & 0x10) != 0;
            S5 = (data & 0x20) != 0;
            S6 = (data & 0x40) != 0;
            S7 = (data & 0x80) != 0;
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
