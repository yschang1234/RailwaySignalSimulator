using System;
using System.Collections.Generic;
using System.Linq;

namespace TFMUMSimulator.Core
{
    /// <summary>
    /// Pure-logic core of the TFM UM simulator (no WPF dependencies).
    ///
    /// Responsibilities:
    ///   • Receive SSI control telegrams via <see cref="ISerialCommunication.DataReceived"/>.
    ///   • Parse the telegram, update <see cref="Outputs"/>, and immediately respond.
    ///   • Run the fail-safe config-integrity check on every call to
    ///     <see cref="CheckConfigIntegrity"/>. After
    ///     <see cref="ShutdownThresholdCycles"/> consecutive invalid-config cycles the
    ///     module enters a latched shutdown state: all outputs are cleared and the reply
    ///     DATA byte is forced to 0x00.
    ///
    /// Telegram format: [STX=0x02] [ADDR] [DATA] [BCC=ADDR^DATA] [ETX=0x03]
    /// </summary>
    public class TFMUMModule
    {
        // ── Telegram constants ───────────────────────────────────────────────────
        public const byte STX = 0x02;
        public const byte ETX = 0x03;

        /// <summary>Safety-timer major-cycle period in milliseconds (~850 ms).</summary>
        public const int SafetyCycleMs = 850;

        /// <summary>
        /// Number of consecutive invalid-config cycles before the module shuts down.
        /// </summary>
        public const int ShutdownThresholdCycles = 3;

        // ── Serial communication ─────────────────────────────────────────────────
        private readonly ISerialCommunication _comm;

        // ── Rx byte accumulation ─────────────────────────────────────────────────
        private readonly List<byte> _rxBuffer = new();
        private readonly object     _rxLock   = new();

        // ── Module state ─────────────────────────────────────────────────────────
        private byte _moduleAddress          = 0x01;
        private byte _configNormalValue      = 0xAA;
        private byte _configMaintenanceValue = 0x55; // complement of 0xAA
        private bool _isFaultSimulated;

        private readonly bool[] _outputs = new bool[8];
        private readonly bool[] _inputs  = new bool[8];

        private int  _mismatchCycleCount;
        private bool _isShutDown;

        // ────────────────────────────────────────────────────────────────────────
        public TFMUMModule(ISerialCommunication comm)
        {
            _comm = comm ?? throw new ArgumentNullException(nameof(comm));
            _comm.DataReceived += OnDataReceived;
        }

        // ── Config Board properties ──────────────────────────────────────────────
        /// <summary>Module address (valid range 0–63).</summary>
        public byte ModuleAddress
        {
            get => _moduleAddress;
            set { _moduleAddress = value; ConfigChanged?.Invoke(this, EventArgs.Empty); }
        }

        /// <summary>
        /// Normal-side configuration value.
        /// Must be the bitwise complement of <see cref="ConfigMaintenanceValue"/>.
        /// </summary>
        public byte ConfigNormalValue
        {
            get => _configNormalValue;
            set { _configNormalValue = value; ConfigChanged?.Invoke(this, EventArgs.Empty); }
        }

        /// <summary>
        /// Maintenance-side configuration value.
        /// Must equal <c>~ConfigNormalValue</c> for the config to be valid.
        /// </summary>
        public byte ConfigMaintenanceValue
        {
            get => _configMaintenanceValue;
            set { _configMaintenanceValue = value; ConfigChanged?.Invoke(this, EventArgs.Empty); }
        }

        /// <summary>
        /// When true a hardware-mismatch fault is artificially injected,
        /// causing the fail-safe counter to increment regardless of config values.
        /// </summary>
        public bool IsFaultSimulated
        {
            get => _isFaultSimulated;
            set { _isFaultSimulated = value; ConfigChanged?.Invoke(this, EventArgs.Empty); }
        }

        // ── Computed config validity ─────────────────────────────────────────────
        /// <summary>
        /// Returns true when the Config Board settings are self-consistent:
        ///   • <see cref="ModuleAddress"/> ∈ [0, 63]
        ///   • <see cref="ConfigNormalValue"/> == ~<see cref="ConfigMaintenanceValue"/>
        ///   • <see cref="IsFaultSimulated"/> == false
        /// </summary>
        public bool IsConfigValid =>
            ModuleAddress <= 63 &&
            !IsFaultSimulated &&
            ConfigNormalValue == (byte)(~ConfigMaintenanceValue);

        // ── Fail-safe state ──────────────────────────────────────────────────────
        /// <summary>Running count of consecutive cycles with an invalid configuration.</summary>
        public int MismatchCycleCount => _mismatchCycleCount;

        /// <summary>
        /// True when the module has entered the latched fail-safe shutdown state.
        /// All outputs are cleared and response data is forced to 0x00.
        /// </summary>
        public bool IsShutDown => _isShutDown;

        // ── I/O state ────────────────────────────────────────────────────────────
        /// <summary>
        /// Output indicator states O0–O7 (index 0–7).
        /// Driven by the DATA byte of received SSI control telegrams.
        /// Cleared on shutdown.
        /// </summary>
        public IReadOnlyList<bool> Outputs => _outputs;

        /// <summary>
        /// Input states I0–I7 (index 0–7).
        /// Set by the operator/test code to simulate field conditions.
        /// Packed into the DATA byte of every response telegram.
        /// </summary>
        public bool[] Inputs => _inputs;

        // ── Events ───────────────────────────────────────────────────────────────
        /// <summary>Fired after a valid telegram is received and processed.</summary>
        public event EventHandler<TelegramEventArgs>? TelegramReceived;

        /// <summary>Fired after a response telegram is sent.</summary>
        public event EventHandler<TelegramEventArgs>? TelegramSent;

        /// <summary>Fired when any config property changes.</summary>
        public event EventHandler? ConfigChanged;

        /// <summary>Fired when the shutdown state changes.</summary>
        public event EventHandler? ShutdownStateChanged;

        /// <summary>Fired when any output changes.</summary>
        public event EventHandler? OutputsChanged;

        /// <summary>
        /// Fired when an I/O error occurs while sending a response telegram.
        /// Callers can subscribe to log or handle the error; the module continues running.
        /// </summary>
        public event EventHandler<Exception>? SendError;

        // ── Fail-safe logic ──────────────────────────────────────────────────────
        /// <summary>
        /// Evaluates the Config Board settings for one safety cycle.
        /// Call this once per major cycle from the safety timer.
        /// </summary>
        public void CheckConfigIntegrity()
        {
            if (!IsConfigValid)
            {
                _mismatchCycleCount++;
                if (_mismatchCycleCount >= ShutdownThresholdCycles && !_isShutDown)
                {
                    _isShutDown = true;
                    ClearAllOutputs();
                    ShutdownStateChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            else
            {
                _mismatchCycleCount = 0;
            }
        }

        /// <summary>
        /// Clears the latched shutdown state and resets the mismatch counter.
        /// Should only be invoked by an explicit operator reset action.
        /// </summary>
        public void ResetShutdown()
        {
            _isShutDown         = false;
            _mismatchCycleCount = 0;
            ShutdownStateChanged?.Invoke(this, EventArgs.Empty);
        }

        // ── Telegram handling ────────────────────────────────────────────────────
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

            int start = Array.IndexOf(pending, STX);
            if (start < 0) return;

            int end = Array.IndexOf(pending, ETX, start + 1);
            if (end < 0 || end - start < 4) return;

            byte rxAddr = pending[start + 1];
            byte rxData = pending[start + 2];
            byte rxBcc  = pending[start + 3];

            if (rxBcc != (byte)(rxAddr ^ rxData)) return; // BCC mismatch
            if (rxAddr != ModuleAddress)           return; // not for this module

            // Update outputs (or clear them if shutdown)
            ApplyOutputByte(rxData);
            TelegramReceived?.Invoke(this, new TelegramEventArgs(rxAddr, rxData));

            // Build and send response
            byte replyData = _isShutDown ? (byte)0x00 : BuildInputByte();
            SendResponseTelegram(rxAddr, replyData);
        }

        private void SendResponseTelegram(byte addr, byte data)
        {
            byte   bcc   = (byte)(addr ^ data);
            byte[] frame = { STX, addr, data, bcc, ETX };
            try
            {
                _comm.Send(frame);
                TelegramSent?.Invoke(this, new TelegramEventArgs(addr, data));
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

        // ── Bit helpers ──────────────────────────────────────────────────────────
        private void ApplyOutputByte(byte data)
        {
            if (_isShutDown)
            {
                ClearAllOutputs();
                return;
            }

            for (int i = 0; i < 8; i++)
                _outputs[i] = (data & (1 << i)) != 0;

            OutputsChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Packs Input[0]–Input[7] into a single byte (I0 = LSB).</summary>
        public byte BuildInputByte()
        {
            byte b = 0;
            for (int i = 0; i < 8; i++)
                if (_inputs[i]) b |= (byte)(1 << i);
            return b;
        }

        private void ClearAllOutputs()
        {
            Array.Clear(_outputs, 0, _outputs.Length);
            OutputsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ── Event args ────────────────────────────────────────────────────────────
    /// <summary>Carries the address and data from a processed telegram.</summary>
    public sealed class TelegramEventArgs : EventArgs
    {
        public byte Address { get; }
        public byte Data    { get; }

        public TelegramEventArgs(byte address, byte data)
        {
            Address = address;
            Data    = data;
        }
    }
}
