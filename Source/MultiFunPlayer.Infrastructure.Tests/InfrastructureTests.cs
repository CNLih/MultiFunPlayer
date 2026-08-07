using System.Text;
using System.Text.Json;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.Win32.SafeHandles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using MultiFunPlayer.Application;
using MultiFunPlayer.Infrastructure.Configuration;
using MultiFunPlayer.Infrastructure.Media;
using MultiFunPlayer.Infrastructure.Output;
using MultiFunPlayer.Infrastructure.Serial;
using MultiFunPlayer.Infrastructure.Storage;
using MultiFunPlayer.Infrastructure.Transport;

namespace MultiFunPlayer.Infrastructure.Tests;

public sealed class InfrastructureTests
{
    [Fact]
    public async Task RootedStoreRejectsPathTraversal()
    {
        using var temp = new TemporaryDirectory();
        var store = new RootedFileStore(temp.Path);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ReadTextAsync("../outside.json"));
        Assert.Throws<ArgumentException>(() => store.Resolve(Path.Combine(temp.Path, "outside.json")));
    }

    [Fact]
    public async Task JsonStoreRoundTripsAndCatalogListsScripts()
    {
        using var temp = new TemporaryDirectory();
        var store = new RootedFileStore(temp.Path);
        var value = new SampleSettings("demo", 42);
        await store.WriteJsonAsync("nested/settings.json", value);

        var loaded = await store.ReadJsonAsync<SampleSettings>("nested/settings.json");
        Assert.Equal(value, loaded);

        await store.WriteTextAsync("demo.funscript", "{\"actions\":[]}");
        await store.WriteTextAsync("ignored.txt", "ignore");
        var catalog = new ScriptCatalog(store);
        var scripts = await catalog.ListAsync();
        Assert.Equal(2, scripts.Count);
        var script = Assert.Single(scripts.Where(x => x.RelativePath == "demo.funscript"));
        Assert.Equal("demo.funscript", script.RelativePath);
        Assert.Equal("{\"actions\":[]}", await catalog.ReadAsync(script.RelativePath));
    }

    [Fact]
    public async Task FileTransportWritesPayload()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "output.tcode");
        await using var transport = new FileOutputTransport("file-test", path);
        await transport.ConnectAsync();
        await transport.SendAsync(Encoding.UTF8.GetBytes("L0500\n"));
        await transport.DisconnectAsync();

        Assert.Equal("L0500\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task TCodeSinkFormatsValuesAndSurfacesTransportFailure()
    {
        await using var transport = new RecordingTransport("fake");
        await transport.ConnectAsync();
        var sink = new TCodeOutputSink(transport);
        sink.Publish(new OutputFrame(TimeSpan.Zero, new Dictionary<string, double> { ["L0"] = 0.5 }));
        Assert.Equal("L0500\n", Assert.Single(transport.Payloads));

        await transport.DisposeAsync();
        var exception = Assert.Throws<TCodeOutputException>(() => sink.Publish(
            new OutputFrame(TimeSpan.Zero, new Dictionary<string, double> { ["L0"] = 0.5 })));
        Assert.Equal("fake", exception.TransportId);
    }

    [Fact]
    public void RateLimitedSink_samples_forward_progress_and_seeks()
    {
        var inner = new RecordingSink();
        using var sink = new RateLimitedOutputSink(inner, 2);

        sink.Publish(FrameAt(0));
        sink.Publish(FrameAt(0.25));
        sink.Publish(FrameAt(0.5));
        sink.Publish(FrameAt(0.4));

        Assert.Equal(new[] { 0d, 0.5d, 0.4d }, inner.Positions);
        Assert.Equal(3, sink.SentFrames);
        Assert.Equal(1, sink.DroppedFrames);
        Assert.Equal(0, sink.FailedFrames);
        Assert.NotNull(sink.LastSentAtUtc);

        static OutputFrame FrameAt(double seconds)
            => new(TimeSpan.FromSeconds(seconds), new Dictionary<string, double> { ["L0"] = seconds });
    }

    [Fact]
    public void RateLimitedSink_tolerates_small_scheduler_jitter_at_the_rate_boundary()
    {
        var inner = new RecordingSink();
        using var sink = new RateLimitedOutputSink(inner, 20);

        sink.Publish(new OutputFrame(TimeSpan.Zero,
            new Dictionary<string, double> { ["L0"] = 0 }));
        // 46 ms is early by less than the documented 10% scheduler tolerance.
        sink.Publish(new OutputFrame(TimeSpan.FromMilliseconds(46),
            new Dictionary<string, double> { ["L0"] = 0.1 }));
        sink.Publish(new OutputFrame(TimeSpan.FromMilliseconds(90),
            new Dictionary<string, double> { ["L0"] = 0.2 }));

        Assert.Equal(2, sink.SentFrames);
        Assert.Equal(1, sink.DroppedFrames);
    }

    [Fact]
    public void RateLimitedSink_counts_forward_failures()
    {
        var inner = new ThrowingSink();
        using var sink = new RateLimitedOutputSink(inner, 20);

        Assert.Throws<InvalidOperationException>(() => sink.Publish(
            new OutputFrame(TimeSpan.Zero, new Dictionary<string, double> { ["L0"] = 0.5 })));

        Assert.Equal(0, sink.SentFrames);
        Assert.Equal(0, sink.DroppedFrames);
        Assert.Equal(1, sink.FailedFrames);
        Assert.Null(sink.LastSentAtUtc);
        Assert.Equal("sink failure", sink.LastError);

        var recoverableInner = new RecoverableSink { Fail = true };
        using var recovered = new RateLimitedOutputSink(recoverableInner, 20);
        Assert.Throws<InvalidOperationException>(() => recovered.Publish(
            new OutputFrame(TimeSpan.Zero, new Dictionary<string, double> { ["L0"] = 0.5 })));
        Assert.Equal("recoverable failure", recovered.LastError);
        recoverableInner.Fail = false;
        recovered.Publish(new OutputFrame(TimeSpan.Zero,
            new Dictionary<string, double> { ["L0"] = 0.5 }));
        Assert.Null(recovered.LastError);
    }

    [Fact]
    public async Task BoundedTransportCanRecoverAfter_a_single_write_failure()
    {
        await using var transport = new RecordingTransport("recoverable");
        await transport.ConnectAsync();
        transport.FailWrites = true;

        await Assert.ThrowsAsync<OutputTransportException>(async () =>
            await transport.SendAsync(Encoding.UTF8.GetBytes("first")));

        transport.FailWrites = false;
        await transport.SendAsync(Encoding.UTF8.GetBytes("second"));

        Assert.Equal("second", Assert.Single(transport.Payloads));
        Assert.True(transport.IsConnected);
    }

    [Fact]
    public async Task BoundedTransportReports_connect_timeout()
    {
        await using var transport = new BlockingConnectTransport("connect-timeout",
            new OutputTransportOptions { ConnectTimeout = TimeSpan.FromMilliseconds(30) });

        var exception = await Assert.ThrowsAsync<OutputTransportException>(() => transport.ConnectAsync());

        Assert.Equal("connect", exception.Operation);
    }

    [Fact]
    public async Task BoundedTransportReports_send_timeout()
    {
        await using var transport = new BlockingWriteTransport("send-timeout",
            new OutputTransportOptions { SendTimeout = TimeSpan.FromMilliseconds(30) });
        await transport.ConnectAsync();

        var exception = await Assert.ThrowsAsync<OutputTransportException>(async () =>
            await transport.SendAsync(Encoding.UTF8.GetBytes("blocked")));

        Assert.Equal("send", exception.Operation);
    }

    [Fact]
    public async Task BoundedTransportCan_disconnect_and_reconnect()
    {
        await using var transport = new RecordingTransport("reconnect");
        await transport.ConnectAsync();
        await transport.SendAsync(Encoding.UTF8.GetBytes("one"));
        await transport.DisconnectAsync();
        Assert.False(transport.IsConnected);

        await transport.ConnectAsync();
        await transport.SendAsync(Encoding.UTF8.GetBytes("two"));

        Assert.Equal(["one", "two"], transport.Payloads);
    }

    [Fact]
    public async Task TcpTransport_round_trips_a_payload()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = listener.AcceptTcpClientAsync();
        await using var transport = new TcpOutputTransport("tcp-loopback", "127.0.0.1", port);
        await transport.ConnectAsync();
        using var server = await accepted.WaitAsync(TimeSpan.FromSeconds(2));

        await transport.SendAsync(Encoding.UTF8.GetBytes("L0500\n"));
        var buffer = new byte[32];
        var read = await server.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("L0500\n", Encoding.UTF8.GetString(buffer, 0, read));
    }

    [Fact]
    public async Task TcpTransport_reconnects_after_peer_socket_restart()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var transport = new TcpOutputTransport("tcp-player-restart", "127.0.0.1", port);

        var firstAccepted = listener.AcceptTcpClientAsync();
        await transport.ConnectAsync();
        using var firstPeer = await firstAccepted.WaitAsync(TimeSpan.FromSeconds(2));
        await transport.SendAsync(Encoding.UTF8.GetBytes("first\n"));
        var firstBuffer = new byte[32];
        var firstRead = await firstPeer.GetStream().ReadAsync(firstBuffer).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("first\n", Encoding.UTF8.GetString(firstBuffer, 0, firstRead));

        firstPeer.Client.LingerState = new LingerOption(true, 0);
        firstPeer.Close();
        await transport.DisconnectAsync();

        var secondAccepted = listener.AcceptTcpClientAsync();
        await transport.ConnectAsync();
        using var secondPeer = await secondAccepted.WaitAsync(TimeSpan.FromSeconds(2));
        await transport.SendAsync(Encoding.UTF8.GetBytes("second\n"));
        var secondBuffer = new byte[32];
        var secondRead = await secondPeer.GetStream().ReadAsync(secondBuffer).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("second\n", Encoding.UTF8.GetString(secondBuffer, 0, secondRead));
    }

    [Fact]
    public async Task UdpTransport_round_trips_a_payload()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        await using var transport = new UdpOutputTransport("udp-loopback", "127.0.0.1", port);
        await transport.ConnectAsync();

        await transport.SendAsync(Encoding.UTF8.GetBytes("R20750\n"));
        var packet = await receiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("R20750\n", Encoding.UTF8.GetString(packet.Buffer));
    }

    [Fact]
    public async Task WebSocketTransport_round_trips_a_payload()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        var app = builder.Build();
        app.UseWebSockets();
        app.Run(async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[1024];
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            received.TrySetResult(Encoding.UTF8.GetString(buffer, 0, result.Count));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test", CancellationToken.None);
        });

        await app.StartAsync();
        try
        {
            await using var transport = new WebSocketOutputTransport(
                "websocket-loopback", new Uri($"ws://127.0.0.1:{port}/output"));
            await transport.ConnectAsync();
            await transport.SendAsync(Encoding.UTF8.GetBytes("L0500\n"));

            Assert.Equal("L0500\n", await received.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            await transport.DisconnectAsync();
            Assert.False(transport.IsConnected);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task WebSocketTransport_reconnects_after_peer_restart()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();

        var firstReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectionNumber = 0;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        var app = builder.Build();
        app.UseWebSockets();
        app.Run(async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[1024];
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            var payload = Encoding.UTF8.GetString(buffer, 0, result.Count);
            if (Interlocked.Increment(ref connectionNumber) == 1)
            {
                firstReceived.TrySetResult(payload);
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "player-restart", CancellationToken.None);
                firstClosed.TrySetResult(true);
            }
            else
            {
                secondReceived.TrySetResult(payload);
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test", CancellationToken.None);
            }
        });

        await app.StartAsync();
        try
        {
            await using var transport = new WebSocketOutputTransport(
                "websocket-player-restart", new Uri($"ws://127.0.0.1:{port}/output"));
            await transport.ConnectAsync();
            await transport.SendAsync(Encoding.UTF8.GetBytes("first\n"));
            Assert.Equal("first\n", await firstReceived.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            await firstClosed.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await transport.DisconnectAsync();
            await transport.ConnectAsync();
            await transport.SendAsync(Encoding.UTF8.GetBytes("second\n"));

            Assert.Equal("second\n", await secondReceived.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public void LinuxSerialEnumeratorUsesStableByIdNames()
    {
        using var temp = new TemporaryDirectory();
        var device = Path.Combine(temp.Path, "ttyACM0");
        File.WriteAllText(device, string.Empty);
        var link = Path.Combine(temp.Path, "usb-MultiFunPlayer_0001-if00");
        File.CreateSymbolicLink(link, device);

        var ports = new LinuxSerialPortEnumerator(temp.Path).Enumerate();

        var port = Assert.Single(ports);
        Assert.Equal("usb-MultiFunPlayer_0001-if00", port.StableId);
        Assert.Equal(link, port.DevicePath);
        Assert.Equal(device, port.LinkTarget);

        File.Delete(link);
        Assert.Empty(new LinuxSerialPortEnumerator(temp.Path).Enumerate());
    }

    [Fact]
    public void StorageOptionsReadsPersistentKeyRootAliases()
    {
        var options = StorageOptions.FromEnvironment(new Dictionary<string, string?>
        {
            ["MFP_KEYS_PATH"] = "/var/lib/mfp/keys",
        });

        Assert.Equal("/var/lib/mfp/keys", options.KeysRoot);
    }

    [Fact]
    public async Task SerialTransportKeepsStableDevicePathAndValidatesLineSettings()
    {
        var settings = new SerialPortSettings
        {
            BaudRate = 230400,
            Parity = Parity.Even,
            DataBits = 7,
            StopBits = StopBits.Two,
            DtrEnable = true,
        };

        await using var transport = new SerialOutputTransport("serial-test",
            "/dev/serial/by-id/usb-MultiFunPlayer_0001-if00", settings);

        Assert.Equal("/dev/serial/by-id/usb-MultiFunPlayer_0001-if00", transport.DevicePath);
        Assert.Equal(230400, transport.Settings.BaudRate);
        Assert.Equal(Parity.Even, transport.Settings.Parity);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SerialPortSettings { DataBits = 9 }.Validate());
    }

    [Fact]
    public async Task SerialTransportWritesToLinuxPseudoTerminal()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var terminal = LinuxPseudoTerminal.Create();
        await using var transport = new SerialOutputTransport("serial-pty",
            terminal.SlavePath, new SerialPortSettings { BaudRate = 115200 });
        await transport.ConnectAsync();

        var buffer = new byte[64];
        var read = terminal.Master.ReadAsync(buffer).AsTask();
        await transport.SendAsync(Encoding.UTF8.GetBytes("L0500\n"));

        var count = await read.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("L0500\n", Encoding.UTF8.GetString(buffer, 0, count));
    }

    [Fact]
    public async Task OfsWebSocketAdapter_round_trips_observed_events_and_commands()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();

        var commands = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        var app = builder.Build();
        app.UseWebSockets();
        app.Run(async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var payload = "{\"type\":\"event\",\"name\":\"media_change\",\"data\":{\"path\":\"demo.mp4\"}}";
            var bytes = Encoding.UTF8.GetBytes(payload);
            var split = bytes.Length / 2;
            await socket.SendAsync(bytes.AsMemory(0, split), WebSocketMessageType.Text, false, CancellationToken.None);
            await socket.SendAsync(bytes.AsMemory(split), WebSocketMessageType.Text, true, CancellationToken.None);
            await SendAsync(socket,
                "{\"type\":\"event\",\"name\":\"play_change\",\"data\":{\"playing\":true}}");
            await SendAsync(socket,
                "{\"type\":\"event\",\"name\":\"duration_change\",\"data\":{\"duration\":12.5}}");
            await SendAsync(socket,
                "{\"type\":\"event\",\"name\":\"time_change\",\"data\":{\"time\":2.25}}");
            await SendAsync(socket,
                "{\"type\":\"event\",\"name\":\"playbackspeed_change\",\"data\":{\"speed\":1.25}}");
            await SendAsync(socket,
                "{\"type\":\"event\",\"name\":\"funscript_change\",\"data\":{\"name\":\"L0\",\"funscript\":{\"actions\":[]}}}");
            await SendAsync(socket,
                "{\"type\":\"event\",\"name\":\"unknown_future_event\",\"data\":{}}");

            var buffer = new byte[4096];
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            commands.TrySetResult(Encoding.UTF8.GetString(buffer, 0, result.Count));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test", CancellationToken.None);
        });

        await app.StartAsync();
        try
        {
            await using var adapter = new OfsWebSocketMediaAdapter(
                new Uri($"ws://127.0.0.1:{port}/ofs"),
                new OfsWebSocketAdapterOptions { ConnectTimeout = TimeSpan.FromSeconds(2) });
            var events = new List<OfsMediaEvent>();
            var allEvents = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            adapter.MessageReceived += message =>
            {
                lock (events)
                {
                    events.Add(message);
                    if (events.Count >= 7)
                        allEvents.TrySetResult();
                }
            };

            await adapter.ConnectAsync();
            await adapter.SendPlayAsync(false);
            await allEvents.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var state = adapter.Current;
            Assert.Equal("demo.mp4", state.Path);
            Assert.True(state.Playing);
            Assert.Equal(TimeSpan.FromSeconds(12.5), state.Duration);
            Assert.Equal(TimeSpan.FromSeconds(2.25), state.Position);
            Assert.Equal(1.25, state.PlaybackSpeed);
            Assert.Contains(events, message => message.Kind == OfsMediaEventKind.FunscriptChanged
                && message.ScriptName == "L0.funscript"
                && message.FunscriptJson == "{\"actions\":[]}");
            Assert.Contains(events, message => message.Kind == OfsMediaEventKind.ProtocolError);

            var command = await commands.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using var commandDocument = JsonDocument.Parse(command);
            Assert.Equal("command", commandDocument.RootElement.GetProperty("type").GetString());
            Assert.Equal("change_play", commandDocument.RootElement.GetProperty("name").GetString());
            Assert.False(commandDocument.RootElement.GetProperty("data").GetProperty("playing").GetBoolean());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        static Task SendAsync(WebSocket socket, string message)
            => socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true,
                CancellationToken.None);
    }

    [Fact]
    public async Task OfsWebSocketAdapter_rejects_oversized_and_malformed_messages_without_throwing()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        var app = builder.Build();
        app.UseWebSockets();
        app.Run(async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await socket.SendAsync(Encoding.UTF8.GetBytes("{not-json"), WebSocketMessageType.Text, true,
                CancellationToken.None);
            await socket.SendAsync(Encoding.UTF8.GetBytes(new string('x', 4097)), WebSocketMessageType.Text, true,
                CancellationToken.None);
            await Task.Delay(100);
        });

        await app.StartAsync();
        try
        {
            await using var adapter = new OfsWebSocketMediaAdapter(
                new Uri($"ws://127.0.0.1:{port}/ofs"),
                new OfsWebSocketAdapterOptions
                {
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                    MaxMessageBytes = 4096,
                    ReceiveBufferBytes = 1024
                });
            var errors = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            adapter.MessageReceived += message =>
            {
                if (message.Kind == OfsMediaEventKind.ProtocolError)
                    errors.TrySetResult();
            };

            await adapter.ConnectAsync();
            await errors.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull(adapter.LastError);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(NetworkMediaSourceKind.Emby, "X-Emby-Token")]
    [InlineData(NetworkMediaSourceKind.Jellyfin, "X-Emby-Token")]
    public async Task NetworkMediaAdapter_reads_json_clock_and_sends_controls(
        NetworkMediaSourceKind kind, string authHeader)
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var commands = new List<string>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        var app = builder.Build();
        app.Run(async context =>
        {
            if (!string.IsNullOrEmpty(context.Request.Headers[authHeader])
                && context.Request.Headers[authHeader] != "test-token")
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            switch (context.Request.Path.Value)
            {
                case "/System/Ping":
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync("{}");
                    break;
                case "/Sessions":
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync("[{\"Id\":\"session-1\",\"DeviceId\":\"browser-1\",\"PlayState\":{\"PositionTicks\":25000000,\"IsPaused\":false,\"PlaybackRate\":1},\"NowPlayingItem\":{\"Id\":\"item-1\",\"Path\":\"/media/demo.mp4\",\"RunTimeTicks\":120000000}}]");
                    break;
                case "/Sessions/session-1/Playing/Pause":
                case "/Sessions/session-1/Playing/Seek":
                    commands.Add(context.Request.Path + context.Request.QueryString);
                    context.Response.StatusCode = StatusCodes.Status204NoContent;
                    break;
                default:
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    break;
            }
        });

        await app.StartAsync();
        try
        {
            await using var adapter = new NetworkMediaSourceAdapter(kind,
                new Uri($"http://127.0.0.1:{port}/"), "test-token", "browser-1",
                options: new NetworkMediaSourceAdapterOptions
                {
                    PollInterval = TimeSpan.FromMilliseconds(100),
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                    RequestTimeout = TimeSpan.FromSeconds(2)
                });
            await adapter.ConnectAsync();
            Assert.True(adapter.IsConnected);
            Assert.Equal("/media/demo.mp4", adapter.Current.Path);
            Assert.True(adapter.Current.Playing);
            Assert.Equal(TimeSpan.FromSeconds(2.5), adapter.Current.Position);
            Assert.Equal(TimeSpan.FromSeconds(12), adapter.Current.Duration);
            Assert.Equal("session-1", adapter.Current.SessionId);

            await adapter.SendPlayAsync(false);
            await adapter.SendSeekAsync(TimeSpan.FromSeconds(3));
            Assert.Contains(commands, command => command.EndsWith("/Pause", StringComparison.Ordinal));
            Assert.Contains(commands, command => command.Contains("SeekPositionTicks=30000000", StringComparison.Ordinal));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task NetworkMediaAdapter_reads_plex_timeline_metadata_and_commands()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var commands = new List<string>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        var app = builder.Build();
        app.Run(async context =>
        {
            if (context.Request.Path == "/player/timeline/poll")
            {
                context.Response.ContentType = "application/xml";
                await context.Response.WriteAsync("<MediaContainer><Timeline type=\"movie\" key=\"/library/metadata/1\" state=\"playing\" time=\"2500\" duration=\"12000\" rate=\"1\" machineIdentifier=\"client-1\" /></MediaContainer>");
            }
            else if (context.Request.Path == "/library/metadata/1")
            {
                context.Response.ContentType = "application/xml";
                await context.Response.WriteAsync("<MediaContainer><Video><Media><Part duration=\"12000\" file=\"/media/plex-demo.mp4\" /></Media></Video></MediaContainer>");
            }
            else if (context.Request.Path == "/clients")
            {
                context.Response.ContentType = "application/xml";
                await context.Response.WriteAsync("<MediaContainer />");
            }
            else if (context.Request.Path.StartsWithSegments("/player/playback"))
            {
                commands.Add(context.Request.Path + context.Request.QueryString);
                context.Response.ContentType = "application/xml";
                await context.Response.WriteAsync("<MediaContainer />");
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
            }
        });

        await app.StartAsync();
        try
        {
            await using var adapter = new NetworkMediaSourceAdapter(NetworkMediaSourceKind.Plex,
                new Uri($"http://127.0.0.1:{port}/"), "plex-token", "client-1",
                options: new NetworkMediaSourceAdapterOptions
                {
                    PollInterval = TimeSpan.FromMilliseconds(100),
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                    RequestTimeout = TimeSpan.FromSeconds(2)
                });
            await adapter.ConnectAsync();
            Assert.True(adapter.IsConnected);
            Assert.Equal("/media/plex-demo.mp4", adapter.Current.Path);
            Assert.Equal(TimeSpan.FromSeconds(2.5), adapter.Current.Position);
            Assert.Equal(TimeSpan.FromSeconds(12), adapter.Current.Duration);
            Assert.Equal("movie", adapter.Current.ContentType);

            await adapter.SendPlayAsync(false);
            await adapter.SendSeekAsync(TimeSpan.FromSeconds(4));
            Assert.Contains(commands, command => command.Contains("/player/playback/pause", StringComparison.Ordinal));
            Assert.Contains(commands, command => command.Contains("offset=4000", StringComparison.Ordinal));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public void ConfigurationMigrationExtractsPortableFieldsAndDropsSecrets()
    {
        var result = new ConfigurationMigrationService().Read(
            "{\"ConfigVersion\":44,\"Device\":{\"SelectedDevice\":\"TCode-0.3\",\"Devices\":[{\"Name\":\"TCode-0.3\",\"Axes\":[{\"Name\":\"L0\",\"Enabled\":true,\"DefaultValue\":1.4}]}]},\"OutputTarget\":{\"Items\":[{\"Type\":\"TcpOutputTarget\",\"Endpoint\":\"host:9000\",\"Enabled\":true,\"ApiKey\":\"do-not-copy\"}]},\"MediaSource\":{\"Selected\":\"Internal\",\"RemoteEndpoint\":\"C:\\\\Media\\\\Root\"}}");

        Assert.True(result.Success);
        Assert.NotNull(result.Configuration);
        Assert.Equal(44, result.Configuration!.SourceConfigVersion);
        Assert.Equal("TCode-0.3", result.Configuration.SelectedDevice);
        var axis = Assert.Single(result.Configuration.Axes);
        Assert.Equal(1, axis.DefaultValue);
        Assert.Equal("tcp", Assert.Single(result.Configuration.Outputs).Protocol);
        Assert.Equal("C:/Media/Root", Assert.Single(result.Configuration.MediaSources).Endpoint);
        Assert.NotEmpty(result.Configuration.Warnings);
    }

    [Fact]
    public void ConfigurationMigrationRejectsMalformedJsonWithoutThrowing()
    {
        var result = new ConfigurationMigrationService().Read("not-json");

        Assert.False(result.Success);
        Assert.Null(result.Configuration);
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    public void ConfigurationMigrationRejectsNonObjectRootsWithoutThrowing(string json)
    {
        var result = new ConfigurationMigrationService().Read(json);

        Assert.False(result.Success);
        Assert.Null(result.Configuration);
        Assert.Contains("object", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfigurationMigrationReadIsDeterministicAndDoesNotMutateInput()
    {
        const string json = "{\"ConfigVersion\":7,\"Device\":{\"SelectedDevice\":\"TCode\",\"Devices\":[{\"Name\":\"TCode\",\"Axes\":[{\"Name\":\"L0\",\"Enabled\":true,\"DefaultValue\":0.25}]}]},\"ScriptRoots\":[\"C:\\\\Media\\\\Scripts\"]}";

        var first = new ConfigurationMigrationService().Read(json);
        var second = new ConfigurationMigrationService().Read(json);

        Assert.True(first.Success);
        Assert.Equal(JsonSerializer.Serialize(first.Configuration),
            JsonSerializer.Serialize(second.Configuration));
        Assert.Contains("C:\\\\Media", json, StringComparison.Ordinal);
    }

    private sealed record SampleSettings(string Name, int Count);

    private sealed class RecordingSink : IOutputSink
    {
        public List<double> Positions { get; } = [];

        public void Publish(OutputFrame frame) => Positions.Add(frame.Position.TotalSeconds);
    }

    private sealed class ThrowingSink : IOutputSink
    {
        public void Publish(OutputFrame frame) => throw new InvalidOperationException("sink failure");
    }

    private sealed class RecoverableSink : IOutputSink
    {
        public bool Fail { get; set; }

        public void Publish(OutputFrame frame)
        {
            if (Fail)
                throw new InvalidOperationException("recoverable failure");
        }
    }

    private sealed class RecordingTransport(string id) : BoundedOutputTransport(id)
    {
        public List<string> Payloads { get; } = [];
        public bool FailWrites { get; set; }

        protected override Task ConnectCoreAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        protected override ValueTask SendCoreAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            if (FailWrites)
                throw new IOException("fake send failure");
            Payloads.Add(Encoding.UTF8.GetString(payload.Span));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingConnectTransport(string id, OutputTransportOptions options)
        : BoundedOutputTransport(id, options)
    {
        protected override async Task ConnectCoreAsync(CancellationToken cancellationToken)
            => await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        protected override ValueTask SendCoreAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    private sealed class BlockingWriteTransport(string id, OutputTransportOptions options)
        : BoundedOutputTransport(id, options)
    {
        protected override Task ConnectCoreAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        protected override async ValueTask SendCoreAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
            => await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mfp-infra-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class LinuxPseudoTerminal : IDisposable
    {
        private const int OReadWrite = 2;
        private const int ONoControllingTerminal = 0x100;

        private LinuxPseudoTerminal(FileStream master, string slavePath)
        {
            Master = master;
            SlavePath = slavePath;
        }

        public FileStream Master { get; }
        public string SlavePath { get; }

        public static LinuxPseudoTerminal Create()
        {
            var descriptor = PosixOpenPt(OReadWrite | ONoControllingTerminal);
            if (descriptor < 0)
                throw new IOException($"posix_openpt failed: {Marshal.GetLastWin32Error()}");

            try
            {
                if (GrantPt(descriptor) != 0 || UnlockPt(descriptor) != 0)
                    throw new IOException($"PTY setup failed: {Marshal.GetLastWin32Error()}");
                var name = PtsName(descriptor);
                if (name == IntPtr.Zero)
                    throw new IOException($"ptsname failed: {Marshal.GetLastWin32Error()}");
                var slavePath = Marshal.PtrToStringAnsi(name);
                if (string.IsNullOrWhiteSpace(slavePath))
                    throw new IOException("PTY slave path was empty.");
                var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
                var master = new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: false);
                return new LinuxPseudoTerminal(master, slavePath);
            }
            catch
            {
                Close(descriptor);
                throw;
            }
        }

        public void Dispose() => Master.Dispose();

        [DllImport("libc", EntryPoint = "posix_openpt", SetLastError = true)]
        private static extern int PosixOpenPt(int flags);

        [DllImport("libc", EntryPoint = "grantpt", SetLastError = true)]
        private static extern int GrantPt(int descriptor);

        [DllImport("libc", EntryPoint = "unlockpt", SetLastError = true)]
        private static extern int UnlockPt(int descriptor);

        [DllImport("libc", EntryPoint = "ptsname", SetLastError = true)]
        private static extern IntPtr PtsName(int descriptor);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        private static extern int Close(int descriptor);
    }
}
