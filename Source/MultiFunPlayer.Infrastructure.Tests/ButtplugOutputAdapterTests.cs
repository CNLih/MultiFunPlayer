using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using MultiFunPlayer.Application;
using MultiFunPlayer.Infrastructure.Output;
using MultiFunPlayer.Infrastructure.Transport;

namespace MultiFunPlayer.Infrastructure.Tests;

public sealed class ButtplugOutputAdapterTests
{
    [Fact]
    public async Task Handshake_discovers_device_and_sends_scalar_command()
    {
        await using var simulator = await ButtplugSimulator.StartAsync();
        await using var adapter = new ButtplugOutputAdapter("buttplug-test", simulator.Endpoint,
            new ButtplugOutputOptions
            {
                SourceAxis = "L0",
                DeviceIndex = 2,
                ActuatorType = "Vibrate",
                ActuatorIndex = 1,
                Transport = new OutputTransportOptions
                {
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                    SendTimeout = TimeSpan.FromSeconds(2)
                }
            });

        Assert.True((await adapter.ConnectAsync()).Connected);
        await simulator.DeviceReady.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => adapter.Devices.Count > 0);
        Assert.Contains(adapter.Devices, device => device.DeviceIndex == 2
            && device.DeviceName == "Simulated Toy");

        adapter.Publish(new OutputFrame(TimeSpan.Zero,
            new Dictionary<string, double> { ["L0"] = 0.42 }));
        var command = await simulator.CommandAsync("ScalarCmd");
        var scalar = command.GetProperty("ScalarCmd");
        Assert.Equal(2, scalar.GetProperty("DeviceIndex").GetInt32());
        var actuator = Assert.Single(scalar.GetProperty("Scalars").EnumerateArray());
        Assert.Equal(1, actuator.GetProperty("Index").GetInt32());
        Assert.Equal("Vibrate", actuator.GetProperty("ActuatorType").GetString());
        Assert.Equal(0.42, actuator.GetProperty("Scalar").GetDouble(), precision: 6);
    }

    [Fact]
    public async Task Linear_and_rotate_commands_use_protocol_v3_shapes()
    {
        await using var simulator = await ButtplugSimulator.StartAsync();
        await using (var linear = new ButtplugOutputAdapter("linear", simulator.Endpoint,
            new ButtplugOutputOptions
            {
                DeviceIndex = 2,
                ActuatorType = "Linear",
                DurationMilliseconds = 125,
                Transport = FastTransport()
            }))
        {
            await linear.ConnectAsync();
            await simulator.DeviceReady.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => linear.Devices.Count > 0);
            linear.Publish(new OutputFrame(TimeSpan.Zero,
                new Dictionary<string, double> { ["L0"] = 0.75 }));
            var command = await simulator.CommandAsync("LinearCmd");
            var vector = Assert.Single(command.GetProperty("LinearCmd").GetProperty("Vectors").EnumerateArray());
            Assert.Equal(125, vector.GetProperty("Duration").GetInt32());
            Assert.Equal(0.75, vector.GetProperty("Position").GetDouble(), precision: 6);
        }

        await using var rotate = new ButtplugOutputAdapter("rotate", simulator.Endpoint,
            new ButtplugOutputOptions
            {
                DeviceIndex = 2,
                ActuatorType = "Rotate",
                Transport = FastTransport()
            });
        await rotate.ConnectAsync();
        await WaitUntilAsync(() => rotate.Devices.Count > 0);
        rotate.Publish(new OutputFrame(TimeSpan.Zero,
            new Dictionary<string, double> { ["L0"] = 0.25 }));
        var rotateCommand = await simulator.CommandAsync("RotateCmd");
        var rotation = Assert.Single(rotateCommand.GetProperty("RotateCmd").GetProperty("Rotations").EnumerateArray());
        Assert.Equal(0.5, rotation.GetProperty("Speed").GetDouble(), precision: 6);
        Assert.False(rotation.GetProperty("Clockwise").GetBoolean());
    }

    [Fact]
    public async Task Rejects_malformed_server_messages_and_surfaces_fault()
    {
        await using var simulator = await ButtplugSimulator.StartAsync(malformedAfterHandshake: true);
        await using var adapter = new ButtplugOutputAdapter("buttplug-malformed", simulator.Endpoint,
            new ButtplugOutputOptions { Transport = FastTransport() });
        var fault = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.Faulted += exception => fault.TrySetResult(exception);

        var connection = await Assert.ThrowsAsync<OutputTransportException>(
            () => adapter.ConnectAsync().AsTask());
        Assert.Contains("invalid", connection.ToString(), StringComparison.OrdinalIgnoreCase);
        var error = await fault.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains("invalid", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(adapter.IsConnected);
        Assert.Contains("invalid", adapter.LastError, StringComparison.OrdinalIgnoreCase);
    }

    private static OutputTransportOptions FastTransport() => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(2),
        SendTimeout = TimeSpan.FromSeconds(2),
        QueueCapacity = 8
    };

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!predicate() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(predicate(), "The expected Buttplug device state was not observed.");
    }

    private sealed class ButtplugSimulator : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly List<string> _messages = [];
        private readonly object _gate = new();
        private ButtplugSimulator(WebApplication app, Uri endpoint)
        {
            _app = app;
            Endpoint = endpoint;
        }

        public Uri Endpoint { get; }
        public bool MalformedAfterHandshake { get; init; }
        public TaskCompletionSource DeviceReady { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static async Task<ButtplugSimulator> StartAsync(bool malformedAfterHandshake = false)
        {
            using var reservation = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();

            ButtplugSimulator? simulator = null;
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
                await simulator!.RunAsync(socket, context.RequestAborted);
            });
            await app.StartAsync();
            simulator = new ButtplugSimulator(app,
                new Uri($"ws://127.0.0.1:{port}/buttplug"))
            {
                MalformedAfterHandshake = malformedAfterHandshake
            };
            // The handler is invoked only after StartAsync has returned, so the
            // closure is now guaranteed to hold the simulator instance.
            return simulator;
        }

        public async Task<JsonElement> CommandAsync(string command)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                string[] messages;
                lock (_gate)
                    messages = _messages.ToArray();
                foreach (var text in messages)
                {
                    using var document = JsonDocument.Parse(text);
                    var root = document.RootElement;
                    if (root.ValueKind == JsonValueKind.Array && root.EnumerateArray().Any(item => item.TryGetProperty(command, out _)))
                    {
                        var match = root.EnumerateArray().First(item => item.TryGetProperty(command, out _));
                        return match.Clone();
                    }
                }
                await Task.Delay(10);
            }
            throw new TimeoutException($"Timed out waiting for Buttplug command '{command}'.");
        }

        private async Task RunAsync(WebSocket socket, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var message = await ReceiveAsync(socket, cancellationToken);
                if (message is null)
                    return;
                lock (_gate)
                    _messages.Add(message);
                using var document = JsonDocument.Parse(message);
                foreach (var envelope in document.RootElement.EnumerateArray())
                {
                    if (envelope.TryGetProperty("RequestServerInfo", out var request))
                    {
                        var id = request.GetProperty("Id").GetInt32();
                        await SendAsync(socket, $"[{{\"ServerInfo\":{{\"Id\":{id},\"MessageVersion\":3,\"MaxPingTime\":0}}}}]");
                        if (MalformedAfterHandshake)
                        {
                            await SendAsync(socket, "{not-an-array");
                            return;
                        }
                    }
                    else if (envelope.TryGetProperty("RequestDeviceList", out var deviceList))
                    {
                        var id = deviceList.GetProperty("Id").GetInt32();
                        await SendAsync(socket,
                            $"[{{\"DeviceList\":{{\"Id\":{id},\"Devices\":[{{\"DeviceIndex\":2,\"DeviceName\":\"Simulated Toy\",\"DeviceMessages\":{{\"VibrateCmd\":{{}},\"LinearCmd\":{{}},\"RotateCmd\":{{}}}}}}]}}}}]");
                    }
                    else if (envelope.TryGetProperty("StartScanning", out var scan))
                    {
                        var id = scan.GetProperty("Id").GetInt32();
                        await SendAsync(socket, $"[{{\"Ok\":{{\"Id\":{id}}}}}]");
                        await SendAsync(socket, "[{\"DeviceAdded\":{\"Id\":2,\"DeviceIndex\":2,\"DeviceName\":\"Simulated Toy\",\"DeviceMessages\":{\"VibrateCmd\":{},\"LinearCmd\":{},\"RotateCmd\":{}}}}]");
                        DeviceReady.TrySetResult();
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            await _app.StopAsync();
            await _app.DisposeAsync();
            _shutdown.Dispose();
        }

        private static async Task<string?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
        {
            var buffer = new byte[16 * 1024];
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            return Encoding.UTF8.GetString(buffer, 0, result.Count);
        }

        private static Task SendAsync(WebSocket socket, string message)
            => socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true,
                CancellationToken.None);
    }
}
