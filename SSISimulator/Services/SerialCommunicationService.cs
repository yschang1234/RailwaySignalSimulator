using System;
using System.IO.Ports;
using SSISimulator.Core;

namespace SSISimulator.Services
{
    /// <summary>
    /// Production implementation of <see cref="ISerialCommunication"/> that wraps
    /// <see cref="System.IO.Ports.SerialPort"/>.
    /// </summary>
    public sealed class SerialCommunicationService : ISerialCommunication, IDisposable
    {
        private SerialPort? _port;

        /// <inheritdoc/>
        public event EventHandler<byte[]>? DataReceived;

        /// <summary>Fired when a read error occurs on the background receive thread.</summary>
        public event EventHandler<Exception>? ReceiveError;

        /// <inheritdoc/>
        public bool IsOpen => _port?.IsOpen ?? false;

        /// <inheritdoc/>
        public void Open(string portName, int baudRate)
        {
            _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
            {
                ReadTimeout  = 500,
                WriteTimeout = 500,
                ReceivedBytesThreshold = 1
            };
            _port.DataReceived += OnPortDataReceived;
            _port.Open();
        }

        /// <inheritdoc/>
        public void Close()
        {
            if (_port is null) return;
            _port.DataReceived -= OnPortDataReceived;
            if (_port.IsOpen)
                _port.Close();
            _port.Dispose();
            _port = null;
        }

        /// <inheritdoc/>
        public void Send(byte[] data)
        {
            if (_port is { IsOpen: true })
                _port.Write(data, 0, data.Length);
        }

        private void OnPortDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (_port is not { IsOpen: true }) return;
            try
            {
                int available = _port.BytesToRead;
                if (available <= 0) return;
                byte[] buf = new byte[available];
                _port.Read(buf, 0, available);
                DataReceived?.Invoke(this, buf);
            }
            catch (System.IO.IOException ex)
            {
                ReceiveError?.Invoke(this, ex);
            }
            catch (InvalidOperationException ex)
            {
                ReceiveError?.Invoke(this, ex);
            }
        }

        public void Dispose() => Close();
    }
}
