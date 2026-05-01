using System;
using System.Collections.Generic;
using SSISimulator.Core;
using Xunit;

namespace SSISimulator.Tests
{
    // ── Fake serial communication ─────────────────────────────────────────────
    /// <summary>
    /// In-memory stub of <see cref="ISerialCommunication"/>.
    /// Collects every <see cref="Send"/> call and exposes a
    /// <see cref="Raise"/> helper to simulate incoming bytes.
    /// </summary>
    internal sealed class FakeSerialComm : ISerialCommunication
    {
        public event EventHandler<byte[]>? DataReceived;

#pragma warning disable CS0067 // required by interface; never raised in this stub
        public event EventHandler<Exception>? ReceiveError;
#pragma warning restore CS0067

        public bool IsOpen { get; private set; }

        public void Open(string portName, int baudRate) => IsOpen = true;
        public void Close()                             => IsOpen = false;

        public readonly List<byte[]> SentFrames = new();

        public void Send(byte[] data) => SentFrames.Add((byte[])data.Clone());

        /// <summary>Simulate bytes arriving from the TFM UM field device.</summary>
        public void Raise(byte[] data) => DataReceived?.Invoke(this, data);
    }

    // ── Test fixture ─────────────────────────────────────────────────────────
    public class SSIMasterModuleTests
    {
        /// <summary>Returns a default module with address 0x01.</summary>
        private static (SSIMasterModule module, FakeSerialComm comm) CreateDefault()
        {
            var comm   = new FakeSerialComm();
            var module = new SSIMasterModule(comm) { TfmUmAddress = 0x01 };
            return (module, comm);
        }

        // ════════════════════════════════════════════════════════════════════
        // 1. BuildControlByte – bit packing
        // ════════════════════════════════════════════════════════════════════

        [Theory]
        [InlineData(0b00000000, 0x00)]
        [InlineData(0b00000001, 0x01)]
        [InlineData(0b10000000, 0x80)]
        [InlineData(0b11111111, 0xFF)]
        [InlineData(0b01010101, 0x55)]
        [InlineData(0b10101010, 0xAA)]
        public void BuildControlByte_CorrectlyPacksBits(int inputMask, byte expected)
        {
            bool[] bits = new bool[8];
            for (int i = 0; i < 8; i++)
                bits[i] = (inputMask & (1 << i)) != 0;

            byte result = SSIMasterModule.BuildControlByte(bits);

            Assert.Equal(expected, result);
        }

        [Fact]
        public void BuildControlByte_NullArgument_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => SSIMasterModule.BuildControlByte(null!));
        }

        [Fact]
        public void BuildControlByte_WrongLength_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => SSIMasterModule.BuildControlByte(new bool[7]));
        }

        // ════════════════════════════════════════════════════════════════════
        // 2. BuildTelegram – frame structure and BCC
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void BuildTelegram_StartsWithStx()
        {
            byte[] frame = SSIMasterModule.BuildTelegram(0x01, 0xAA);
            Assert.Equal(SSIMasterModule.STX, frame[0]);
        }

        [Fact]
        public void BuildTelegram_EndsWithEtx()
        {
            byte[] frame = SSIMasterModule.BuildTelegram(0x01, 0xAA);
            Assert.Equal(SSIMasterModule.ETX, frame[4]);
        }

        [Fact]
        public void BuildTelegram_HasCorrectLength()
        {
            byte[] frame = SSIMasterModule.BuildTelegram(0x01, 0xAA);
            Assert.Equal(5, frame.Length);
        }

        [Theory]
        [InlineData(0x01, 0x00)]
        [InlineData(0x01, 0xFF)]
        [InlineData(0x3F, 0x55)]
        [InlineData(0x00, 0xAA)]
        public void BuildTelegram_BccIsAddrXorData(byte addr, byte data)
        {
            byte[] frame = SSIMasterModule.BuildTelegram(addr, data);
            // frame: [STX][ADDR][DATA][BCC][ETX]
            Assert.Equal((byte)(addr ^ data), frame[3]);
        }

        [Theory]
        [InlineData(0x01, 0xAA)]
        [InlineData(0x3F, 0x55)]
        public void BuildTelegram_AddrAndDataFieldsCorrect(byte addr, byte data)
        {
            byte[] frame = SSIMasterModule.BuildTelegram(addr, data);
            Assert.Equal(addr, frame[1]);
            Assert.Equal(data, frame[2]);
        }

        // ════════════════════════════════════════════════════════════════════
        // 3. TryParseResponse – valid frame
        // ════════════════════════════════════════════════════════════════════

        [Theory]
        [InlineData(0x01, 0x00)]
        [InlineData(0x01, 0xFF)]
        [InlineData(0x01, 0x55)]
        [InlineData(0x01, 0xAA)]
        public void TryParseResponse_ValidFrame_ReturnsTrueWithCorrectStatusByte(byte addr, byte data)
        {
            var (module, _) = CreateDefault();
            byte[] frame = SSIMasterModule.BuildTelegram(addr, data);

            bool ok = module.TryParseResponse(frame, out byte statusByte);

            Assert.True(ok);
            Assert.Equal(data, statusByte);
        }

        [Fact]
        public void TryParseResponse_CorruptBcc_ReturnsFalse()
        {
            var (module, _) = CreateDefault();
            byte[] frame = SSIMasterModule.BuildTelegram(0x01, 0xAA);
            frame[3] ^= 0xFF; // corrupt BCC

            Assert.False(module.TryParseResponse(frame, out _));
        }

        [Fact]
        public void TryParseResponse_WrongEtx_ReturnsFalse()
        {
            var (module, _) = CreateDefault();
            byte[] frame = SSIMasterModule.BuildTelegram(0x01, 0xAA);
            frame[4] = 0x00; // corrupt ETX

            Assert.False(module.TryParseResponse(frame, out _));
        }

        [Fact]
        public void TryParseResponse_MissingStx_ReturnsFalse()
        {
            var (module, _) = CreateDefault();
            // Craft a frame without STX
            byte[] frame = { 0x00, 0x01, 0xAA, 0x00, SSIMasterModule.ETX };

            Assert.False(module.TryParseResponse(frame, out _));
        }

        [Fact]
        public void TryParseResponse_TooShort_ReturnsFalse()
        {
            var (module, _) = CreateDefault();
            byte[] frame = { SSIMasterModule.STX, 0x01 }; // only 2 bytes

            Assert.False(module.TryParseResponse(frame, out _));
        }

        [Fact]
        public void TryParseResponse_NullArray_ReturnsFalse()
        {
            var (module, _) = CreateDefault();
            Assert.False(module.TryParseResponse(null!, out _));
        }

        // ════════════════════════════════════════════════════════════════════
        // 4. ApplyStatusByte – status bit unpacking
        // ════════════════════════════════════════════════════════════════════

        [Theory]
        [InlineData(0x00, false, false, false, false, false, false, false, false)]
        [InlineData(0xFF, true,  true,  true,  true,  true,  true,  true,  true )]
        [InlineData(0x01, true,  false, false, false, false, false, false, false)]
        [InlineData(0x80, false, false, false, false, false, false, false, true )]
        [InlineData(0x55, true,  false, true,  false, true,  false, true,  false)]
        [InlineData(0xAA, false, true,  false, true,  false, true,  false, true )]
        public void ApplyStatusByte_CorrectlyUnpacksAllBits(
            byte data,
            bool s0, bool s1, bool s2, bool s3,
            bool s4, bool s5, bool s6, bool s7)
        {
            var (module, _) = CreateDefault();

            module.ApplyStatusByte(data);

            Assert.Equal(s0, module.StatusBits[0]);
            Assert.Equal(s1, module.StatusBits[1]);
            Assert.Equal(s2, module.StatusBits[2]);
            Assert.Equal(s3, module.StatusBits[3]);
            Assert.Equal(s4, module.StatusBits[4]);
            Assert.Equal(s5, module.StatusBits[5]);
            Assert.Equal(s6, module.StatusBits[6]);
            Assert.Equal(s7, module.StatusBits[7]);
        }

        [Fact]
        public void ApplyStatusByte_FiresStatusChangedWhenBitsChange()
        {
            var (module, _) = CreateDefault();
            bool eventFired = false;
            module.StatusChanged += (_, _) => eventFired = true;

            module.ApplyStatusByte(0xFF); // all bits change from false to true

            Assert.True(eventFired);
        }

        [Fact]
        public void ApplyStatusByte_DoesNotFireStatusChangedWhenBitsUnchanged()
        {
            var (module, _) = CreateDefault();
            module.ApplyStatusByte(0x00); // all still false (initial state)

            int count = 0;
            module.StatusChanged += (_, _) => count++;

            module.ApplyStatusByte(0x00); // no change

            Assert.Equal(0, count);
        }

        // ════════════════════════════════════════════════════════════════════
        // 5. SendControlTelegram – frame content and events
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void SendControlTelegram_SendsCorrectFrame()
        {
            var (module, comm) = CreateDefault();
            // D0=true, D2=true → data = 0x05
            bool[] bits = { true, false, true, false, false, false, false, false };

            module.SendControlTelegram(bits);

            Assert.Single(comm.SentFrames);
            byte[] frame = comm.SentFrames[0];
            Assert.Equal(SSIMasterModule.STX, frame[0]);
            Assert.Equal((byte)0x01,          frame[1]); // ADDR
            Assert.Equal((byte)0x05,          frame[2]); // DATA
            Assert.Equal((byte)(0x01 ^ 0x05), frame[3]); // BCC
            Assert.Equal(SSIMasterModule.ETX, frame[4]);
        }

        [Fact]
        public void SendControlTelegram_FiresControlSentEvent()
        {
            var (module, _) = CreateDefault();
            SsiTelegramEventArgs? args = null;
            module.ControlSent += (_, e) => args = e;

            module.SendControlTelegram(new bool[8]);

            Assert.NotNull(args);
            Assert.Equal((byte)0x01, args!.Address);
        }

        [Fact]
        public void SendControlTelegram_NullBits_ThrowsArgumentNullException()
        {
            var (module, _) = CreateDefault();
            Assert.Throws<ArgumentNullException>(() => module.SendControlTelegram(null!));
        }

        [Fact]
        public void SendControlTelegram_WrongLength_ThrowsArgumentException()
        {
            var (module, _) = CreateDefault();
            Assert.Throws<ArgumentException>(() => module.SendControlTelegram(new bool[7]));
        }

        [Fact]
        public void SendControlTelegram_RespectsCurrentTfmUmAddress()
        {
            var (module, comm) = CreateDefault();
            module.TfmUmAddress = 0x3F; // change address to 63

            module.SendControlTelegram(new bool[8]);

            Assert.Single(comm.SentFrames);
            Assert.Equal((byte)0x3F, comm.SentFrames[0][1]); // ADDR field
        }

        // ════════════════════════════════════════════════════════════════════
        // 6. End-to-end: receive response via DataReceived event
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void DataReceived_ValidResponseFrame_UpdatesStatusBits()
        {
            var (module, comm) = CreateDefault();
            // Simulate field device sending back status byte 0xAA (bits 1,3,5,7 set)
            byte[] response = SSIMasterModule.BuildTelegram(0x01, 0xAA);

            comm.Raise(response);

            Assert.False(module.StatusBits[0]);
            Assert.True( module.StatusBits[1]);
            Assert.False(module.StatusBits[2]);
            Assert.True( module.StatusBits[3]);
            Assert.False(module.StatusBits[4]);
            Assert.True( module.StatusBits[5]);
            Assert.False(module.StatusBits[6]);
            Assert.True( module.StatusBits[7]);
        }

        [Fact]
        public void DataReceived_ValidResponseFrame_FiresResponseReceivedEvent()
        {
            var (module, comm) = CreateDefault();
            SsiTelegramEventArgs? args = null;
            module.ResponseReceived += (_, e) => args = e;

            byte[] response = SSIMasterModule.BuildTelegram(0x01, 0x55);
            comm.Raise(response);

            Assert.NotNull(args);
            Assert.Equal((byte)0x01, args!.Address);
            Assert.Equal((byte)0x55, args.Data);
        }

        [Fact]
        public void DataReceived_CorruptBcc_DoesNotUpdateStatusBits()
        {
            var (module, comm) = CreateDefault();
            byte[] response = SSIMasterModule.BuildTelegram(0x01, 0xFF);
            response[3] ^= 0xFF; // corrupt BCC

            comm.Raise(response);

            // All status bits must remain false (initial state)
            for (int i = 0; i < 8; i++)
                Assert.False(module.StatusBits[i], $"StatusBit[{i}] should remain false");
        }

        [Fact]
        public void DataReceived_CorruptBcc_DoesNotFireResponseReceivedEvent()
        {
            var (module, comm) = CreateDefault();
            bool eventFired = false;
            module.ResponseReceived += (_, _) => eventFired = true;

            byte[] response = SSIMasterModule.BuildTelegram(0x01, 0xFF);
            response[3] ^= 0xFF; // corrupt BCC

            comm.Raise(response);

            Assert.False(eventFired);
        }

        [Fact]
        public void DataReceived_LeadingGarbageBytes_StillParsesResponse()
        {
            var (module, comm) = CreateDefault();
            byte[] valid = SSIMasterModule.BuildTelegram(0x01, 0x55);
            byte[] withGarbage = new byte[3 + valid.Length];
            withGarbage[0] = 0xDE;
            withGarbage[1] = 0xAD;
            withGarbage[2] = 0xBE;
            valid.CopyTo(withGarbage, 3);

            comm.Raise(withGarbage);

            // Status bits for 0x55: bits 0,2,4,6 set
            Assert.True( module.StatusBits[0]);
            Assert.False(module.StatusBits[1]);
            Assert.True( module.StatusBits[2]);
            Assert.False(module.StatusBits[3]);
            Assert.True( module.StatusBits[4]);
            Assert.False(module.StatusBits[5]);
            Assert.True( module.StatusBits[6]);
            Assert.False(module.StatusBits[7]);
        }

        // ════════════════════════════════════════════════════════════════════
        // 7. SendError event on I/O failure
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void SendControlTelegram_WhenCommThrows_FiresSendErrorEvent()
        {
            var comm = new ThrowingSerialComm();
            var module = new SSIMasterModule(comm) { TfmUmAddress = 0x01 };
            Exception? captured = null;
            module.SendError += (_, ex) => captured = ex;

            module.SendControlTelegram(new bool[8]);

            Assert.NotNull(captured);
            Assert.IsType<System.IO.IOException>(captured);
        }
    }

    // ── Throwing stub for SendError tests ────────────────────────────────────
    internal sealed class ThrowingSerialComm : ISerialCommunication
    {
#pragma warning disable CS0067 // required by interface; never raised in these stubs
        public event EventHandler<byte[]>?   DataReceived;
        public event EventHandler<Exception>? ReceiveError;
#pragma warning restore CS0067
        public bool IsOpen => true;
        public void Open(string portName, int baudRate) { }
        public void Close() { }
        public void Send(byte[] data) => throw new System.IO.IOException("simulated send error");
    }
}
