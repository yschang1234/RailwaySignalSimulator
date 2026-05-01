using System;
using System.Collections.Generic;

namespace SSISimulator.Core
{
    /// <summary>
    /// Pure-logic core of the SSI Master Simulator (no WPF dependencies).
    ///
    /// Responsibilities:
    ///   • Build and transmit SSI control telegrams via <see cref="ISerialCommunication.Send"/>.
    ///   • Receive response telegrams through <see cref="ISerialCommunication.DataReceived"/>,
    ///     validate BCC, and unpack the status byte into <see cref="StatusBits"/>.
    ///
    /// Telegram frame format (TFM UM – 5 bytes):
    ///   [STX=0x02] [ADDR] [DATA] [BCC=ADDR^DATA] [ETX=0x03]
    ///
    ///   STX  – Start of text (0x02)
    ///   ADDR – TFM UM module address (see <see cref="TfmUmAddress"/>)
    ///   DATA – Control or status byte; each bit maps to one I/O channel (bit 0 = channel 0)
    ///   BCC  – Block Check Character: XOR of ADDR and DATA
    ///   ETX  – End of text (0x03)
    /// </summary>
    public class SSIMasterModule
    {
        // ── Telegram constants ───────────────────────────────────────────────────
        public const byte STX = 0x02;
        public const byte ETX = 0x03;

        // ── Serial communication ─────────────────────────────────────────────────
        private readonly ISerialCommunication _comm;

        // ── Rx byte accumulation ─────────────────────────────────────────────────
        private readonly List<byte> _rxBuffer = new();
        private readonly object     _rxLock   = new();

        // ── Module state ─────────────────────────────────────────────────────────
        private byte _tfmUmAddress = 0x01;
        private readonly bool[] _statusBits = new bool[8];

        // ────────────────────────────────────────────────────────────────────────
        public SSIMasterModule(ISerialCommunication comm)
        {
            _comm = comm ?? throw new ArgumentNullException(nameof(comm));
            _comm.DataReceived += OnDataReceived;
        }

        // ── Configuration ────────────────────────────────────────────────────────
        /// <summary>TFM UM unit address used in every outgoing control telegram.</summary>
        public byte TfmUmAddress
        {
            get => _tfmUmAddress;
            set => _tfmUmAddress = value;
        }

        // ── Status output ─────────────────────────────────────────────────────────
        /// <summary>
        /// Status indicator states S0–S7 (index 0–7).
        /// Updated whenever a valid response telegram is received from the field device.
        /// </summary>
        public IReadOnlyList<bool> StatusBits => _statusBits;

        // ── Events ───────────────────────────────────────────────────────────────
        /// <summary>Fired after a valid response telegram is received and decoded.</summary>
        public event EventHandler<SsiTelegramEventArgs>? ResponseReceived;

        /// <summary>Fired after a control telegram is successfully transmitted.</summary>
        public event EventHandler<SsiTelegramEventArgs>? ControlSent;

        /// <summary>Fired when the status bits change (new valid response received).</summary>
        public event EventHandler? StatusChanged;

        /// <summary>
        /// Fired when an I/O error occurs while sending a control telegram.
        /// Callers can subscribe to log or handle the error; the module continues running.
        /// </summary>
        public event EventHandler<Exception>? SendError;

        // ── Outbound telegram API ────────────────────────────────────────────────
        /// <summary>
        /// Builds and transmits a control telegram using the current
        /// <see cref="TfmUmAddress"/> and the supplied eight control bits.
        /// </summary>
        /// <param name="controlBits">
        /// Array of exactly 8 booleans; index 0 = bit 0 (LSB), index 7 = bit 7 (MSB).
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="controlBits"/> does not contain exactly 8 elements.
        /// </exception>
        public void SendControlTelegram(bool[] controlBits)
        {
            if (controlBits is null) throw new ArgumentNullException(nameof(controlBits));
            if (controlBits.Length != 8)
                throw new ArgumentException("controlBits must have exactly 8 elements.", nameof(controlBits));

            byte addr  = _tfmUmAddress;
            byte data  = BuildControlByte(controlBits);
            byte[] frame = BuildTelegram(addr, data);

            try
            {
                _comm.Send(frame);
                ControlSent?.Invoke(this, new SsiTelegramEventArgs(addr, data));
            }
            catch (System.IO.IOException ex)
            {
                SendError?.Invoke(this, ex);
            }
            catch (InvalidOperationException ex)
            {
                SendError?.Invoke(this, ex);
            }
        }

        // ── Telegram helpers ─────────────────────────────────────────────────────
        /// <summary>
        /// Packs eight boolean flags into a single byte (index 0 = LSB, index 7 = MSB).
        /// </summary>
        public static byte BuildControlByte(bool[] bits)
        {
            if (bits is null) throw new ArgumentNullException(nameof(bits));
            if (bits.Length != 8)
                throw new ArgumentException("bits must have exactly 8 elements.", nameof(bits));

            byte b = 0;
            for (int i = 0; i < 8; i++)
                if (bits[i]) b |= (byte)(1 << i);
            return b;
        }

        /// <summary>
        /// Builds a valid 5-byte telegram frame:
        ///   [STX] [ADDR] [DATA] [BCC=ADDR^DATA] [ETX]
        /// </summary>
        public static byte[] BuildTelegram(byte addr, byte data)
        {
            byte bcc = (byte)(addr ^ data);
            return new byte[] { STX, addr, data, bcc, ETX };
        }

        /// <summary>
        /// Attempts to locate and parse the first valid response telegram in
        /// <paramref name="raw"/>.  Returns <c>true</c> and sets
        /// <paramref name="statusByte"/> when a complete, BCC-correct frame is found.
        /// Leading garbage bytes are skipped.
        /// </summary>
        public bool TryParseResponse(byte[] raw, out byte statusByte)
        {
            statusByte = 0;
            if (raw is null || raw.Length < 5) return false;

            int start = Array.IndexOf(raw, STX);
            if (start < 0) return false;
            if (raw.Length - start < 5) return false;

            byte addr    = raw[start + 1];
            byte data    = raw[start + 2];
            byte bcc     = raw[start + 3];
            byte etx     = raw[start + 4];

            if (etx != ETX)                   return false;
            if (bcc != (byte)(addr ^ data))   return false;

            statusByte = data;
            return true;
        }

        // ── Inbound data handler ─────────────────────────────────────────────────
        private void OnDataReceived(object? sender, byte[] bytes)
        {
            lock (_rxLock)
                _rxBuffer.AddRange(bytes);
            ProcessRxBuffer();
        }

        private void ProcessRxBuffer()
        {
            byte[] pending;
            lock (_rxLock)
            {
                if (_rxBuffer.Count == 0) return;
                pending = _rxBuffer.ToArray();
                _rxBuffer.Clear();
            }

            if (!TryParseResponse(pending, out byte statusByte)) return;

            // Update status bits
            ApplyStatusByte(statusByte);

            byte addr = pending[Array.IndexOf(pending, STX) + 1];
            ResponseReceived?.Invoke(this, new SsiTelegramEventArgs(addr, statusByte));
        }

        // ── Bit helpers ──────────────────────────────────────────────────────────
        /// <summary>
        /// Unpacks <paramref name="data"/> into <see cref="StatusBits"/> (S0–S7)
        /// and fires <see cref="StatusChanged"/> when any bit has changed.
        /// </summary>
        public void ApplyStatusByte(byte data)
        {
            bool changed = false;
            for (int i = 0; i < 8; i++)
            {
                bool newVal = (data & (1 << i)) != 0;
                if (_statusBits[i] != newVal)
                {
                    _statusBits[i] = newVal;
                    changed = true;
                }
            }
            if (changed)
                StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ── Event args ────────────────────────────────────────────────────────────
    /// <summary>Carries the address and data byte from a processed SSI telegram.</summary>
    public sealed class SsiTelegramEventArgs : EventArgs
    {
        public byte Address { get; }
        public byte Data    { get; }

        public SsiTelegramEventArgs(byte address, byte data)
        {
            Address = address;
            Data    = data;
        }
    }
}
