using SignalCore;
using Xunit;

namespace SignalCore.Tests
{
    public class TelegramParserTests
    {
        // ════════════════════════════════════════════════════════════════════
        // 1. Direction extraction
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void Parse_TxDirectionByte_ReturnsDirectionTX()
        {
            byte[] frame = TelegramParser.Build(TelegramParser.DIR_TX, 0x01, 0x05, 0xAA);
            ParsedTelegram? result = TelegramParser.Parse(frame);

            Assert.NotNull(result);
            Assert.Equal("TX", result!.Direction);
        }

        [Fact]
        public void Parse_RxDirectionByte_ReturnsDirectionRX()
        {
            byte[] frame = TelegramParser.Build(TelegramParser.DIR_RX, 0x01, 0x05, 0xAA);
            ParsedTelegram? result = TelegramParser.Parse(frame);

            Assert.NotNull(result);
            Assert.Equal("RX", result!.Direction);
        }

        [Fact]
        public void Parse_UnknownDirectionByte_ReturnsNull()
        {
            // Build frame manually with DIR = 0x00 (not TX or RX)
            byte dir  = 0x00;
            byte id   = 0x01;
            byte addr = 0x05;
            byte data = 0xAA;
            byte bcc  = (byte)(dir ^ id ^ addr ^ data);
            byte[] frame = { TelegramParser.STX, dir, id, addr, data, bcc, TelegramParser.ETX };

            Assert.Null(TelegramParser.Parse(frame));
        }

        // ════════════════════════════════════════════════════════════════════
        // 2. ID extraction
        // ════════════════════════════════════════════════════════════════════

        [Theory]
        [InlineData(0x00)]
        [InlineData(0x01)]
        [InlineData(0x7F)]
        [InlineData(0xFF)]
        public void Parse_VariousIds_ReturnsCorrectId(byte expectedId)
        {
            byte[] frame = TelegramParser.Build(TelegramParser.DIR_TX, expectedId, 0x01, 0x00);
            ParsedTelegram? result = TelegramParser.Parse(frame);

            Assert.NotNull(result);
            Assert.Equal(expectedId, result!.Id);
        }

        // ════════════════════════════════════════════════════════════════════
        // 3. Address extraction
        // ════════════════════════════════════════════════════════════════════

        [Theory]
        [InlineData(0x00)]
        [InlineData(0x01)]
        [InlineData(0x3F)]  // max valid module address (63)
        [InlineData(0xFF)]
        public void Parse_VariousAddresses_ReturnsCorrectAddress(byte expectedAddr)
        {
            byte[] frame = TelegramParser.Build(TelegramParser.DIR_TX, 0x01, expectedAddr, 0x00);
            ParsedTelegram? result = TelegramParser.Parse(frame);

            Assert.NotNull(result);
            Assert.Equal(expectedAddr, result!.Address);
        }

        // ════════════════════════════════════════════════════════════════════
        // 4. Data extraction
        // ════════════════════════════════════════════════════════════════════

        [Theory]
        [InlineData(0x00)]
        [InlineData(0xFF)]
        [InlineData(0xAA)]
        [InlineData(0x55)]
        [InlineData(0x0F)]
        public void Parse_VariousDataBytes_ReturnsCorrectData(byte expectedData)
        {
            byte[] frame = TelegramParser.Build(TelegramParser.DIR_RX, 0x01, 0x01, expectedData);
            ParsedTelegram? result = TelegramParser.Parse(frame);

            Assert.NotNull(result);
            Assert.Equal(expectedData, result!.Data);
        }

        // ════════════════════════════════════════════════════════════════════
        // 5. All four fields extracted correctly in one call
        // ════════════════════════════════════════════════════════════════════

        [Theory]
        [InlineData(TelegramParser.DIR_TX, 0x01, 0x05, 0xAA, "TX")]
        [InlineData(TelegramParser.DIR_RX, 0x10, 0x3F, 0x55, "RX")]
        [InlineData(TelegramParser.DIR_TX, 0xFF, 0x01, 0x00, "TX")]
        [InlineData(TelegramParser.DIR_RX, 0x00, 0x00, 0xFF, "RX")]
        public void Parse_KnownFrame_ExtractsAllFieldsCorrectly(
            byte dirByte, byte id, byte addr, byte data, string expectedDirection)
        {
            byte[] frame = TelegramParser.Build(dirByte, id, addr, data);
            ParsedTelegram? result = TelegramParser.Parse(frame);

            Assert.NotNull(result);
            Assert.Equal(expectedDirection, result!.Direction);
            Assert.Equal(id,   result.Id);
            Assert.Equal(addr, result.Address);
            Assert.Equal(data, result.Data);
        }

        // ════════════════════════════════════════════════════════════════════
        // 6. BCC validation
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void Parse_CorruptedBcc_ReturnsNull()
        {
            byte[] frame = TelegramParser.Build(TelegramParser.DIR_TX, 0x01, 0x01, 0xAA);
            frame[5] ^= 0xFF; // corrupt BCC
            Assert.Null(TelegramParser.Parse(frame));
        }

        [Fact]
        public void Parse_ValidBcc_ReturnsNonNull()
        {
            byte[] frame = TelegramParser.Build(TelegramParser.DIR_TX, 0x01, 0x01, 0xAA);
            Assert.NotNull(TelegramParser.Parse(frame));
        }

        // ════════════════════════════════════════════════════════════════════
        // 7. Frame framing validation
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void Parse_MissingStx_ReturnsNull()
        {
            // Frame without STX
            byte[] frame = { 0x00, TelegramParser.DIR_TX, 0x01, 0x01, 0xAA, 0x00, TelegramParser.ETX };
            Assert.Null(TelegramParser.Parse(frame));
        }

        [Fact]
        public void Parse_WrongEtx_ReturnsNull()
        {
            byte[] frame = TelegramParser.Build(TelegramParser.DIR_TX, 0x01, 0x01, 0xAA);
            frame[6] = 0x00; // corrupt ETX
            Assert.Null(TelegramParser.Parse(frame));
        }

        [Fact]
        public void Parse_TooShort_ReturnsNull()
        {
            byte[] frame = { TelegramParser.STX, TelegramParser.DIR_TX, 0x01 }; // too short
            Assert.Null(TelegramParser.Parse(frame));
        }

        [Fact]
        public void Parse_EmptyArray_ReturnsNull()
        {
            Assert.Null(TelegramParser.Parse(Array.Empty<byte>()));
        }

        // ════════════════════════════════════════════════════════════════════
        // 8. Leading garbage bytes are skipped
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void Parse_LeadingGarbageBytes_StillParsesCorrectly()
        {
            byte[] validFrame = TelegramParser.Build(TelegramParser.DIR_TX, 0x02, 0x01, 0xCC);
            // Prepend 3 garbage bytes
            byte[] withGarbage = new byte[3 + validFrame.Length];
            withGarbage[0] = 0xDE;
            withGarbage[1] = 0xAD;
            withGarbage[2] = 0xBE;
            validFrame.CopyTo(withGarbage, 3);

            ParsedTelegram? result = TelegramParser.Parse(withGarbage);

            Assert.NotNull(result);
            Assert.Equal("TX",  result!.Direction);
            Assert.Equal(0x02,  result.Id);
            Assert.Equal(0x01,  result.Address);
            Assert.Equal(0xCC,  result.Data);
        }

        // ════════════════════════════════════════════════════════════════════
        // 9. Null argument throws
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void Parse_NullArgument_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => TelegramParser.Parse(null!));
        }
    }
}
