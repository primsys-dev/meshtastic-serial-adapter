// Copyright (c) 2026 Primsys <https://www.primsys.eu/>
//
// Meshtastic open-source notice
// -----------------------------
// Meshtastic firmware is open-source software licensed under the GNU General
// Public License v3.0:
//   https://github.com/meshtastic/firmware
//   https://github.com/meshtastic/firmware/blob/develop/LICENSE
//
// This module is an independent client implementation of the Meshtastic
// Stream API. It is not part of, affiliated with, endorsed by, or sponsored by
// Meshtastic LLC or the Meshtastic project. "Meshtastic" is a registered
// trademark of Meshtastic LLC. All trademarks remain the property of their
// respective owners.
//
// Primsys extension
// -----------------
// This module is used as the serial transport core of the Primsys extension.
// Primsys-specific integration and configuration are maintained separately.
//   https://www.primsys.eu/

using System.Buffers.Binary;
using System.IO.Ports;
using System.Text;

#if USB_SERIAL_WINDOWS && USB_SERIAL_LINUX
#error Define only one of USB_SERIAL_WINDOWS or USB_SERIAL_LINUX.
#endif
#if USB_SERIAL_WINDOWS && USB_SERIAL_MACOS
#error Define only one platform symbol.
#endif
#if USB_SERIAL_LINUX && USB_SERIAL_MACOS
#error Define only one platform symbol.
#endif

namespace Meshtastic.UsbSerial;

/// <summary>
/// .NET 10 clone of the C++ <c>usbserial::USBSerialHandler</c>.
/// </summary>
/// <remarks>
/// <para>
/// This module is the serial transport core of the Primsys extension.
/// Primsys-specific integration and configuration are maintained separately.
/// </para>
/// <para>
/// Meshtastic notice: this is an independent client implementation, not part
/// of, affiliated with, endorsed by, or sponsored by Meshtastic LLC or the
/// Meshtastic project. Meshtastic firmware is open source under GPL-3.0:
/// <c>https://github.com/meshtastic/firmware</c>. "Meshtastic" is a registered
/// trademark of Meshtastic LLC.
/// </para>
/// <para>
/// The handler opens a Meshtastic Stream API serial port, requests the device
/// configuration, reassembles the <c>0x94 0xC3</c> framed protobuf messages and
/// invokes a text or binary callback for decoded packets. A non-empty callback
/// result is sent back to the packet sender.
/// </para>
/// <para>
/// The COM port may be a USB CDC/ACM port, a USB-UART bridge, or a Bluetooth
/// SPP port that the OS exposes as a serial port. The transport is detected for
/// selection/logging only; all I/O goes through <see cref="SerialPort"/>.
/// Bluetooth pairing and RFCOMM binding are OS responsibilities.
/// </para>
/// <para>
/// The same code runs on Windows, Linux and macOS. A build-time
/// <c>USB_SERIAL_WINDOWS</c>, <c>USB_SERIAL_LINUX</c> or
/// <c>USB_SERIAL_MACOS</c> symbol only changes the automatic port-name
/// detection/scoring default; a normal <c>net10.0</c> build uses the runtime OS
/// through <see cref="OperatingSystem"/>.
/// </para>
/// <para>
/// The constructor calls <see cref="Start"/>. Call <see cref="Stop"/> or
/// dispose the object to release the port. Callbacks run on the reader thread.
/// Only decoded MeshPackets are processed; encrypted packets and PKI are not
/// handled.
/// </para>
/// </remarks>
public sealed class UsbSerialHandler : IDisposable, IAsyncDisposable
{
    /// <summary><c>PortNum.TEXT_MESSAGE_APP</c>.</summary>
    public const uint TextPortNum = UsbSerialProtocol.TextPortNum;

    /// <summary><c>PortNum.PRIVATE_APP</c>.</summary>
    public const uint BinaryPortNum = UsbSerialProtocol.BinaryPortNum;

    /// <summary>The reserved broadcast node number.</summary>
    public const uint BroadcastNode = UsbSerialProtocol.BroadcastNode;

    private const byte Start1 = 0x94;
    private const byte Start2 = 0xC3;
    private const int HeaderLength = 4;
    private const int MaxFramePayload = UsbSerialProtocol.MaxFramePayload;
    private const int ReceiveChunk = 4096;
    private const int MaxBufferedBytes = 8192;

    private readonly UsbSerialSettings _settings;
    private readonly object _lifecycleLock = new();
    private readonly object _portLock = new();
    private readonly object _sendLock = new();
    private readonly object _textHandlerLock = new();
    private readonly object _binaryHandlerLock = new();

    private SerialPort? _port;
    private Thread? _reader;
    private string _devicePath = string.Empty;
    private volatile bool _running;
    private int _nextPacketId;
    private int _generation;

    private Func<string, string?>? _textHandler;
    private Func<byte[], byte[]?>? _binaryHandler;

    /// <summary>Creates the handler and immediately calls <see cref="Start"/>.</summary>
    /// <param name="settings">Device, mode and protocol settings.</param>
    /// <param name="textHandler">
    /// Called on the reader thread with UTF-8 text for
    /// <c>TEXT_MESSAGE_APP</c> packets. Return null or an empty string for no
    /// reply.
    /// </param>
    /// <param name="binaryHandler">
    /// Called on the reader thread with a private copy of the bytes for
    /// <c>PRIVATE_APP</c> packets. Return null or an empty array for no reply.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric setting is invalid.</exception>
    public UsbSerialHandler(
        UsbSerialSettings settings,
        Func<string, string?>? textHandler = null,
        Func<byte[], byte[]?>? binaryHandler = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidateSettings(settings);

        _settings = settings;
        _textHandler = textHandler;
        _binaryHandler = binaryHandler;
        Start();
    }

    /// <summary>True while the reader thread owns an open port.</summary>
    public bool IsRunning => _running;

    /// <summary>The selected port, or an empty string when stopped.</summary>
    public string DevicePath
    {
        get
        {
            lock (_portLock)
            {
                return _devicePath;
            }
        }
    }

    /// <summary>The live settings object used by this handler.</summary>
    public UsbSerialSettings Settings => _settings;

    /// <summary>Replaces the text callback.</summary>
    public void SetTextHandler(Func<string, string?>? handler)
    {
        lock (_textHandlerLock)
        {
            _textHandler = handler;
        }
    }

    /// <summary>Replaces the binary callback.</summary>
    public void SetBinaryHandler(Func<byte[], byte[]?>? handler)
    {
        lock (_binaryHandlerLock)
        {
            _binaryHandler = handler;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Selects a port when needed, opens it and starts the reader. Idempotent;
    /// setup failures are logged and return false rather than throwing.
    /// </summary>
    public bool Start()
    {
        lock (_lifecycleLock)
        {
            if (_running)
            {
                return true;
            }

            // A previous reader may have stopped itself after an I/O error.
            if (_reader is { IsAlive: true } oldReader &&
                oldReader != Thread.CurrentThread)
            {
                try
                {
                    oldReader.Join(TimeSpan.FromSeconds(2));
                }
                catch (Exception error)
                {
                    Log(UsbSerialLogLevel.Warning,
                        "could not join the previous reader: " + error.Message);
                }
            }

            var generation = Interlocked.Increment(ref _generation);
            DisposePortLocked();

            var device = ChooseDevice();
            if (device.Length == 0)
            {
                return false;
            }

            SerialPort? port = null;
            try
            {
                port = new SerialPort(device, _settings.BaudRate, Parity.None,
                                      8, StopBits.One)
                {
                    Handshake = Handshake.None,
                    ReadTimeout = Math.Max(1, _settings.ReadTimeoutMs),
                    WriteTimeout = Math.Max(1, _settings.WriteTimeoutMs),
                };

                _settings.ConfigurePort?.Invoke(port);
                port.Open();

                if (_settings.DtrEnable.HasValue)
                {
                    TrySetControlLine(port, "DTR", _settings.DtrEnable.Value);
                }
                if (_settings.RtsEnable.HasValue)
                {
                    TrySetControlLine(port, "RTS", _settings.RtsEnable.Value);
                }
            }
            catch (Exception error)
            {
                Log(UsbSerialLogLevel.Error,
                    "cannot open \"" + device + "\" at " +
                    _settings.BaudRate + " baud: " + error.Message);
                try
                {
                    port?.Dispose();
                }
                catch
                {
                    // The open failed; there is nothing useful to recover.
                }
                return false;
            }

            lock (_portLock)
            {
                _port = port;
                _devicePath = device;
            }
            _running = true;

            var reader = new Thread(() => ReaderLoop(port, generation))
            {
                IsBackground = true,
                Name = "Meshtastic serial reader",
            };
            try
            {
                reader.Start();
                _reader = reader;
            }
            catch (Exception error)
            {
                _running = false;
                lock (_portLock)
                {
                    _port = null;
                    _devicePath = string.Empty;
                }
                try
                {
                    port.Dispose();
                }
                catch
                {
                    // The thread failed to start; dispose is best effort.
                }
                Log(UsbSerialLogLevel.Error,
                    "cannot start the serial reader: " + error.Message);
                return false;
            }

            Log(UsbSerialLogLevel.Info,
                "opened " + device + " at " + _settings.BaudRate + " baud, " +
                ModeName(_settings.Mode) + " mode");
            if (_settings.RequestConfigOnStart && !SendWantConfig())
            {
                Log(UsbSerialLogLevel.Warning,
                    "could not request the device config; continuing without "
                    + "the handshake");
            }
            return true;
        }
    }

    /// <summary>
    /// Stops the reader, closes the port and releases it. Safe to call twice.
    /// Calling it from a reader callback is allowed, but the reader exits only
    /// after that callback returns.
    /// </summary>
    public void Stop()
    {
        lock (_lifecycleLock)
        {
            _running = false;
            Interlocked.Increment(ref _generation);

            // Do not hold the send lock while joining: a callback waiting to
            // send would otherwise keep the reader from exiting.
            SerialPort? port;
            lock (_sendLock)
            {
                lock (_portLock)
                {
                    port = _port;
                    _port = null;
                    _devicePath = string.Empty;
                }
                if (port is not null)
                {
                    try
                    {
                        port.Close();
                    }
                    catch (Exception error)
                    {
                        Log(UsbSerialLogLevel.Warning,
                            "closing the serial port failed: " + error.Message);
                    }
                }
            }

            var reader = _reader;
            _reader = null;
            if (reader is not null && reader != Thread.CurrentThread)
            {
                try
                {
                    if (!reader.Join(TimeSpan.FromSeconds(2)))
                    {
                        Log(UsbSerialLogLevel.Warning,
                            "the serial reader did not stop within two seconds");
                    }
                }
                catch (Exception error)
                {
                    Log(UsbSerialLogLevel.Warning,
                        "joining the serial reader failed: " + error.Message);
                }
            }

            try
            {
                port?.Dispose();
            }
            catch (Exception error)
            {
                Log(UsbSerialLogLevel.Debug,
                    "disposing the serial port failed: " + error.Message);
            }
        }
    }

    /// <summary>
    /// Lists the serial ports the OS currently exposes, best candidate first.
    /// </summary>
    /// <remarks>
    /// USB and Bluetooth are inferred from the port name. On Windows names are
    /// only <c>COMx</c>, so the transport is <see cref="UsbSerialTransport.Unknown"/>;
    /// when several ports exist, set <see cref="UsbSerialSettings.Device"/>.
    /// </remarks>
    public static IReadOnlyList<UsbSerialPortInfo> EnumeratePorts(
        UsbSerialPlatform platform = UsbSerialPlatform.Auto)
    {
        platform = ResolvePlatform(platform);
        string[] names;
        try
        {
            names = SerialPort.GetPortNames();
        }
        catch (Exception)
        {
            return Array.Empty<UsbSerialPortInfo>();
        }

        var result = new List<UsbSerialPortInfo>(names.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
            {
                continue;
            }
            result.Add(DescribePort(name, platform));
        }
        result.Sort((left, right) =>
        {
            var byScore = right.Score.CompareTo(left.Score);
            return byScore != 0
                ? byScore
                : StringComparer.OrdinalIgnoreCase.Compare(left.PortName,
                                                          right.PortName);
        });
        return result;
    }

    /// <summary>Classifies one port name without opening it.</summary>
    public static UsbSerialPortInfo DescribePort(
        string portName,
        UsbSerialPlatform platform = UsbSerialPlatform.Auto)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        platform = ResolvePlatform(platform);
        var name = portName.ToLowerInvariant();
        var transport = DetectTransport(name);
        return new UsbSerialPortInfo(portName, transport,
                                     ScorePort(name, transport, platform));
    }

    private static UsbSerialTransport DetectTransport(string lowerName)
    {
        if (lowerName.Contains("rfcomm", StringComparison.Ordinal) ||
            lowerName.Contains("bluetooth", StringComparison.Ordinal) ||
            lowerName.Contains("bthenum", StringComparison.Ordinal) ||
            lowerName.Contains("bthmodem", StringComparison.Ordinal) ||
            lowerName.Contains("bt-spp", StringComparison.Ordinal) ||
            lowerName.Contains("bt_spp", StringComparison.Ordinal))
        {
            return UsbSerialTransport.Bluetooth;
        }

        if (lowerName.Contains("ttyacm", StringComparison.Ordinal) ||
            lowerName.Contains("ttyusb", StringComparison.Ordinal) ||
            lowerName.Contains("usbmodem", StringComparison.Ordinal) ||
            lowerName.Contains("usbserial", StringComparison.Ordinal) ||
            lowerName.Contains("slab", StringComparison.Ordinal) ||
            lowerName.Contains("cp210", StringComparison.Ordinal) ||
            lowerName.Contains("ftdi", StringComparison.Ordinal) ||
            lowerName.Contains("ch340", StringComparison.Ordinal) ||
            lowerName.Contains("wch", StringComparison.Ordinal) ||
            lowerName.Contains("usb", StringComparison.Ordinal))
        {
            return UsbSerialTransport.Usb;
        }

        // On Windows a plain COMx name carries no bus information, but custom
        // Bluetooth/USB hints in the name are still honored above.
        return UsbSerialTransport.Unknown;
    }

    private static int ScorePort(string lowerName,
                                 UsbSerialTransport transport,
                                 UsbSerialPlatform platform)
    {
        switch (transport)
        {
            case UsbSerialTransport.Usb:
            {
                var score = 100;
                if (lowerName.Contains("ttyacm", StringComparison.Ordinal))
                {
                    score += 20;
                }
                else if (lowerName.Contains("ttyusb", StringComparison.Ordinal))
                {
                    score += 10;
                }
                return score;
            }
            case UsbSerialTransport.Bluetooth:
            {
                var score = 90;
                if (lowerName.Contains("rfcomm", StringComparison.Ordinal))
                {
                    score += 20;
                }
                else if (lowerName.Contains("bluetooth",
                                            StringComparison.Ordinal))
                {
                    score += 10;
                }
                return score;
            }
            default:
                return platform switch
                {
                    // Windows COMx: valid serial port, but the bus is unknown.
                    UsbSerialPlatform.Windows when IsWindowsComPort(lowerName) => 20,
                    // Linux onboard UART: least likely to be the Meshtastic radio.
                    UsbSerialPlatform.Linux when lowerName.Contains("ttys",
                                                                     StringComparison.Ordinal) => 5,
                    // Prefer macOS callout devices over the blocking tty node.
                    UsbSerialPlatform.MacOS when lowerName.Contains("cu.",
                                                                     StringComparison.Ordinal) => 15,
                    _ => 10,
                };
        }
    }

    private static bool IsWindowsComPort(string lowerName) =>
        lowerName.StartsWith("com", StringComparison.Ordinal) ||
        lowerName.StartsWith(@"\\.\com", StringComparison.Ordinal);

    private static UsbSerialPlatform CompileTimePlatform
    {
        get
        {
#if USB_SERIAL_WINDOWS
            return UsbSerialPlatform.Windows;
#elif USB_SERIAL_LINUX
            return UsbSerialPlatform.Linux;
#elif USB_SERIAL_MACOS
            return UsbSerialPlatform.MacOS;
#else
            return UsbSerialPlatform.Auto;
#endif
        }
    }

    /// <summary>
    /// Resolves <see cref="UsbSerialPlatform.Auto"/> from the compile-time
    /// symbol first, then from <see cref="OperatingSystem"/>.
    /// </summary>
    private static UsbSerialPlatform ResolvePlatform(UsbSerialPlatform platform)
    {
        if (platform != UsbSerialPlatform.Auto)
        {
            return platform;
        }
        var buildPlatform = CompileTimePlatform;
        if (buildPlatform != UsbSerialPlatform.Auto)
        {
            return buildPlatform;
        }
        if (OperatingSystem.IsWindows())
        {
            return UsbSerialPlatform.Windows;
        }
        if (OperatingSystem.IsLinux())
        {
            return UsbSerialPlatform.Linux;
        }
        if (OperatingSystem.IsMacOS())
        {
            return UsbSerialPlatform.MacOS;
        }
        return UsbSerialPlatform.Auto;
    }

    private string ChooseDevice()
    {
        var platform = ResolvePlatform(_settings.Platform);
        Log(UsbSerialLogLevel.Info, "port platform: " + platform);

        if (!string.IsNullOrWhiteSpace(_settings.Device))
        {
            var configured = DescribePort(_settings.Device.Trim(), platform);
            Log(UsbSerialLogLevel.Info,
                "using configured device \"" + configured.PortName + "\" [" +
                configured.Transport + "]");
            return configured.PortName;
        }

        var ports = EnumeratePorts(platform);
        if (ports.Count == 0)
        {
            Log(UsbSerialLogLevel.Warning,
                "no serial port found (is the Meshtastic device attached or "
                + "paired?)");
            return string.Empty;
        }

        Log(UsbSerialLogLevel.Info,
            "serial ports: " + string.Join(", ", ports.Select(
                port => port.PortName + " [" + port.Transport + "]")));

        var best = ports[0];
        if (best.Transport == UsbSerialTransport.Unknown && ports.Count > 1)
        {
            Log(UsbSerialLogLevel.Warning,
                "multiple generic serial ports; set Device to choose the "
                + "Meshtastic one");
        }
        Log(UsbSerialLogLevel.Info,
            "selected serial port \"" + best.PortName + "\" [" +
            best.Transport + "]");
        return best.PortName;
    }

    private bool IsCurrentGeneration(int generation) =>
        Volatile.Read(ref _generation) == generation;

    private void ReaderLoop(SerialPort port, int generation)
    {
        var buffer = new List<byte>();
        var chunk = new byte[ReceiveChunk];

        while (_running && Volatile.Read(ref _generation) == generation)
        {
            int received;
            try
            {
                received = port.Read(chunk, 0, chunk.Length);
            }
            catch (TimeoutException)
            {
                continue;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (InvalidOperationException error)
            {
                if (_running)
                {
                    Log(UsbSerialLogLevel.Error,
                        "serial read failed: " + error.Message);
                }
                if (IsCurrentGeneration(generation))
                {
                    _running = false;
                }
                break;
            }
            catch (IOException error)
            {
                if (_running)
                {
                    Log(UsbSerialLogLevel.Error,
                        "serial read failed: " + error.Message);
                }
                if (IsCurrentGeneration(generation))
                {
                    _running = false;
                }
                break;
            }
            catch (Exception error)
            {
                if (_running)
                {
                    Log(UsbSerialLogLevel.Error,
                        "serial read failed: " + error.Message);
                }
                if (IsCurrentGeneration(generation))
                {
                    _running = false;
                }
                break;
            }

            if (received <= 0)
            {
                if (!_running)
                {
                    break;
                }
                Thread.Sleep(1);
                continue;
            }

            for (var i = 0; i < received; ++i)
            {
                buffer.Add(chunk[i]);
            }
            if (buffer.Count > MaxBufferedBytes)
            {
                TrimBuffer(buffer);
            }
            ProcessBuffer(buffer, generation);
        }
    }

    private static void TrimBuffer(List<byte> buffer)
    {
        var magic = FindMagic(buffer);
        if (magic < 0)
        {
            buffer.Clear();
        }
        else if (magic > 0)
        {
            buffer.RemoveRange(0, magic);
        }
    }

    private static int FindMagic(List<byte> buffer)
    {
        for (var i = 0; i + 1 < buffer.Count; ++i)
        {
            if (buffer[i] == Start1 && buffer[i + 1] == Start2)
            {
                return i;
            }
        }
        return -1;
    }

    private void ProcessBuffer(List<byte> buffer, int generation)
    {
        while (true)
        {
            var start = FindMagic(buffer);
            if (start < 0)
            {
                // A trailing 0x94 may be the first byte of a header whose
                // second byte is still in flight.
                if (buffer.Count > 0 && buffer[^1] == Start1)
                {
                    buffer.Clear();
                    buffer.Add(Start1);
                }
                else
                {
                    buffer.Clear();
                }
                return;
            }
            if (start > 0)
            {
                buffer.RemoveRange(0, start);
            }
            if (buffer.Count < HeaderLength)
            {
                return;
            }

            var length = (buffer[2] << 8) | buffer[3];
            if (length > MaxFramePayload)
            {
                // Bogus length: drop the two magic bytes and resynchronize.
                buffer.RemoveRange(0, 2);
                continue;
            }
            if (buffer.Count < HeaderLength + length)
            {
                return;
            }

            var frame = buffer.GetRange(HeaderLength, length).ToArray();
            buffer.RemoveRange(0, HeaderLength + length);
            try
            {
                HandleFrame(frame, generation);
            }
            catch (Exception error)
            {
                Log(UsbSerialLogLevel.Error,
                    "frame handling failed: " + error.Message);
            }
        }
    }

    private void HandleFrame(byte[] frame, int generation)
    {
        if (!IsCurrentGeneration(generation))
        {
            return;
        }
        if (!TryDecodeFromRadio(frame, out var packet) || !packet.HasDecoded)
        {
            return;
        }

        var expected = _settings.Mode == UsbSerialMode.Text
            ? TextPortNum
            : BinaryPortNum;
        if (packet.PortNum != expected)
        {
            return;
        }

        byte[]? reply;
        if (expected == TextPortNum)
        {
            var text = Encoding.UTF8.GetString(packet.Payload);
            var result = CallTextHandler(text);
            reply = string.IsNullOrEmpty(result)
                ? null
                : Encoding.UTF8.GetBytes(result);
        }
        else
        {
            reply = CallBinaryHandler(packet.Payload);
        }

        if (reply is null || reply.Length == 0)
        {
            return;
        }
        if (!IsCurrentGeneration(generation))
        {
            return;
        }

        // A direct message carries the sender in 'from'; fall back to the
        // configured destination for broadcasts/zero.
        var destination = packet.From != 0 && packet.From != BroadcastNode
            ? packet.From
            : _settings.Destination;
        if (!SendInternal(reply, expected, destination))
        {
            Log(UsbSerialLogLevel.Warning,
                "failed to send the handler reply");
        }
    }

    private string? CallTextHandler(string text)
    {
        Func<string, string?>? handler;
        lock (_textHandlerLock)
        {
            handler = _textHandler;
        }
        if (handler is null)
        {
            return null;
        }
        try
        {
            return handler(text);
        }
        catch (Exception error)
        {
            Log(UsbSerialLogLevel.Warning,
                "text handler failed: " + error.Message);
            return null;
        }
    }

    private byte[]? CallBinaryHandler(byte[] data)
    {
        Func<byte[], byte[]?>? handler;
        lock (_binaryHandlerLock)
        {
            handler = _binaryHandler;
        }
        if (handler is null)
        {
            return null;
        }
        try
        {
            return handler(data);
        }
        catch (Exception error)
        {
            Log(UsbSerialLogLevel.Warning,
                "binary handler failed: " + error.Message);
            return null;
        }
    }

    /// <summary>Sends bytes using the configured mode's port number.</summary>
    public bool Send(ReadOnlySpan<byte> data)
    {
        return SendInternal(data.ToArray(),
            _settings.Mode == UsbSerialMode.Text ? TextPortNum : BinaryPortNum,
            _settings.Destination);
    }

    /// <summary>
    /// Sends UTF-8 text in text mode, or the UTF-8 bytes of <paramref name="data"/>
    /// in binary mode.
    /// </summary>
    public bool Send(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return _settings.Mode == UsbSerialMode.Text
            ? SendText(data)
            : SendBytes(Encoding.UTF8.GetBytes(data));
    }

    /// <summary>Sends UTF-8 text on <c>TEXT_MESSAGE_APP</c>.</summary>
    public bool SendText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return SendInternal(Encoding.UTF8.GetBytes(text), TextPortNum,
                            _settings.Destination);
    }

    /// <summary>Sends opaque bytes on <c>PRIVATE_APP</c>.</summary>
    public bool SendBytes(ReadOnlySpan<byte> bytes)
    {
        return SendInternal(bytes.ToArray(), BinaryPortNum,
                            _settings.Destination);
    }

    /// <summary>Sends <see cref="UsbSerialSettings.TestText"/> or TestBytes.</summary>
    public bool SendTest()
    {
        return _settings.Mode == UsbSerialMode.Text
            ? SendInternal(Encoding.UTF8.GetBytes(_settings.TestText ?? string.Empty),
                           TextPortNum, _settings.Destination)
            : SendInternal(_settings.TestBytes ?? Array.Empty<byte>(),
                           BinaryPortNum, _settings.Destination);
    }

    private bool SendInternal(byte[] data, uint portnum, uint destination)
    {
        if (data.Length > _settings.MaxPayloadBytes)
        {
            Log(UsbSerialLogLevel.Warning,
                "payload too large (" + data.Length + " > " +
                _settings.MaxPayloadBytes + " bytes)");
            return false;
        }

        var packetId = NextPacketId();
        var channel = (uint)Math.Max(0, _settings.Channel);
        var hop = (uint)Math.Clamp(_settings.HopLimit, 0, 7);
        var dataMessage = EncodeData(portnum, data);
        var packet = EncodeMeshPacket(0, destination, channel, dataMessage,
                                      packetId, hop);
        return WriteToRadio(EncodeToRadio(packet));
    }

    private bool SendWantConfig()
    {
        return WriteToRadio(EncodeWantConfig(NextPacketId()));
    }

    private uint NextPacketId()
    {
        while (true)
        {
            var id = unchecked((uint)Interlocked.Increment(ref _nextPacketId));
            if (id != 0)
            {
                return id;
            }
        }
    }

    private bool WriteToRadio(byte[] message)
    {
        lock (_sendLock)
        {
            SerialPort? port;
            lock (_portLock)
            {
                port = _port;
            }
            if (port is null || !_running)
            {
                Log(UsbSerialLogLevel.Warning,
                    "cannot send, the serial port is not open");
                return false;
            }

            var frame = MakeFrame(message);
            if (frame.Length == 0)
            {
                Log(UsbSerialLogLevel.Warning,
                    "encoded frame is too large");
                return false;
            }

            try
            {
                port.Write(frame, 0, frame.Length);
            }
            catch (Exception error)
            {
                Log(UsbSerialLogLevel.Error,
                    "serial write failed: " + error.Message);
                return false;
            }
            return true;
        }
    }

    private readonly record struct DecodedPacket(
        uint From,
        uint To,
        uint Channel,
        uint PacketId,
        uint PortNum,
        byte[] Payload,
        bool HasDecoded);

    private static void AppendVarint(Stream output, ulong value)
    {
        while (value >= 0x80)
        {
            output.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        output.WriteByte((byte)value);
    }

    private static void AppendTag(Stream output, uint field, uint wire)
    {
        AppendVarint(output, ((ulong)field << 3) | wire);
    }

    private static void AppendVarintField(Stream output, uint field,
                                          ulong value)
    {
        AppendTag(output, field, 0);
        AppendVarint(output, value);
    }

    private static void AppendFixed32Field(Stream output, uint field,
                                           uint value)
    {
        AppendTag(output, field, 5);
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        output.Write(bytes);
    }

    private static void AppendBytesField(Stream output, uint field,
                                         ReadOnlySpan<byte> value)
    {
        AppendTag(output, field, 2);
        AppendVarint(output, (ulong)value.Length);
        output.Write(value);
    }

    private static byte[] EncodeData(uint portnum, ReadOnlySpan<byte> payload)
    {
        using var output = new MemoryStream();
        AppendVarintField(output, 1, portnum);
        AppendBytesField(output, 2, payload);
        return output.ToArray();
    }

    private static byte[] EncodeMeshPacket(uint from, uint to, uint channel,
                                           ReadOnlySpan<byte> decoded,
                                           uint packetId, uint hopLimit)
    {
        using var output = new MemoryStream();
        if (from != 0)
        {
            AppendFixed32Field(output, 1, from);
        }
        AppendFixed32Field(output, 2, to);
        if (channel != 0)
        {
            AppendVarintField(output, 3, channel);
        }
        AppendBytesField(output, 4, decoded);
        if (packetId != 0)
        {
            AppendFixed32Field(output, 6, packetId);
        }
        if (hopLimit != 0)
        {
            AppendVarintField(output, 9, hopLimit);
        }
        return output.ToArray();
    }

    private static byte[] EncodeToRadio(ReadOnlySpan<byte> packet)
    {
        using var output = new MemoryStream();
        AppendBytesField(output, 1, packet);
        return output.ToArray();
    }

    private static byte[] EncodeWantConfig(uint nonce)
    {
        using var output = new MemoryStream();
        AppendVarintField(output, 3, nonce); // ToRadio.want_config_id
        return output.ToArray();
    }

    private static byte[] MakeFrame(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxFramePayload)
        {
            return Array.Empty<byte>();
        }
        var frame = new byte[HeaderLength + payload.Length];
        frame[0] = Start1;
        frame[1] = Start2;
        frame[2] = (byte)((payload.Length >> 8) & 0xFF);
        frame[3] = (byte)(payload.Length & 0xFF);
        payload.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int pos,
                                      out ulong value)
    {
        value = 0;
        var shift = 0;
        for (var i = 0; i < 10; ++i)
        {
            if (pos >= data.Length)
            {
                return false;
            }
            var current = data[pos++];
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return true;
            }
            shift += 7;
        }
        return false;
    }

    private static bool TryReadFixed32(ReadOnlySpan<byte> data, ref int pos,
                                       out uint value)
    {
        if (data.Length - pos < 4)
        {
            value = 0;
            return false;
        }
        value = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos, 4));
        pos += 4;
        return true;
    }

    private static bool TryReadBytes(ReadOnlySpan<byte> data, ref int pos,
                                     out ReadOnlySpan<byte> value)
    {
        if (!TryReadVarint(data, ref pos, out var length) ||
            length > int.MaxValue)
        {
            value = default;
            return false;
        }

        var count = (int)length;
        if (pos > data.Length || count > data.Length - pos)
        {
            value = default;
            return false;
        }
        value = data.Slice(pos, count);
        pos += count;
        return true;
    }

    private static bool TrySkipField(ReadOnlySpan<byte> data, ref int pos,
                                     uint wire)
    {
        switch (wire)
        {
            case 0:
                return TryReadVarint(data, ref pos, out _);
            case 1:
                if (data.Length - pos < 8)
                {
                    return false;
                }
                pos += 8;
                return true;
            case 2:
                return TryReadBytes(data, ref pos, out _);
            case 5:
                if (data.Length - pos < 4)
                {
                    return false;
                }
                pos += 4;
                return true;
            default:
                return false;
        }
    }

    private static bool TryDecodeData(ReadOnlySpan<byte> data,
                                      out uint portnum, out byte[] payload)
    {
        portnum = 0;
        payload = Array.Empty<byte>();
        var pos = 0;
        var hasPortnum = false;

        while (pos < data.Length)
        {
            if (!TryReadVarint(data, ref pos, out var key))
            {
                return false;
            }
            var field = (uint)(key >> 3);
            var wire = (uint)(key & 0x07);
            if (field == 1 && wire == 0)
            {
                if (!TryReadVarint(data, ref pos, out var value))
                {
                    return false;
                }
                portnum = (uint)value;
                hasPortnum = true;
            }
            else if (field == 2 && wire == 2)
            {
                if (!TryReadBytes(data, ref pos, out var bytes))
                {
                    return false;
                }
                payload = bytes.ToArray();
            }
            else if (!TrySkipField(data, ref pos, wire))
            {
                return false;
            }
        }
        return hasPortnum;
    }

    private static bool TryDecodeMeshPacket(ReadOnlySpan<byte> data,
                                            out DecodedPacket packet)
    {
        uint from = 0;
        uint to = BroadcastNode;
        uint channel = 0;
        uint packetId = 0;
        uint portnum = 0;
        var payload = Array.Empty<byte>();
        var hasDecoded = false;
        var pos = 0;

        while (pos < data.Length)
        {
            if (!TryReadVarint(data, ref pos, out var key))
            {
                packet = default;
                return false;
            }
            var field = (uint)(key >> 3);
            var wire = (uint)(key & 0x07);
            if (field == 1 && wire == 5)
            {
                if (!TryReadFixed32(data, ref pos, out from))
                {
                    packet = default;
                    return false;
                }
            }
            else if (field == 2 && wire == 5)
            {
                if (!TryReadFixed32(data, ref pos, out to))
                {
                    packet = default;
                    return false;
                }
            }
            else if (field == 3 && wire == 0)
            {
                if (!TryReadVarint(data, ref pos, out var value))
                {
                    packet = default;
                    return false;
                }
                channel = (uint)value;
            }
            else if (field == 4 && wire == 2)
            {
                if (!TryReadBytes(data, ref pos, out var decoded) ||
                    !TryDecodeData(decoded, out portnum, out payload))
                {
                    packet = default;
                    return false;
                }
                hasDecoded = true;
            }
            else if (field == 6 && wire == 5)
            {
                if (!TryReadFixed32(data, ref pos, out packetId))
                {
                    packet = default;
                    return false;
                }
            }
            else if (!TrySkipField(data, ref pos, wire))
            {
                packet = default;
                return false;
            }
        }

        packet = new DecodedPacket(from, to, channel, packetId, portnum,
                                   payload, hasDecoded);
        return true;
    }

    private static bool TryDecodeFromRadio(ReadOnlySpan<byte> data,
                                           out DecodedPacket packet)
    {
        packet = default;
        var pos = 0;
        while (pos < data.Length)
        {
            if (!TryReadVarint(data, ref pos, out var key))
            {
                return false;
            }
            var field = (uint)(key >> 3);
            var wire = (uint)(key & 0x07);
            if (field == 2 && wire == 2)
            {
                if (!TryReadBytes(data, ref pos, out var nested))
                {
                    return false;
                }
                return TryDecodeMeshPacket(nested, out packet);
            }
            if (!TrySkipField(data, ref pos, wire))
            {
                return false;
            }
        }
        return true;
    }

    private void DisposePortLocked()
    {
        SerialPort? port;
        lock (_sendLock)
        {
            lock (_portLock)
            {
                port = _port;
                _port = null;
                _devicePath = string.Empty;
            }
        }
        try
        {
            port?.Dispose();
        }
        catch (Exception error)
        {
            Log(UsbSerialLogLevel.Debug,
                "disposing an old serial port failed: " + error.Message);
        }
    }

    private void TrySetControlLine(SerialPort port, string name, bool value)
    {
        try
        {
            if (name == "DTR")
            {
                port.DtrEnable = value;
            }
            else
            {
                port.RtsEnable = value;
            }
        }
        catch (Exception error)
        {
            Log(UsbSerialLogLevel.Warning,
                "setting " + name + " failed: " + error.Message);
        }
    }

    private static void ValidateSettings(UsbSerialSettings settings)
    {
        if (settings.BaudRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings.BaudRate),
                "BaudRate must be positive.");
        }
        if (settings.ReadTimeoutMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings.ReadTimeoutMs),
                "ReadTimeoutMs must be positive.");
        }
        if (settings.WriteTimeoutMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings.WriteTimeoutMs),
                "WriteTimeoutMs must be positive.");
        }
        if (settings.MaxPayloadBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings.MaxPayloadBytes),
                "MaxPayloadBytes cannot be negative.");
        }
        if (settings.TestBytes is null)
        {
            throw new ArgumentException("TestBytes cannot be null.",
                                        nameof(settings));
        }
    }

    private static string ModeName(UsbSerialMode mode) =>
        mode == UsbSerialMode.Text ? "text" : "binary";

    private void Log(UsbSerialLogLevel level, string message)
    {
        var log = _settings.Log;
        if (log is null)
        {
            return;
        }
        try
        {
            log(level, "USBSerialHandler: " + message);
        }
        catch
        {
            // A diagnostic sink must not be able to kill the reader thread.
        }
    }
}

/// <summary>The payload type used in both directions.</summary>
public enum UsbSerialMode
{
    /// <summary>UTF-8 text on Meshtastic <c>TEXT_MESSAGE_APP</c> (port 1).</summary>
    Text,

    /// <summary>Opaque bytes on Meshtastic <c>PRIVATE_APP</c> (port 256).</summary>
    Binary,
}

/// <summary>How a serial port reaches the radio.</summary>
public enum UsbSerialTransport
{
    Unknown,
    Usb,
    Bluetooth,
}

/// <summary>
/// Platform used for port-name detection/scoring. <see cref="Auto"/> uses the
/// build-time override when one is present, otherwise the runtime OS.
/// </summary>
public enum UsbSerialPlatform
{
    Auto,
    Windows,
    Linux,
    MacOS,
}

/// <summary>Severity passed to <see cref="UsbSerialSettings.Log"/>.</summary>
public enum UsbSerialLogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>Meshtastic protocol values used by the handler.</summary>
public static class UsbSerialProtocol
{
    /// <summary><c>PortNum.TEXT_MESSAGE_APP</c>.</summary>
    public const uint TextPortNum = 1;

    /// <summary><c>PortNum.PRIVATE_APP</c>.</summary>
    public const uint BinaryPortNum = 256;

    /// <summary>The reserved broadcast node number (all ones).</summary>
    public const uint BroadcastNode = 0xFFFFFFFFu;

    /// <summary>Largest Meshtastic Stream API protobuf payload.</summary>
    public const int MaxFramePayload = 512;
}

/// <summary>One serial port reported by <see cref="UsbSerialHandler.EnumeratePorts"/>.</summary>
/// <param name="PortName">The OS name, for example <c>/dev/ttyACM0</c>, <c>/dev/rfcomm0</c> or <c>COM5</c>.</param>
/// <param name="Transport">Best-effort USB/Bluetooth classification from the port name.</param>
/// <param name="Score">Selection priority; higher is preferred.</param>
public sealed record UsbSerialPortInfo(string PortName, UsbSerialTransport Transport, int Score);

/// <summary>Configuration for <see cref="UsbSerialHandler"/>.</summary>
/// <remarks>
/// A Meshtastic radio may expose the Stream API through a USB CDC port or
/// through a Bluetooth serial port. Both are ordinary <see cref="SerialPort"/>
/// devices once the OS has paired/bound them, so this class opens either kind.
/// On Windows the port name is only <c>COMx</c>, so automatic USB/Bluetooth
/// classification is not possible; set <see cref="Device"/> explicitly when
/// more than one serial port exists. On Linux Bluetooth SPP usually appears as
/// <c>/dev/rfcomm0</c>; on macOS as <c>/dev/cu.Bluetooth-*</c>.
/// </remarks>
public sealed class UsbSerialSettings
{
    /// <summary>
    /// Explicit serial port name. Empty or null selects the best USB/Bluetooth
    /// serial port automatically.
    /// </summary>
    public string? Device { get; set; }

    /// <summary>
    /// Port-name detection/scoring platform. <see cref="UsbSerialPlatform.Auto"/>
    /// uses the compile-time <c>USB_SERIAL_WINDOWS</c>/<c>USB_SERIAL_LINUX</c>/
    /// <c>USB_SERIAL_MACOS</c> symbol when present, otherwise the runtime OS.
    /// Set it explicitly only for unusual hosts or tests.
    /// </summary>
    public UsbSerialPlatform Platform { get; set; } = UsbSerialPlatform.Auto;

    /// <summary>Serial speed. USB CDC ignores it; a Bluetooth SPP link or a USB-UART bridge uses it.</summary>
    public int BaudRate { get; set; } = 115200;

    /// <summary>Text or binary payloads, in both directions.</summary>
    public UsbSerialMode Mode { get; set; } = UsbSerialMode.Text;

    /// <summary>Mesh channel index used for messages this handler sends.</summary>
    public int Channel { get; set; }

    /// <summary>Destination node number; defaults to the channel broadcast address.</summary>
    public uint Destination { get; set; } = UsbSerialProtocol.BroadcastNode;

    /// <summary>Mesh hop limit, clamped to 0..7 when a packet is built.</summary>
    public int HopLimit { get; set; } = 3;

    /// <summary>One blocking read timeout; also bounds normal shutdown latency.</summary>
    public int ReadTimeoutMs { get; set; } = 200;

    /// <summary>Serial write timeout.</summary>
    public int WriteTimeoutMs { get; set; } = 5000;

    /// <summary>Largest handler reply or send buffer accepted, in bytes.</summary>
    public int MaxPayloadBytes { get; set; } = 237;

    /// <summary>
    /// Send a <c>ToRadio.want_config_id</c> handshake after opening the port.
    /// This also tells the radio that a structured API client is present.
    /// </summary>
    public bool RequestConfigOnStart { get; set; } = true;

    /// <summary>Payload sent by <see cref="UsbSerialHandler.SendTest"/> in text mode.</summary>
    public string TestText { get; set; } = "USBSerialHandler test";

    /// <summary>Payload sent by <see cref="UsbSerialHandler.SendTest"/> in binary mode.</summary>
    public byte[] TestBytes { get; set; } = [0x55, 0xAA, 0x01, 0x02, 0x03, 0xFE, 0xFF];

    /// <summary>
    /// Optional DTR override applied after opening. Null keeps the
    /// <see cref="SerialPort"/> default. USB CDC devices often need DTR while
    /// some Bluetooth serial adapters do not support it.
    /// </summary>
    public bool? DtrEnable { get; set; }

    /// <summary>Optional RTS override applied after opening; null keeps the default.</summary>
    public bool? RtsEnable { get; set; }

    /// <summary>
    /// Optional hook to configure the port before it is opened (buffer sizes,
    /// encoding, etc.). Exceptions are logged and leave the handler stopped.
    /// </summary>
    public Action<SerialPort>? ConfigurePort { get; set; }

    /// <summary>Optional diagnostic sink. The handler is silent when this is null.</summary>
    public Action<UsbSerialLogLevel, string>? Log { get; set; }
}
