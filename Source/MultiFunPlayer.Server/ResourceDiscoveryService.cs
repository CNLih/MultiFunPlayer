using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MultiFunPlayer.Infrastructure.Output;

namespace MultiFunPlayer.Server;

public sealed class ResourceDiscoveryService
{
    private const int DiscoveryPort = 7359;
    private const int MaxResponseBytes = 16 * 1024;

    public async Task<IReadOnlyList<MediaServerDiscoveryResponse>> DiscoverMediaServersAsync(
        string kind, int timeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        var normalizedKind = kind.Trim().ToLowerInvariant();
        var message = normalizedKind switch
        {
            "emby" => "who is EmbyServer?",
            "jellyfin" => "Who is JellyfinServer?",
            _ => throw new ArgumentException("Media discovery supports emby or jellyfin.", nameof(kind))
        };
        if (timeoutMilliseconds is < 250 or > 5_000)
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds),
                "Discovery timeout must be between 250 and 5000 milliseconds.");

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        var payload = Encoding.UTF8.GetBytes(message);
        await socket.SendToAsync(payload, SocketFlags.None,
            new IPEndPoint(IPAddress.Broadcast, DiscoveryPort), cancellationToken).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMilliseconds);
        var results = new Dictionary<string, MediaServerDiscoveryResponse>(StringComparer.OrdinalIgnoreCase);
        while (!timeout.IsCancellationRequested)
        {
            var buffer = new byte[MaxResponseBytes];
            try
            {
                var response = await socket.ReceiveFromAsync(buffer, SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0), timeout.Token).ConfigureAwait(false);
                if (response.ReceivedBytes == 0 || response.RemoteEndPoint is not IPEndPoint remote)
                    continue;
                var candidate = ParseMediaServer(normalizedKind,
                    buffer.AsSpan(0, response.ReceivedBytes), remote.Address);
                if (candidate is not null)
                    results[candidate.ServerId ?? candidate.Endpoint] = candidate;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (JsonException) { }
            catch (DecoderFallbackException) { }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return results.Values.OrderBy(result => result.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<IReadOnlyList<ButtplugDeviceDiscoveryResponse>> DiscoverButtplugDevicesAsync(
        string endpoint, int scanMilliseconds, CancellationToken cancellationToken = default)
    {
        if (scanMilliseconds is < 250 or > 5_000)
            throw new ArgumentOutOfRangeException(nameof(scanMilliseconds),
                "Scan duration must be between 250 and 5000 milliseconds.");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("ws" or "wss") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Buttplug discovery requires a ws:// or wss:// endpoint.", nameof(endpoint));

        await using var adapter = new ButtplugOutputAdapter("discovery", uri);
        await adapter.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await Task.Delay(scanMilliseconds, cancellationToken).ConfigureAwait(false);
        return adapter.Devices.Select(device => new ButtplugDeviceDiscoveryResponse(
            device.DeviceIndex, device.DeviceName, device.ActuatorTypes)).ToArray();
    }

    private static MediaServerDiscoveryResponse? ParseMediaServer(string kind,
        ReadOnlySpan<byte> payload, IPAddress sourceAddress)
    {
        using var document = JsonDocument.Parse(payload.ToArray(), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return null;
        var address = ReadString(document.RootElement, "Address")
            ?? ReadString(document.RootElement, "EndpointAddress");
        var endpoint = NormalizeEndpoint(address, sourceAddress);
        if (endpoint is null)
            return null;
        var id = ReadString(document.RootElement, "Id");
        var name = ReadString(document.RootElement, "Name") ?? $"{kind} at {sourceAddress}";
        var apiKeyUrl = kind == "jellyfin"
            ? endpoint.TrimEnd('/') + "/web/index.html#/dashboard/keys"
            : endpoint.TrimEnd('/') + "/web/index.html#!/apikeys.html";
        return new(kind, id, name, endpoint, sourceAddress.ToString(), true, apiKeyUrl);
    }

    private static string? NormalizeEndpoint(string? address, IPAddress sourceAddress)
    {
        if (!string.IsNullOrWhiteSpace(address)
            && Uri.TryCreate(address.Trim(), UriKind.Absolute, out var advertised)
            && advertised.Scheme is "http" or "https" && string.IsNullOrEmpty(advertised.UserInfo))
            return advertised.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return new UriBuilder(Uri.UriSchemeHttp, sourceAddress.ToString(), 8096).Uri
            .GetLeftPart(UriPartial.Authority);
    }

    private static string? ReadString(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        }
        return null;
    }
}
