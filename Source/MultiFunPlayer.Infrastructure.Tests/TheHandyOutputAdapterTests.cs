using System.Net;
using System.Text;
using System.Text.Json;
using MultiFunPlayer.Application;
using MultiFunPlayer.Infrastructure.Output;
using MultiFunPlayer.Infrastructure.Transport;

namespace MultiFunPlayer.Infrastructure.Tests;

public sealed class TheHandyOutputAdapterTests
{
    [Fact]
    public async Task Uses_existing_v2_contract_and_recovers_after_send_failure()
    {
        using var simulator = new HandyProtocolSimulator();
        await using var adapter = new TheHandyOutputAdapter("handy-test", simulator.Endpoint,
            "test-connection-key", "L0", simulator.Handler);
        var fault = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.Faulted += exception => fault.TrySetResult(exception);

        var connected = await adapter.ConnectAsync();
        Assert.True(connected.Connected);
        Assert.True(adapter.IsConnected);

        adapter.Publish(new OutputFrame(TimeSpan.Zero,
            new Dictionary<string, double> { ["L0"] = 0.42 }));
        await WaitUntilAsync(() => simulator.Count("/hdsp/xpt") >= 1);
        var command = simulator.Single("/hdsp/xpt");
        Assert.Equal("PUT", command.Method);
        Assert.Equal("test-connection-key", command.ConnectionKey);
        using (var document = JsonDocument.Parse(command.Body))
        {
            Assert.Equal(42, document.RootElement.GetProperty("position").GetDouble());
            Assert.Equal(50, document.RootElement.GetProperty("duration").GetInt32());
            Assert.True(document.RootElement.GetProperty("immediateResponse").GetBoolean());
            Assert.True(document.RootElement.GetProperty("stopOnTarget").GetBoolean());
        }

        simulator.FailNextSend = true;
        adapter.Publish(new OutputFrame(TimeSpan.Zero,
            new Dictionary<string, double> { ["L0"] = 0.5 }));
        await WaitUntilAsync(() => !adapter.IsConnected);
        Assert.Contains("simulated connection reset",
            (await fault.Task.WaitAsync(TimeSpan.FromSeconds(2))).Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("simulated connection reset", adapter.LastError,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(adapter.IsConnected);

        Assert.True((await adapter.ConnectAsync()).Connected);
        adapter.Publish(new OutputFrame(TimeSpan.Zero,
            new Dictionary<string, double> { ["L0"] = 0.75 }));
        await WaitUntilAsync(() => simulator.Count("/hdsp/xpt") >= 3);
        Assert.Equal(3, simulator.Count("/hdsp/xpt"));
    }

    [Fact]
    public async Task Rejects_timeout_and_malformed_handshake_responses()
    {
        using var timeoutSimulator = new HandyProtocolSimulator { DelayResponses = true };
        await using var timeoutAdapter = new TheHandyOutputAdapter("handy-timeout",
            timeoutSimulator.Endpoint, "test-key", handler: timeoutSimulator.Handler,
            options: new OutputTransportOptions
            {
                ConnectTimeout = TimeSpan.FromMilliseconds(30),
                SendTimeout = TimeSpan.FromMilliseconds(30)
            });

        var timeout = await Assert.ThrowsAsync<OutputTransportException>(async () =>
            await timeoutAdapter.ConnectAsync());
        Assert.Equal("connect", timeout.Operation);
        Assert.False(timeoutAdapter.IsConnected);

        using var malformedSimulator = new HandyProtocolSimulator { MalformedResponses = true };
        await using var malformedAdapter = new TheHandyOutputAdapter("handy-malformed",
            malformedSimulator.Endpoint, "test-key", handler: malformedSimulator.Handler);

        var malformed = await Assert.ThrowsAsync<OutputTransportException>(async () =>
            await malformedAdapter.ConnectAsync());
        Assert.Equal("connect", malformed.Operation);
        Assert.Contains("malformed JSON", malformed.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Preserves_caller_cancellation_during_handshake()
    {
        using var simulator = new HandyProtocolSimulator { DelayResponses = true };
        await using var adapter = new TheHandyOutputAdapter("handy-cancel",
            simulator.Endpoint, "test-key", handler: simulator.Handler,
            options: new OutputTransportOptions
            {
                ConnectTimeout = TimeSpan.FromSeconds(5),
                SendTimeout = TimeSpan.FromSeconds(5)
            });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.ConnectAsync(cancellation.Token).AsTask());
        Assert.False(adapter.IsConnected);
        Assert.Null(adapter.LastError);
    }

    private sealed class HandyProtocolSimulator : IDisposable
    {
        private readonly HttpMessageHandler _handler;

        public HandyProtocolSimulator()
        {
            _handler = new HandySimulatorHandler(this);
            Endpoint = new Uri("https://handy-simulator.invalid/api/handy/v2/");
        }

        public Uri Endpoint { get; }
        public HttpMessageHandler Handler => _handler;
        public List<HandyRequest> Requests { get; } = [];
        public bool FailNextSend { get; set; }
        public bool DelayResponses { get; set; }
        public bool MalformedResponses { get; set; }

        public int Count(string path)
        {
            lock (Requests)
                return Requests.Count(request => request.Path == path);
        }

        public HandyRequest Single(string path)
        {
            lock (Requests)
                return Assert.Single(Requests.Where(request => request.Path == path));
        }

        public void Dispose() => _handler.Dispose();

        private sealed class HandySimulatorHandler(HandyProtocolSimulator simulator) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var body = request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                var path = request.RequestUri?.AbsolutePath.Split("/api/handy/v2/",
                    StringSplitOptions.None).LastOrDefault() ?? string.Empty;
                var record = new HandyRequest(request.Method.Method, "/" + path.Trim('/'), body,
                    request.Headers.GetValues("X-Connection-Key").SingleOrDefault());
                lock (simulator.Requests)
                    simulator.Requests.Add(record);

                if (simulator.FailNextSend && record.Path == "/hdsp/xpt")
                {
                    simulator.FailNextSend = false;
                    throw new HttpRequestException("simulated connection reset");
                }
                if (simulator.DelayResponses)
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

                var responseBody = simulator.MalformedResponses ? "{malformed"
                    : record.Path switch
                    {
                        "/connected" => "{\"connected\":true}",
                        "/info" => "{\"fwStatus\":0}",
                        "/mode" => "{\"result\":0}",
                        "/servertime" => "{\"serverTime\":123}",
                        "/hdsp/xpt" => "{\"result\":0}",
                        _ => "{}"
                    };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
                };
            }
        }

        public sealed record HandyRequest(string Method, string Path, string Body, string? ConnectionKey);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!predicate() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(predicate(), "Timed out waiting for simulator state.");
    }
}
