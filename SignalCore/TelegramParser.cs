using System;

namespace SignalCore
{
    /// <summary>
    /// Represents the result of a successfully parsed signal telegram.
    /// </summary>
    public sealed class ParsedTelegram
    {
        /// <summary>Direction of the telegram: "TX" (outbound) or "RX" (inbound).</summary>
        public string Direction { get; }

        /// <summary>Telegram type / sequence identifier.</summary>
        public byte Id { get; }

        /// <summary>Target module address.</summary>
        public byte Address { get; }

        /// <summary>Payload data byte.</summary>
        public byte Data { get; }

        public ParsedTelegram(string direction, byte id, byte address, byte data)
        {
            Direction = direction ?? throw new ArgumentNullException(nameof(direction));
            Id        = id;
            Address   = address;
            Data      = data;
        }
    }

    /// <summary>
    /// Parses raw byte arrays into <see cref="ParsedTelegram"/> instances.
    ///
    /// Extended telegram frame format (7 bytes):
    ///   [STX=0x02] [DIR] [ID] [ADDR] [DATA] [BCC] [ETX=0x03]
    ///
    ///   STX  – Start of text (0x02)
    ///   DIR  – Direction indicator: 0x01 = TX (outbound), 0x02 = RX (inbound)
    ///   ID   – Telegram type / sequence identifier
    ///   ADDR – Target module address
    ///   DATA – Payload data byte
    ///   BCC  – Block Check Character: XOR of DIR, ID, ADDR, DATA
    ///   ETX  – End of text (0x03)
    /// </summary>
    public static class TelegramParser
    {
        /// <summary>Start of text marker.</summary>
        public const byte STX = 0x02;

        /// <summary>End of text marker.</summary>
        public const byte ETX = 0x03;

        /// <summary>Direction byte value for outbound (TX) telegrams.</summary>
        public const byte DIR_TX = 0x01;

        /// <summary>Direction byte value for inbound (RX) telegrams.</summary>
        public const byte DIR_RX = 0x02;

        /// <summary>
        /// Parses a raw byte array and returns a <see cref="ParsedTelegram"/>, or
        /// <c>null</c> when the frame is invalid (wrong framing, bad BCC, or unknown
        /// direction byte).
        /// </summary>
        /// <param name="raw">Raw bytes to parse.  May contain leading/trailing garbage.</param>
        /// <returns>A parsed telegram, or <c>null</c> if parsing fails.</returns>
        public static ParsedTelegram? Parse(byte[] raw)
        {
            if (raw is null) throw new ArgumentNullException(nameof(raw));

            // Locate STX
            int start = Array.IndexOf(raw, STX);
            if (start < 0) return null;

            // Need 7 bytes from STX onwards: STX DIR ID ADDR DATA BCC ETX
            if (raw.Length - start < 7) return null;

            byte dirByte = raw[start + 1];
            byte id      = raw[start + 2];
            byte addr    = raw[start + 3];
            byte data    = raw[start + 4];
            byte bcc     = raw[start + 5];
            byte etx     = raw[start + 6];

            // Validate framing
            if (etx != ETX) return null;

            // Validate BCC
            byte expectedBcc = (byte)(dirByte ^ id ^ addr ^ data);
            if (bcc != expectedBcc) return null;

            // Map direction byte to string; unknown values are rejected
            string? direction = dirByte switch
            {
                DIR_TX => "TX",
                DIR_RX => "RX",
                _      => null
            };

            if (direction is null) return null;

            return new ParsedTelegram(direction, id, addr, data);
        }

        /// <summary>
        /// Builds a valid 7-byte telegram frame.
        /// Useful for constructing test vectors.
        /// </summary>
        public static byte[] Build(byte dirByte, byte id, byte addr, byte data)
        {
            byte bcc = (byte)(dirByte ^ id ^ addr ^ data);
            return new byte[] { STX, dirByte, id, addr, data, bcc, ETX };
        }
    }
}
