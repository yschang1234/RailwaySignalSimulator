using System;

namespace SSISimulator.Models
{
    /// <summary>
    /// Represents a single log entry in the communication log window.
    /// </summary>
    public class LogEntry
    {
        /// <summary>Timestamp of the log event.</summary>
        public DateTime Timestamp { get; set; }

        /// <summary>Direction of the telegram: TX (sent) or RX (received).</summary>
        public string Direction { get; set; } = string.Empty;

        /// <summary>Raw telegram bytes displayed as a hexadecimal string.</summary>
        public string RawHex { get; set; } = string.Empty;

        /// <summary>Optional human-readable description.</summary>
        public string Description { get; set; } = string.Empty;

        public override string ToString() =>
            $"[{Timestamp:HH:mm:ss.fff}] {Direction,2}  {RawHex,-40}  {Description}";
    }
}
