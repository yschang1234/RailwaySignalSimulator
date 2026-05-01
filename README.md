# RailwaySignalSimulator

A WPF-based simulator suite for the **TFM UM Railway Signal Interface** protocol.
The solution implements both ends of the serial communication link in separate WPF
applications backed by independently testable core libraries.

---

## Solution Structure

```
RailwaySignalSimulator/
│
├── RailwaySignalSimulation.sln    ← Visual Studio solution (all projects)
│
├── SSISimulator/                  ← WPF app – SSI Master (controller side)
│   ├── Commands/RelayCommand.cs
│   ├── Converters/
│   │   ├── BoolToVisibilityConverter.cs
│   │   └── ByteHexConverter.cs
│   ├── Models/LogEntry.cs
│   ├── Services/SerialCommunicationService.cs  ← ISerialCommunication impl
│   ├── ViewModels/MainViewModel.cs             ← MVVM ViewModel (uses SSIMasterModule)
│   ├── MainWindow.xaml / MainWindow.xaml.cs    ← XAML View + DI wiring
│   └── App.xaml / App.xaml.cs
│
├── SSISimulator.Core/             ← Pure-logic library (no WPF, no hardware)
│   ├── ISerialCommunication.cs    ← Communication interface
│   └── SSIMasterModule.cs         ← Protocol core: telegram build/parse, status bits
│
├── SSISimulator.Tests/            ← xUnit test project for SSISimulator.Core
│   └── SSIMasterModuleTests.cs
│
├── TFMUMSimulator/                ← WPF app – TFM UM Field Device (slave side)
│   ├── Commands/RelayCommand.cs
│   ├── Converters/
│   │   ├── BoolToVisibilityConverter.cs
│   │   └── ByteHexConverter.cs
│   ├── Models/LogEntry.cs
│   ├── Services/SerialCommunicationService.cs  ← ISerialCommunication impl
│   ├── ViewModels/TFMUMViewModel.cs            ← MVVM ViewModel (uses TFMUMModule)
│   ├── MainWindow.xaml / MainWindow.xaml.cs    ← XAML View + DI wiring
│   └── App.xaml / App.xaml.cs
│
├── TFMUMSimulator.Core/           ← Pure-logic library (no WPF, no hardware)
│   ├── ISerialCommunication.cs    ← Communication interface
│   └── TFMUMModule.cs             ← Protocol core: telegram receive/respond, fail-safe
│
├── TFMUMSimulator.Tests/          ← xUnit test project for TFMUMSimulator.Core
│   └── TFMUMViewModelTests.cs     ← TFMUMModule + fail-safe integration tests
│
├── SignalCore/                    ← Shared telegram parsing library
│   └── TelegramParser.cs          ← 7-byte extended frame parser
│
└── SignalCore.Tests/              ← xUnit test project for SignalCore
    └── TelegramParserTests.cs
```

---

## Project Roles

| Project | Role |
|---|---|
| **SSISimulator** | WPF master/controller application. Sends control telegrams (D0–D7) every 850 ms and displays received status bits (S0–S7). |
| **SSISimulator.Core** | Platform-independent core logic: `ISerialCommunication` interface, `SSIMasterModule` (telegram building, response parsing, status bit mapping). |
| **SSISimulator.Tests** | 45 xUnit tests covering `SSIMasterModule`: bit packing, frame building, BCC validation, status bit unpacking, event firing, send-error handling. |
| **TFMUMSimulator** | WPF field-device (slave) application. Receives control telegrams, drives Output0–7 LEDs, and responds with Input0–7 status. |
| **TFMUMSimulator.Core** | Platform-independent core logic: `ISerialCommunication` interface, `TFMUMModule` (telegram receive/respond, **fail-safe shutdown** logic). |
| **TFMUMSimulator.Tests** | 33 xUnit tests covering `TFMUMModule`: config validity, fail-safe counter, shutdown latching, output clearing, response BCC, etc. |
| **SignalCore** | Shared library: `TelegramParser` for 7-byte extended frames (STX DIR ID ADDR DATA BCC ETX). |
| **SignalCore.Tests** | 17 xUnit tests for `TelegramParser`: direction extraction, BCC validation, garbage tolerance, null handling. |

---

## Communication Interface

Both core libraries define the same `ISerialCommunication` interface (in their
respective namespaces) to decouple protocol logic from hardware:

```csharp
public interface ISerialCommunication
{
    event EventHandler<byte[]> DataReceived;   // fires on background thread
    bool   IsOpen { get; }
    void   Open(string portName, int baudRate);
    void   Close();
    void   Send(byte[] data);
}
```

Production wiring: each WPF app creates a `SerialCommunicationService`
(a thin `SerialPort` wrapper) and passes it to the ViewModel constructor.
Tests substitute a `FakeSerialComm` stub.

---

## Telegram Frame Format (TFM UM – 5 bytes)

```
[STX=0x02] [ADDR] [DATA] [BCC=ADDR⊕DATA] [ETX=0x03]
```

| Field | Size | Description |
|---|---|---|
| STX | 1 byte | Start of text (0x02) |
| ADDR | 1 byte | Module address (0–63) |
| DATA | 1 byte | Control byte (master→slave) or status byte (slave→master) |
| BCC | 1 byte | Block Check Character = ADDR XOR DATA |
| ETX | 1 byte | End of text (0x03) |

---

## Fail-Safe Logic (TFMUMModule)

The TFM UM slave module contains a latching fail-safe mechanism to detect
configuration corruption. On every 850 ms safety cycle the ViewModel calls
`TFMUMModule.CheckConfigIntegrity()`:

1. **Valid config** – `ModuleAddress ∈ [0,63]` AND `ConfigNormalValue == ~ConfigMaintenanceValue` AND `IsFaultSimulated == false` → mismatch counter resets to 0.
2. **Invalid config** – mismatch counter increments.
3. **Shutdown** – after **3 consecutive** invalid cycles (`ShutdownThresholdCycles = 3`):
   - `IsShutDown` is latched to `true`.
   - All 8 output indicators are forced to `false`.
   - Every subsequent response telegram has `DATA = 0x00`.
   - Only an explicit operator `ResetShutdown()` call can clear the latch.

---

## MVVM Architecture

Both WPF applications follow the same pattern:

```
View (XAML)
  │  data-bind
  ▼
ViewModel (INotifyPropertyChanged + ICommand)
  │  constructor-inject ISerialCommunication
  │  compose
  ▼
Core Module (pure C#, no WPF)
  │  inject
  ▼
ISerialCommunication
  ├── SerialCommunicationService  (production – wraps System.IO.Ports.SerialPort)
  └── FakeSerialComm              (tests – in-memory stub)
```

---

## Building & Testing

```bash
# Build entire solution
dotnet build

# Run all unit tests
dotnet test

# Run only SSISimulator tests
dotnet test SSISimulator.Tests/SSISimulator.Tests.csproj

# Run only TFMUMSimulator tests
dotnet test TFMUMSimulator.Tests/TFMUMSimulator.Tests.csproj

# Run only SignalCore tests
dotnet test SignalCore.Tests/SignalCore.Tests.csproj
```
