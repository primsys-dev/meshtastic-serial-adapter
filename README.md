# Meshtastic USB / Bluetooth Serial Handler (.NET 10)

A standalone C# class library for talking to a Meshtastic radio through its
Stream API over a serial COM port. The port can come from USB CDC/ACM, a
USB-UART bridge, or a Bluetooth SPP adapter exposed by the operating system.
Then you can add your own processing logic, handlers, responses, and position
tracking.

Meshtastic (TM) is a fast-growing mesh radio network community that gives the 
people around the globe the freedom to communicate, experiment and create. 
Whether you are a mountaineer tourist, a doomsday prepper, a radio hobbyist,
an IoT implementator, Meshtastic is something that will make you happy. 
This module is our tribute to the Meshtastic community and vibe.  

## Primsys extension

This module is the serial transport core used by the **Primsys** extension.
Primsys-specific integration and configuration are maintained separately.

- Primsys: <https://www.primsys.eu/>

## Requirements

- .NET 10 SDK
- `System.IO.Ports` (referenced by the project)
- A paired/bound Bluetooth serial device if Bluetooth is used
- A device firmware with Protobuf data exchange support

## Build

```sh
dotnet build MqttServer/DotNet/USBSerialHandler.csproj
```

Reference the project from another .NET 10 project, or copy
`USBSerialHandler.cs` and add the `System.IO.Ports` package.

## Quick start

```csharp
using Meshtastic.UsbSerial;

var settings = new UsbSerialSettings
{
    Device = null,       // auto-detect USB or Bluetooth serial port
    Mode = UsbSerialMode.Text,
    Channel = 0,         // primary/default channel
    Destination = UsbSerialHandler.BroadcastNode,
    Log = (level, message) => Console.WriteLine($"{level}: {message}"),
};

// the primary channel 0 is mapped for RX/TX:
using var radio = new UsbSerialHandler(
    settings,
    text =>
    {
        Console.WriteLine($"RX text: {text}");
        return null;   // null: no automatic reply, return for response
    },
    bytes =>
    {
        Console.WriteLine($"RX binary: {bytes.Length} bytes");
        return null;
    },
    position =>
    {
        // Notification-only; no reply is sent for POSITION_APP.
        Console.WriteLine($"POSITION: {position}");
    });

if (!radio.IsRunning)
{
    Console.Error.WriteLine("Radio did not open: " + radio.DevicePath);
    return;
}

radio.SendText("hi");
radio.Stop();
```

The constructor calls `Start()` automatically. `Stop()` joins the reader and
releases the port; `Dispose()`/`await using` does the same.

## API

| Member | Purpose |
|---|---|
| `Start()` / `Stop()` | Open or release the serial port; idempotent. |
| `IsRunning` / `DevicePath` | Current state and selected port. |
| `Send(string)` | Send using the configured mode. |
| `SendText(string)` | Send UTF-8 text on `TEXT_MESSAGE_APP` (port 1). |
| `SendBytes(ReadOnlySpan<byte>)` | Send opaque bytes on `PRIVATE_APP` (port 256). |
| `SendTest()` | Send the configured test text or bytes. |
| `SetTextHandler(...)` / `SetBinaryHandler(...)` | Replace receive callbacks while running. |
| `SetPositionHandler(...)` | Replace the notification callback for `POSITION_APP`. |
| `EnumeratePorts()` | List serial ports, best candidate first. |
| `DescribePort(name)` | Classify one port as USB, Bluetooth or unknown. |

Text and binary handlers run on the reader thread and may return a reply.
Position handlers are notification-only: `POSITION_APP` packets are decoded
into `MeshtasticPosition`, logged through `Log` when `LogPositions` is enabled,
and passed to the position callback; no reply is sent. Keep callbacks short.
Sends are serialized and safe to call from other threads. An empty/null text
or binary result means no reply; a non-empty result is sent back to the
incoming sender, or to the configured destination for a broadcast/zero source.

## Settings

| Setting | Default | Notes |
|---|---|---|
| `Device` | `null` | Explicit `COM5`, `/dev/ttyACM0`, `/dev/rfcomm0`, etc.; null auto-detects. |
| `BaudRate` | `115200` | Used by USB-UART and Bluetooth; USB CDC usually ignores it. |
| `Mode` | `Text` | `Text` or `Binary`. |
| `Channel` | `0` | Channel index for outgoing packets. |
| `Destination` | broadcast | Node number; default `0xFFFFFFFF`. |
| `HopLimit` | `3` | Clamped to 0..7. |
| `ReadTimeoutMs` | `200` | One blocking read; also bounds normal shutdown. |
| `WriteTimeoutMs` | `5000` | Serial write timeout. |
| `MaxPayloadBytes` | `237` | Handler replies and sends above this are rejected. |
| `RequestConfigOnStart` | `true` | Send `ToRadio.want_config_id` after opening. |
| `LogPositions` | `true` | Log decoded `POSITION_APP` packets through `Log`. |
| `DtrEnable` / `RtsEnable` | `null` | Optional control-line overrides. |
| `ConfigurePort` | `null` | Optional hook called before `SerialPort.Open()`. |
| `Log` | `null` | Optional `(level, message)` diagnostic callback. |

## Position handler

`POSITION_APP` is Meshtastic port number **3**. Its protobuf payload is parsed
into `MeshtasticPosition` and delivered to the optional position callback. The
callback returns no value, so no reply is generated.

Parsed fields include latitude/longitude (degrees), MSL and HAE altitude,
position and GPS timestamps, location/altitude source, DOP, GPS accuracy,
ground speed/track, fix quality/type, satellites in view and precision bits.
The built-in one-line log looks like:

```text
position node=0x12345678 lat=52.5200080 lon=13.4049540 alt=34m
         time=2023-11-14T22:13:20.0000000+00:00 sats=9 fix=1/3 src=Internal
```

Position reporting is independent of `Mode`: text and binary modes both receive
position notifications.

## Platform notes

The normal `net10.0` build selects the platform at runtime. A build-time
override can be supplied when needed:

```sh
dotnet build MqttServer/DotNet/USBSerialHandler.csproj -p:UsbSerialPlatform=Windows
dotnet build MqttServer/DotNet/USBSerialHandler.csproj -p:UsbSerialPlatform=Linux
dotnet build MqttServer/DotNet/USBSerialHandler.csproj -p:UsbSerialPlatform=MacOS
```

The override defines `USB_SERIAL_WINDOWS`, `USB_SERIAL_LINUX` or
`USB_SERIAL_MACOS` and affects only automatic port detection/scoring.

- **Windows:** ports are named `COMx`; Windows does not expose USB-vs-Bluetooth
  through the serial port name. Set `Device = "COM5"` when several ports exist.
- **Linux:** USB ports usually appear as `/dev/ttyACM*` or `/dev/ttyUSB*`;
  Bluetooth SPP usually appears as `/dev/rfcomm*`.
- **macOS:** USB devices appear as `/dev/cu.usbmodem*` or
  `/dev/cu.usbserial*`; Bluetooth ports contain `Bluetooth`.
- Bluetooth pairing and RFCOMM binding are operating-system responsibilities.

## Meshtastic open-source notice

This module is an independent client implementation of the Meshtastic Stream
API. It is **not** part of, affiliated with, endorsed by, or sponsored by
Meshtastic LLC or the Meshtastic project.

Meshtastic firmware is open-source software under the **GNU General Public
License v3.0**:

- Project: <https://meshtastic.org/>
- Firmware: <https://github.com/meshtastic/firmware>
- Firmware license: <https://github.com/meshtastic/firmware/blob/develop/LICENSE>

"Meshtastic" is a registered trademark of Meshtastic LLC. All trademarks
remain the property of their respective owners.

## License

Copyright (c) 2026 Primsys. This module is provided under the repository's
license terms and distributed AS IS. See the repository `LICENSE` file.

## Protocol limits

- Stream framing: `0x94 0xC3`, big-endian 16-bit payload length, 512-byte
  maximum.
- Text: `TEXT_MESSAGE_APP` (1); binary: `PRIVATE_APP` (256).
- Position: `POSITION_APP` (3), decoded and logged; no reply.
- Decoded packets only; encrypted packets, PKI and Bluetooth pairing are not
  handled by this library.
