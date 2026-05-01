using System;

namespace SSISimulator.Core
{
    /// <summary>
    /// Abstraction over a serial port connection used by the SSI Simulator.
    /// Provides an event-driven interface that enables dependency injection and
    /// unit-testing without a physical serial port.
    /// </summary>
    public interface ISerialCommunication
    {
        /// <summary>
        /// Fired on a background thread when bytes are received from the remote device.
        /// </summary>
        event EventHandler<byte[]> DataReceived;

        /// <summary>
        /// Fired when a read error occurs on the receive thread.
        /// Implementations that do not support background read errors may leave this
        /// as a no-op event.
        /// </summary>
        event EventHandler<Exception> ReceiveError;

        /// <summary>Whether the underlying port is currently open.</summary>
        bool IsOpen { get; }

        /// <summary>Opens the serial port.</summary>
        void Open(string portName, int baudRate);

        /// <summary>Closes and releases the serial port.</summary>
        void Close();

        /// <summary>Transmits <paramref name="data"/> over the open port.</summary>
        void Send(byte[] data);
    }
}
