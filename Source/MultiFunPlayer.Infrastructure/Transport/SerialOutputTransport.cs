using System.IO.Ports;

namespace MultiFunPlayer.Infrastructure.Transport;

/// <summary>Line settings for a raw serial TCode endpoint.</summary>
public sealed record SerialPortSettings
{
    public int BaudRate { get; init; } = 115200;
    public Parity Parity { get; init; } = Parity.None;
    public int DataBits { get; init; } = 8;
    public StopBits StopBits { get; init; } = StopBits.One;
    public bool DtrEnable { get; init; }
    public bool RtsEnable { get; init; }

    public void Validate()
    {
        if (BaudRate is < 110 or > 4_000_000)
            throw new ArgumentOutOfRangeException(nameof(BaudRate));
        if (DataBits is < 5 or > 8)
            throw new ArgumentOutOfRangeException(nameof(DataBits));
        if (StopBits == StopBits.None)
            throw new ArgumentOutOfRangeException(nameof(StopBits));
    }
}

/// <summary>
/// Bounded serial writer. The device path is normally a stable Linux
/// /dev/serial/by-id link, but COM names remain valid for the retained Windows
/// host. Timing and queueing stay in <see cref="BoundedOutputTransport"/>.
/// </summary>
public sealed class SerialOutputTransport : BoundedOutputTransport
{
    private readonly string _devicePath;
    private readonly SerialPortSettings _settings;
    private SerialPort? _port;

    public SerialOutputTransport(string id, string devicePath,
        SerialPortSettings? settings = null, OutputTransportOptions? options = null)
        : base(id, options)
    {
        _devicePath = string.IsNullOrWhiteSpace(devicePath)
            ? throw new ArgumentException("A serial device path is required.", nameof(devicePath))
            : devicePath.Trim();
        _settings = settings ?? new SerialPortSettings();
        _settings.Validate();
    }

    public string DevicePath => _devicePath;
    public SerialPortSettings Settings => _settings;

    protected override Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var port = new SerialPort(_devicePath, _settings.BaudRate, _settings.Parity,
            _settings.DataBits, _settings.StopBits)
        {
            DtrEnable = _settings.DtrEnable,
            RtsEnable = _settings.RtsEnable,
            ReadTimeout = 500,
            WriteTimeout = 2_000
        };

        try
        {
            port.Open();
            cancellationToken.ThrowIfCancellationRequested();
            _port = port;
            return Task.CompletedTask;
        }
        catch
        {
            port.Dispose();
            throw;
        }
    }

    protected override async ValueTask SendCoreAsync(ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var port = _port;
        if (port is null || !port.IsOpen)
            throw new InvalidOperationException("Serial transport is not connected.");

        await port.BaseStream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await port.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override Task DisconnectCoreAsync(CancellationToken cancellationToken)
    {
        var port = Interlocked.Exchange(ref _port, null);
        if (port is null)
            return Task.CompletedTask;

        try
        {
            if (port.IsOpen)
                port.Close();
        }
        finally
        {
            port.Dispose();
        }

        return Task.CompletedTask;
    }
}
