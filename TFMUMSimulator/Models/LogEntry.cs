using System;

namespace TFMUMSimulator.Models
{
    /// <summary>
    /// Represents a single entry in the communication log display.
    /// </summary>
    public class LogEntry
    {
        /// <summary>Timestamp of the event.</summary>
        public DateTime Timestamp { get; set; }

        /// <summary>Direction: "TX" (sent) or "RX" (received) or "--" (info).</summary>
        public string Direction { get; set; } = string.Empty;

        /// <summary>Raw telegram bytes formatted as a space-separated hex string.</summary>
        public string RawHex { get; set; } = string.Empty;

        /// <summary>Optional human-readable description.</summary>
        public string Description { get; set; } = string.Empty;

        public override string ToString() =>
            $"[{Timestamp:HH:mm:ss.fff}] {Direction,2}  {RawHex,-40}  {Description}";
    }
}
