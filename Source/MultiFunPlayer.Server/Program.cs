using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Json;
using MultiFunPlayer.Application;
using MultiFunPlayer.Common;
using MultiFunPlayer.Infrastructure.Capabilities;
using MultiFunPlayer.Infrastructure.Serial;
using MultiFunPlayer.Infrastructure.Storage;
using MultiFunPlayer.Script;

namespace MultiFunPlayer.Server;

public static class ServerProgram
{
    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Configure(WebApplicationBuilder builder)
    {
        DeviceAxis.InitializeDefaults();
        var storage = StorageOptions.FromEnvironment();
        builder.Services.AddProblemDetails();
        builder.Services.Configure<JsonOptions>(options =>
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        builder.Services.AddSingleton<HeadlessPlayerEngine>();
        builder.Services.AddSingleton(storage);
        ConfigureKeyPersistence(builder, storage);
        builder.Services.AddSingleton<AuthTokenStore>();
        builder.Services.AddSingleton<ScriptCatalog>();
        builder.Services.AddSingleton<ConfigurationMigrationHostedService>();
        builder.Services.AddSingleton<IHostedService>(services =>
            services.GetRequiredService<ConfigurationMigrationHostedService>());
        builder.Services.AddSingleton<OutputTargetRegistry>();
        builder.Services.AddSingleton<IHostedService>(services =>
            services.GetRequiredService<OutputTargetRegistry>());
        builder.Services.AddSingleton<StateBroadcaster>();
        var runtimeLogStore = new RuntimeLogStore(builder.Configuration);
        builder.Services.AddSingleton(runtimeLogStore);
        builder.Logging.AddProvider(new RuntimeLogProvider(runtimeLogStore));
        builder.Services.AddHostedService<EngineTicker>();
    }

    private static void ConfigureKeyPersistence(WebApplicationBuilder builder, StorageOptions storage)
    {
        var explicitlyConfigured = Environment.GetEnvironmentVariable("MFP_KEYS_ROOT") is not null
            || Environment.GetEnvironmentVariable("MFP_KEYS_PATH") is not null;
        if (!explicitlyConfigured)
            return;

        try
        {
            Directory.CreateDirectory(storage.KeysRoot);
            builder.Services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(storage.KeysRoot))
                .SetApplicationName("MultiFunPlayer");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void Map(WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.Use(async (context, next) =>
        {
            if (IsHealthPath(context.Request.Path) || IsPublicAsset(context.Request.Path)
                || IsPairingPath(context.Request.Path))
            {
                await next(context);
                return;
            }

            var auth = context.RequestServices.GetRequiredService<AuthTokenStore>();
            if (!auth.IsConfigured)
            {
                await next(context);
                return;
            }

            var actual = ReadToken(context.Request);
            if (!auth.Validate(actual))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await Results.Problem(statusCode: StatusCodes.Status401Unauthorized,
                    title: "Authentication required", detail: "Provide a valid bearer token.")
                    .ExecuteAsync(context);
                return;
            }

            await next(context);
        });

        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/health/ready", (HeadlessPlayerEngine engine) =>
            Results.Ok(new { status = "ready", state = engine.State.ToString() }));

        app.MapGet("/api/v1/status", (HeadlessPlayerEngine engine) =>
            Results.Ok(ApiModelMapper.ToStatus(engine.Current)));
        app.MapPost("/api/v1/auth/pair", (PairRequest request, AuthTokenStore auth) =>
        {
            return auth.TryPair(request.PairingToken, request.AuthToken) switch
            {
                PairingResult.Paired => Results.Ok(new { status = "paired" }),
                PairingResult.PairingUnavailable => Problem(StatusCodes.Status503ServiceUnavailable,
                    "Pairing unavailable", "Configure a pairing token and writable configuration root.", "pairing_unavailable"),
                PairingResult.AlreadyConfigured => Problem(StatusCodes.Status409Conflict,
                    "Already configured", "The server already has an authentication token.", "already_configured"),
                PairingResult.InvalidPairingToken => Problem(StatusCodes.Status401Unauthorized,
                    "Invalid pairing token", "The pairing token was not accepted.", "invalid_pairing_token"),
                PairingResult.InvalidAuthToken => Problem(StatusCodes.Status400BadRequest,
                    "Invalid auth token", "The new auth token must be at least 16 characters.", "invalid_auth_token"),
                _ => Problem(StatusCodes.Status400BadRequest, "Pairing failed", "The pairing request was rejected.", "pairing_failed")
            };
        });
        app.MapGet("/api/v1/capabilities", (IConfiguration config) =>
        {
            var hz = ServerOptions.ResolveTickHz(config);
            return Results.Ok(new CapabilitiesResponse("v1", hz,
                ["play", "pause", "stop", "seek", "load"],
                ["funscript", .. InfrastructureCapabilities.All.Where(capability => capability.Available).Select(capability => capability.Id)],
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MFP_AUTH_TOKEN")), "/hubs/state"));
        });
        app.MapGet("/api/v1/adapters", () => Results.Ok(AdapterCapabilityCatalog.Current));
        app.MapGet("/api/v1/settings", (IConfiguration config, StorageOptions storage) =>
        {
            var tickHz = ServerOptions.ResolveTickHz(config);
            var maxClients = ServerOptions.ResolveMaxWebSocketClients(config);
            return Results.Ok(new SettingsResponse(1, tickHz, maxClients, storage.ConfigRoot,
                storage.ScriptsRoot, storage.LogsRoot, storage.KeysRoot,
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MFP_AUTH_TOKEN"))));
        });
        app.MapGet("/api/v1/configuration/migration",
            (ConfigurationMigrationHostedService migration) => Results.Ok(migration.Status));
        app.MapGet("/api/v1/output-targets", (OutputTargetRegistry registry) =>
            Results.Ok(registry.List()));
        app.MapPost("/api/v1/output-targets", async (OutputTargetRequest request,
            OutputTargetRegistry registry, CancellationToken cancellationToken) =>
            MapOutputTargetOperation(await registry.CreateAsync(ToDefinition(request.Id, request), cancellationToken)
                .ConfigureAwait(false), created: true));
        app.MapPut("/api/v1/output-targets/{id}", async (string id, OutputTargetRequest request,
            OutputTargetRegistry registry, CancellationToken cancellationToken) =>
            MapOutputTargetOperation(await registry.UpdateAsync(id, ToDefinition(id, request), cancellationToken)
                .ConfigureAwait(false), created: false));
        app.MapDelete("/api/v1/output-targets/{id}", async (string id,
            OutputTargetRegistry registry, CancellationToken cancellationToken) =>
            MapOutputTargetOperation(await registry.DeleteAsync(id, cancellationToken)
                .ConfigureAwait(false), created: false));
        app.MapPost("/api/v1/output-targets/{id}/connect", async (string id,
            OutputTargetRegistry registry, CancellationToken cancellationToken) =>
            MapOutputTargetOperation(await registry.ConnectAsync(id, cancellationToken)
                .ConfigureAwait(false), created: false));
        app.MapPost("/api/v1/output-targets/{id}/disconnect", async (string id,
            OutputTargetRegistry registry, CancellationToken cancellationToken) =>
            MapOutputTargetOperation(await registry.DisconnectAsync(id, cancellationToken)
                .ConfigureAwait(false), created: false));
        app.MapGet("/api/v1/devices/serial", (HttpContext context) =>
        {
            var root = Environment.GetEnvironmentVariable("MFP_SERIAL_BY_ID_ROOT");
            var ports = new LinuxSerialPortEnumerator(string.IsNullOrWhiteSpace(root)
                    ? "/dev/serial/by-id"
                    : root)
                .Enumerate(context.RequestAborted)
                .Select(port => new SerialDeviceResponse(port.StableId, port.DevicePath, port.LinkTarget));
            return Results.Ok(ports);
        });
        app.MapGet("/openapi/v1.json", () => Results.Json(OpenApiDocument.Value));
        app.MapGet("/api/v1/axes", (HeadlessPlayerEngine engine) => Results.Ok(engine.Current.Values));
        app.MapGet("/api/v1/logs", (int? limit, RuntimeLogStore store) =>
        {
            var requested = limit ?? 100;
            if (requested is < 1 or > 1000)
                return Problem(StatusCodes.Status400BadRequest, "Invalid log limit", "limit must be between 1 and 1000.", "invalid_limit");
            return Results.Ok(new RuntimeLogsResponse(store.Read(requested), store.DroppedCount > 0, store.Capacity));
        });

        app.MapGet("/api/v1/scripts", (ScriptCatalog catalog) => Results.Ok(catalog.List()));
        app.MapGet("/api/v1/scripts/{id}", (string id, ScriptCatalog catalog) =>
            catalog.TryRead(id, out var response)
                ? Results.Ok(response)
                : Problem(StatusCodes.Status404NotFound, "Script not found", "The requested script id is not in the catalog.", "script_not_found"));
        app.MapPost("/api/v1/scripts", (ScriptUploadRequest request, ScriptCatalog catalog) =>
        {
            if (!catalog.TryWrite(request.Id, request.Content, request.Overwrite,
                    out var descriptor, out var error, out var conflict))
            {
                if (conflict)
                    return Problem(StatusCodes.Status409Conflict, "Script already exists",
                        "Set overwrite=true to replace the existing script.", "script_conflict");
                return Problem(StatusCodes.Status400BadRequest, "Invalid script upload",
                    error ?? "The script upload was rejected.", "invalid_script_upload");
            }

            return Results.Created($"/api/v1/scripts/{Uri.EscapeDataString(descriptor!.Id)}", descriptor);
        });
        app.MapPost("/api/v1/scripts/load", (LoadScriptRequest request, ScriptCatalog catalog,
            HeadlessPlayerEngine engine) =>
        {
            string? name = request.Name;
            string? source = "api";
            string? content = null;
            if (!string.IsNullOrWhiteSpace(request.Id))
            {
                if (!catalog.TryRead(request.Id, out var file))
                    return Problem(StatusCodes.Status404NotFound, "Script not found", "The requested script id is not in the catalog.", "script_not_found");
                name = file!.Script.Name;
                source = $"catalog/{file.Script.Id}";
                content = file.Content;
            }
            else if (request.Content.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                content = request.Content.GetRawText();
            }
            else if (request.Content.ValueKind == JsonValueKind.String)
            {
                content = request.Content.GetString();
            }

            if (string.IsNullOrWhiteSpace(content))
                return Problem(StatusCodes.Status400BadRequest, "Script content required", "Provide an id or a funscript JSON document.", "content_or_id_required");

            var result = FunscriptReader.Default.FromText(name ?? "api.funscript", source!, content);
            if (!result.IsSuccess)
                return Problem(StatusCodes.Status400BadRequest, "Invalid funscript", "The supplied document could not be parsed.", "invalid_funscript");

            if (result.Resources is { Count: > 0 })
                engine.Load(result.Resources);
            else if (result.Resource is not null)
                engine.Load(result.Resource, request.Axis ?? "default");
            else
                return Problem(StatusCodes.Status400BadRequest, "Empty funscript", "The supplied document contains no actions.", "empty_funscript");

            return Results.Ok(ApiModelMapper.ToStatus(engine.Current));
        });

        app.MapPost("/api/v1/player/play", (HeadlessPlayerEngine engine) => Command(engine, engine.Play));
        app.MapPost("/api/v1/player/pause", (HeadlessPlayerEngine engine) => Command(engine, engine.Pause));
        app.MapPost("/api/v1/player/stop", (HeadlessPlayerEngine engine) => Command(engine, engine.Stop));
        app.MapPost("/api/v1/player/seek", (SeekRequest request, HeadlessPlayerEngine engine) =>
        {
            if (!double.IsFinite(request.PositionSeconds) || request.PositionSeconds < 0)
                return Problem(StatusCodes.Status400BadRequest, "Invalid seek position", "positionSeconds must be a finite non-negative number.", "invalid_position");
            engine.Seek(TimeSpan.FromSeconds(request.PositionSeconds));
            return Results.Ok(ApiModelMapper.ToStatus(engine.Current));
        });

        app.Map("/hubs/state", async context => await HandleWebSocket(context));
        app.UseStaticFiles();
        app.MapFallbackToFile("index.html");
    }

    private static IResult Command(HeadlessPlayerEngine engine, Action action)
    {
        action();
        return Results.Ok(ApiModelMapper.ToStatus(engine.Current));
    }

    private static OutputTargetDefinition ToDefinition(string? id, OutputTargetRequest request)
        => new(id ?? string.Empty, request.Name ?? string.Empty, request.Protocol ?? string.Empty,
            request.Endpoint ?? string.Empty, request.Enabled, request.AutoConnect,
            request.BaudRate, request.Parity, request.DataBits, request.StopBits,
            request.DtrEnable, request.RtsEnable);

    private static IResult MapOutputTargetOperation(OutputTargetOperation operation, bool created)
        => operation.Status switch
        {
            OutputTargetOperationStatus.Success when created
                => Results.Created($"/api/v1/output-targets/{Uri.EscapeDataString(operation.Target!.Id)}", operation.Target),
            OutputTargetOperationStatus.Success when operation.Target is null => Results.NoContent(),
            OutputTargetOperationStatus.Success => Results.Ok(operation.Target),
            OutputTargetOperationStatus.Invalid => Problem(StatusCodes.Status400BadRequest,
                "Invalid output target", operation.Error ?? "The output target is invalid.", "invalid_output_target"),
            OutputTargetOperationStatus.NotFound => Problem(StatusCodes.Status404NotFound,
                "Output target not found", operation.Error ?? "The output target does not exist.", "output_target_not_found"),
            OutputTargetOperationStatus.Conflict => Problem(StatusCodes.Status409Conflict,
                "Output target already exists", operation.Error ?? "The output target id is already in use.", "output_target_conflict"),
            OutputTargetOperationStatus.Unavailable => Problem(StatusCodes.Status503ServiceUnavailable,
                "Output target storage unavailable", operation.Error ?? "The output target could not be persisted.", "output_target_unavailable"),
            _ => Problem(StatusCodes.Status502BadGateway,
                "Output target connection failed", operation.Error ?? "The output target could not connect.", "output_target_failed")
        };

    private static async Task HandleWebSocket(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "WebSocket upgrade required").ExecuteAsync(context);
            return;
        }

        var broadcaster = context.RequestServices.GetRequiredService<StateBroadcaster>();
        if (!broadcaster.TrySubscribe(out var subscription) || subscription is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "WebSocket capacity reached").ExecuteAsync(context);
            return;
        }

        using (subscription)
        using (var socket = await context.WebSockets.AcceptWebSocketAsync(ReadAuthProtocol(context.Request)))
        {
            var engine = context.RequestServices.GetRequiredService<HeadlessPlayerEngine>();
            subscription.SetInitial(engine.Current);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var sendTask = SendSnapshots(socket, subscription.Reader, engine.Current, linked.Token);
            var receiveTask = DrainUntilClosed(socket, linked.Token);
            await Task.WhenAny(sendTask, receiveTask);
            linked.Cancel();
            try { await Task.WhenAll(sendTask, receiveTask); }
            catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or IOException) { }
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None);
        }
    }

    private static async Task SendSnapshots(WebSocket socket, ChannelReader<EngineSnapshot> reader,
        EngineSnapshot initial, CancellationToken cancellationToken)
    {
        await SendSnapshot(socket, initial, cancellationToken);
        await foreach (var snapshot in reader.ReadAllAsync(cancellationToken))
            await SendSnapshot(socket, snapshot, cancellationToken);
    }

    private static async Task SendSnapshot(WebSocket socket, EngineSnapshot snapshot, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(ApiModelMapper.ToStatus(snapshot), StateJsonOptions);
        await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task DrainUntilClosed(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[512];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                break;
        }
    }

    private static bool IsHealthPath(PathString path) => path.StartsWithSegments("/health");
    private static bool IsPairingPath(PathString path)
        => path.Equals("/api/v1/auth/pair", StringComparison.OrdinalIgnoreCase);
    private static bool IsPublicAsset(PathString path)
        => path == "/" || path.StartsWithSegments("/public") || path.StartsWithSegments("/assets")
            || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/manifest.webmanifest", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/icon.svg", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/sw.js", StringComparison.OrdinalIgnoreCase);

    private static string? ReadToken(HttpRequest request)
    {
        if (request.Headers.TryGetValue("X-MFP-Token", out var value))
            return value.FirstOrDefault();
        var authorization = request.Headers.Authorization.FirstOrDefault();
        if (authorization?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
            return authorization[7..].Trim();
        if (!request.HttpContext.WebSockets.IsWebSocketRequest)
            return null;
        var protocol = ReadAuthProtocol(request);
        if (protocol is null)
            return null;

        try
        {
            var encoded = protocol["mfp-auth.".Length..].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? ReadAuthProtocol(HttpRequest request)
        => request.Headers["Sec-WebSocket-Protocol"].SelectMany(value => value?.Split(',') ?? [])
            .Select(value => value.Trim())
            .FirstOrDefault(value => value.StartsWith("mfp-auth.", StringComparison.Ordinal));

    private static bool CryptographicEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static IResult Problem(int statusCode, string title, string detail, string code)
        => Results.Problem(statusCode: statusCode, title: title, detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });
}

internal static class OpenApiDocument
{
    public static readonly object Value = new
    {
        openapi = "3.0.3",
        info = new { title = "MultiFunPlayer Server API", version = "1.0.0" },
        paths = new Dictionary<string, object>
        {
            ["/health/live"] = new { get = Operation("Liveness") },
            ["/health/ready"] = new { get = Operation("Readiness") },
            ["/api/v1/status"] = new { get = Operation("Current player status") },
            ["/api/v1/auth/pair"] = new { post = Operation("Initial administrator pairing") },
            ["/api/v1/capabilities"] = new { get = Operation("Server capabilities") },
            ["/api/v1/adapters"] = new { get = Operation("Adapter and integration capabilities") },
            ["/api/v1/settings"] = new { get = Operation("Effective non-secret settings") },
            ["/api/v1/configuration/migration"] = new { get = Operation("Legacy configuration migration status") },
            ["/api/v1/output-targets"] = new { get = Operation("List output targets"), post = Operation("Create output target") },
            ["/api/v1/output-targets/{id}"] = new { put = Operation("Update output target"), delete = Operation("Delete output target") },
            ["/api/v1/output-targets/{id}/connect"] = new { post = Operation("Connect output target") },
            ["/api/v1/output-targets/{id}/disconnect"] = new { post = Operation("Disconnect output target") },
            ["/api/v1/devices/serial"] = new { get = Operation("Stable Linux serial devices") },
            ["/api/v1/logs"] = new { get = Operation("Bounded redacted runtime logs") },
            ["/api/v1/scripts"] = new { get = Operation("List scripts"), post = Operation("Upload a JSON script") },
            ["/api/v1/scripts/load"] = new { post = Operation("Load a Funscript") },
            ["/api/v1/player/play"] = new { post = Operation("Play") },
            ["/api/v1/player/pause"] = new { post = Operation("Pause") },
            ["/api/v1/player/stop"] = new { post = Operation("Stop") },
            ["/api/v1/player/seek"] = new { post = Operation("Seek") },
            ["/api/v1/axes"] = new { get = Operation("Current axes") },
            ["/hubs/state"] = new { get = Operation("WebSocket state stream") }
        },
        components = new
        {
            securitySchemes = new
            {
                bearerAuth = new { type = "http", scheme = "bearer" },
                tokenHeader = new { type = "apiKey", name = "X-MFP-Token", @in = "header" }
            }
        }
    };

    private static object Operation(string summary) => new
    {
        summary,
        responses = new Dictionary<string, object> { ["200"] = new { description = "Success" } }
    };
}

public partial class Program { }

public static class ProgramEntryPoint
{
    public static void Main(string[] args)
    {
        var outputWebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var options = new WebApplicationOptions
        {
            Args = args,
            WebRootPath = Directory.Exists(outputWebRoot) ? outputWebRoot : null,
        };
        var builder = WebApplication.CreateBuilder(options);
        ServerProgram.Configure(builder);
        var app = builder.Build();
        ServerProgram.Map(app);
        app.Run();
    }
}
