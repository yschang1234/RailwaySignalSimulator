using System;
using System.Collections.Generic;
using TFMUMSimulator.Core;
using Xunit;

namespace TFMUMSimulator.Tests
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

        public bool IsOpen { get; private set; }

        public void Open(string portName, int baudRate) => IsOpen = true;
        public void Close()                             => IsOpen = false;

        public readonly List<byte[]> SentFrames = new();

        public void Send(byte[] data) => SentFrames.Add((byte[])data.Clone());

        /// <summary>Simulate bytes arriving from the SSI master.</summary>
        public void Raise(byte[] data) => DataReceived?.Invoke(this, data);
    }

    // ── Telegram builder ─────────────────────────────────────────────────────
    internal static class Telegram
    {
        /// <summary>Builds a valid 5-byte TFM UM telegram.</summary>
        public static byte[] Build(byte addr, byte data)
        {
            byte bcc = (byte)(addr ^ data);
            return new[] { TFMUMModule.STX, addr, data, bcc, TFMUMModule.ETX };
        }
    }

    // ── Test fixture ─────────────────────────────────────────────────────────
    public class TFMUMModuleTests
    {
        /// <summary>
        /// Creates a module with a valid default configuration:
        ///   address=1, Normal=0xAA, Maintenance=0x55 (~0xAA).
        /// </summary>
        private static (TFMUMModule module, FakeSerialComm comm) CreateDefault()
        {
            var comm = new FakeSerialComm();
            var module = new TFMUMModule(comm)
            {
                ModuleAddress          = 0x01,
                ConfigNormalValue      = 0xAA,
                ConfigMaintenanceValue = 0x55
            };
            return (module, comm);
        }

        // ════════════════════════════════════════════════════════════════════
        // 1. IsConfigValid
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void IsConfigValid_ValidSettings_ReturnsTrue()
        {
            var (m, _) = CreateDefault();
            Assert.True(m.IsConfigValid);
        }

        [Fact]
        public void IsConfigValid_AddressOutOfRange_ReturnsFalse()
        {
            var (m, _) = CreateDefault();
            m.ModuleAddress = 64; // > 63
            Assert.False(m.IsConfigValid);
        }

        [Fact]
        public void IsConfigValid_NormalMaintenanceMismatch_ReturnsFalse()
        {
            var (m, _) = CreateDefault();
            // 0xAA vs 0xAA not complementary
            m.ConfigMaintenanceValue = 0xAA;
            Assert.False(m.IsConfigValid);
        }

        [Fact]
        public void IsConfigValid_FaultSimulated_ReturnsFalse()
        {
            var (m, _) = CreateDefault();
            m.IsFaultSimulated = true;
            Assert.False(m.IsConfigValid);
        }

        // ════════════════════════════════════════════════════════════════════
        // 2. Fail-safe counter behaviour
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void CheckConfigIntegrity_ValidConfig_CounterStaysZero()
        {
            var (m, _) = CreateDefault();
            m.CheckConfigIntegrity();
            m.CheckConfigIntegrity();
            Assert.Equal(0, m.MismatchCycleCount);
        }

        [Fact]
        public void CheckConfigIntegrity_InvalidConfig_CounterIncrements()
        {
            var (m, _) = CreateDefault();
            m.ModuleAddress = 64;
            m.CheckConfigIntegrity();
            Assert.Equal(1, m.MismatchCycleCount);
            m.CheckConfigIntegrity();
            Assert.Equal(2, m.MismatchCycleCount);
        }

        [Fact]
        public void CheckConfigIntegrity_ValidAfterMismatch_CounterResetsToZero()
        {
            var (m, _) = CreateDefault();
            m.ModuleAddress = 64;
            m.CheckConfigIntegrity();
            m.CheckConfigIntegrity();

            m.ModuleAddress = 0x01; // restore valid
            m.CheckConfigIntegrity();
            Assert.Equal(0, m.MismatchCycleCount);
        }

        // ════════════════════════════════════════════════════════════════════
        // 3. Shutdown triggered at exactly 3 consecutive invalid cycles
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void CheckConfigIntegrity_TwoCyclesMismatch_NotShutdown()
        {
            var (m, _) = CreateDefault();
            m.ModuleAddress = 64;
            m.CheckConfigIntegrity();
            m.CheckConfigIntegrity();
            Assert.False(m.IsShutDown);
        }

        [Fact]
        public void CheckConfigIntegrity_ThreeCyclesMismatch_SetsShutDown()
        {
            var (m, _) = CreateDefault();
            m.ModuleAddress = 64;
            m.CheckConfigIntegrity(); // 1
            m.CheckConfigIntegrity(); // 2
            m.CheckConfigIntegrity(); // 3 -> shutdown
            Assert.True(m.IsShutDown);
        }

        [Fact]
        public void ShutdownThreshold_Is3()
        {
            Assert.Equal(3, TFMUMModule.ShutdownThresholdCycles);
        }

        // ════════════════════════════════════════════════════════════════════
        // 4. Shutdown is latched
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void Shutdown_IsLatched_FixingConfigDoesNotClearIt()
        {
            var (m, _) = CreateDefault();
            m.ModuleAddress = 64;
            for (int i = 0; i < 3; i++) m.CheckConfigIntegrity();
            Assert.True(m.IsShutDown);

            m.ModuleAddress = 0x01; // restore valid config
            m.CheckConfigIntegrity();
            Assert.True(m.IsShutDown); // still latched
        }

        // ════════════════════════════════════════════════════════════════════
        // 5. On shutdown all outputs must be cleared
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void OnShutdown_AllOutputsBecomeFalse()
        {
            var (m, comm) = CreateDefault();

            // Drive outputs ON via a received telegram
            comm.Raise(Telegram.Build(0x01, 0xFF));
            Assert.True(m.Outputs[0]); // sanity check

            // Trigger shutdown
            m.ModuleAddress = 64;
            for (int i = 0; i < 3; i++) m.CheckConfigIntegrity();

            for (int i = 0; i < 8; i++)
                Assert.False(m.Outputs[i], $"Output{i} should be false after shutdown");
        }

        // ════════════════════════════════════════════════════════════════════
        // 6. On shutdown response DATA byte must be 0x00
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void OnShutdown_ResponseDataForcedToZero()
        {
            var (m, comm) = CreateDefault();
            m.Inputs[0] = true;
            m.Inputs[3] = true;

            // Trigger shutdown
            m.ModuleAddress = 64;
            for (int i = 0; i < 3; i++) m.CheckConfigIntegrity();
            Assert.True(m.IsShutDown);
            comm.SentFrames.Clear();

            // Send telegram addressed to new module address (64 = 0x40)
            comm.Raise(Telegram.Build(0x40, 0x00));

            Assert.NotEmpty(comm.SentFrames);
            byte replyData = comm.SentFrames[^1][2]; // [STX][ADDR][DATA][BCC][ETX]
            Assert.Equal(0x00, replyData);
        }

        // ════════════════════════════════════════════════════════════════════
        // 7. Normal operation: response contains current Input byte
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void NormalOperation_ReplyContainsCurrentInputByte()
        {
            var (m, comm) = CreateDefault();
            m.Inputs[0] = true; // bit 0
            m.Inputs[2] = true; // bit 2  -> expected 0x05

            comm.Raise(Telegram.Build(0x01, 0x00));

            Assert.NotEmpty(comm.SentFrames);
            byte replyData = comm.SentFrames[^1][2];
            Assert.Equal((byte)0x05, replyData);
        }

        [Fact]
        public void NormalOperation_ReplyAddressMatchesModuleAddress()
        {
            var (m, comm) = CreateDefault();
            comm.Raise(Telegram.Build(0x01, 0x00));

            Assert.NotEmpty(comm.SentFrames);
            Assert.Equal((byte)0x01, comm.SentFrames[^1][1]);
        }

        [Fact]
        public void NormalOperation_ReplyBccIsCorrect()
        {
            var (m, comm) = CreateDefault();
            comm.Raise(Telegram.Build(0x01, 0x00));

            Assert.NotEmpty(comm.SentFrames);
            byte[] frame = comm.SentFrames[^1];
            Assert.Equal((byte)(frame[1] ^ frame[2]), frame[3]);
        }

        // ════════════════════════════════════════════════════════════════════
        // 8. Telegram with wrong address is silently ignored
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void WrongAddress_NoResponseSent()
        {
            var (m, comm) = CreateDefault();
            comm.Raise(Telegram.Build(0x02, 0xAA)); // module is at 0x01
            Assert.Empty(comm.SentFrames);
        }

        // ════════════════════════════════════════════════════════════════════
        // 9. Corrupted frame (bad BCC) is ignored
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void CorruptedFrame_BadBcc_NoResponseSent()
        {
            var (m, comm) = CreateDefault();
            byte[] frame = { TFMUMModule.STX, 0x01, 0xFF, 0x00 /*wrong BCC*/, TFMUMModule.ETX };
            comm.Raise(frame);
            Assert.Empty(comm.SentFrames);
        }

        // ════════════════════════════════════════════════════════════════════
        // 10. BuildInputByte packs inputs correctly
        // ════════════════════════════════════════════════════════════════════

        [Theory]
        [InlineData(0b00000000, 0x00)]
        [InlineData(0b00000001, 0x01)]
        [InlineData(0b10000000, 0x80)]
        [InlineData(0b11111111, 0xFF)]
        [InlineData(0b01010101, 0x55)]
        public void BuildInputByte_ReturnsCorrectPackedValue(int inputMask, byte expected)
        {
            var (m, _) = CreateDefault();
            for (int i = 0; i < 8; i++)
                m.Inputs[i] = (inputMask & (1 << i)) != 0;
            Assert.Equal(expected, m.BuildInputByte());
        }

        // ════════════════════════════════════════════════════════════════════
        // 11. Received DATA byte is unpacked to Output indicators
        // ════════════════════════════════════════════════════════════════════

        [Theory]
        [InlineData(0x00, false, false, false, false, false, false, false, false)]
        [InlineData(0xFF, true,  true,  true,  true,  true,  true,  true,  true )]
        [InlineData(0x01, true,  false, false, false, false, false, false, false)]
        [InlineData(0x80, false, false, false, false, false, false, false, true )]
        [InlineData(0x55, true,  false, true,  false, true,  false, true,  false)]
        public void ReceivedDataByte_IsUnpackedToOutputs(
            byte data,
            bool o0, bool o1, bool o2, bool o3,
            bool o4, bool o5, bool o6, bool o7)
        {
            var (m, comm) = CreateDefault();
            comm.Raise(Telegram.Build(0x01, data));
            Assert.Equal(o0, m.Outputs[0]);
            Assert.Equal(o1, m.Outputs[1]);
            Assert.Equal(o2, m.Outputs[2]);
            Assert.Equal(o3, m.Outputs[3]);
            Assert.Equal(o4, m.Outputs[4]);
            Assert.Equal(o5, m.Outputs[5]);
            Assert.Equal(o6, m.Outputs[6]);
            Assert.Equal(o7, m.Outputs[7]);
        }

        // ════════════════════════════════════════════════════════════════════
        // 12. Fault simulation causes config invalidity and shutdown
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void FaultSimulated_MakesConfigInvalid()
        {
            var (m, _) = CreateDefault();
            Assert.True(m.IsConfigValid);
            m.IsFaultSimulated = true;
            Assert.False(m.IsConfigValid);
        }

        [Fact]
        public void FaultSimulated_ThreeCycles_TriggersShutdown()
        {
            var (m, _) = CreateDefault();
            m.IsFaultSimulated = true;
            for (int i = 0; i < 3; i++) m.CheckConfigIntegrity();
            Assert.True(m.IsShutDown);
        }

        // ════════════════════════════════════════════════════════════════════
        // 13. ResetShutdown clears IsShutDown and counter
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void ResetShutdown_ClearsStateAndCounter()
        {
            var (m, _) = CreateDefault();
            m.ModuleAddress = 64;
            for (int i = 0; i < 3; i++) m.CheckConfigIntegrity();
            Assert.True(m.IsShutDown);

            m.ModuleAddress = 0x01; // fix config first
            m.ResetShutdown();

            Assert.False(m.IsShutDown);
            Assert.Equal(0, m.MismatchCycleCount);
        }

        // ════════════════════════════════════════════════════════════════════
        // 14. ShutdownStateChanged event fires on shutdown
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void ShutdownStateChanged_FiredOnShutdown()
        {
            var (m, _) = CreateDefault();
            bool eventFired = false;
            m.ShutdownStateChanged += (_, _) => eventFired = true;

            m.ModuleAddress = 64;
            for (int i = 0; i < 3; i++) m.CheckConfigIntegrity();

            Assert.True(eventFired);
        }
    }
}
