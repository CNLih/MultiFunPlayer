using System.Text.Json;
using MultiFunPlayer.Application;
using MultiFunPlayer.Infrastructure.Storage;

namespace MultiFunPlayer.Server;

public enum RuntimeRoutingOperationStatus
{
    Success,
    Invalid,
    Unavailable
}

public sealed record RuntimeRoutingOperation(
    RuntimeRoutingOperationStatus Status,
    IReadOnlyList<RuntimeSourceRouteResponse>? Routes = null,
    string? Error = null);

public sealed record RuntimeSourceRouteDefinition(
    string SourceId,
    string[] OutputIds,
    bool LoopEnabled = false,
    string? ScriptId = null,
    string ScriptMatchMode = "global");

public sealed class RuntimeRoutingRegistry : IHostedService, IAsyncDisposable
{
    private const string StoreFile = "runtime-routes.json";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MediaSourceRegistry _sources;
    private readonly OutputTargetRegistry _outputs;
    private readonly ScriptCatalog _scripts;
    private readonly RuntimeScriptMatcher _matcher;
    private readonly ILogger<RuntimeRoutingRegistry> _logger;
    private readonly RootedFileStore? _config;
    private RuntimeSourceRouteDefinition[] _routes = [];
    private int _disposed;

    public RuntimeRoutingRegistry(StorageOptions storage, MediaSourceRegistry sources,
        OutputTargetRegistry outputs, ScriptCatalog scripts, RuntimeScriptMatcher matcher,
        ILogger<RuntimeRoutingRegistry> logger)
    {
        _sources = sources;
        _outputs = outputs;
        _scripts = scripts;
        _matcher = matcher;
        _logger = logger;
        try { _config = new RootedFileStore(storage.ConfigRoot); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Runtime route persistence is unavailable.");
        }
    }

    public IReadOnlyList<RuntimeSourceRouteResponse> List()
    {
        _gate.Wait();
        try { return ToResponses(_routes); }
        finally { _gate.Release(); }
    }

    public RuntimeSourceRouteDefinition[] Snapshot()
    {
        _gate.Wait();
        try { return _routes.Select(Clone).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        RuntimeSourceRouteDefinition[] loaded;
        if (_config is null)
            return;
        try
        {
            loaded = await _config.ReadJsonAsync<RuntimeSourceRouteDefinition[]>(StoreFile,
                cancellationToken).ConfigureAwait(false) ?? [];
        }
        catch (FileNotFoundException)
        {
            loaded = [];
        }
        catch (JsonException exception)
        {
            _logger.LogError(exception, "Ignoring malformed runtime route configuration.");
            loaded = [];
        }

        var normalized = ResolveScriptBindings(Normalize(loaded)).Select(route =>
        {
            if (route.ScriptId is null || _scripts.Contains(route.ScriptId))
                return route;
            _logger.LogWarning("Runtime route for {SourceId} references missing script {ScriptId}; leaving it unmatched.",
                route.SourceId, route.ScriptId);
            return route with { ScriptId = null };
        }).ToArray();
        var validation = Validate(normalized);
        if (validation is not null)
        {
            _logger.LogWarning("Ignoring runtime route configuration: {Reason}", validation);
            normalized = [];
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { _routes = normalized; }
        finally { _gate.Release(); }
        await ApplyOutputRoutingAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<RuntimeRoutingOperation> SetAsync(IEnumerable<RuntimeSourceRouteDefinition> routes,
        CancellationToken cancellationToken = default)
    {
        var normalized = ResolveScriptBindings(Normalize(routes));
        var validation = Validate(normalized);
        if (validation is not null)
            return new(RuntimeRoutingOperationStatus.Invalid, Error: validation);
        if (_config is null)
            return new(RuntimeRoutingOperationStatus.Unavailable,
                Error: "The configured root is not writable.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _config.WriteJsonAsync(StoreFile, normalized, cancellationToken).ConfigureAwait(false);
            _routes = normalized;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(RuntimeRoutingOperationStatus.Unavailable, Error: exception.Message);
        }
        finally
        {
            _gate.Release();
        }
        await ApplyOutputRoutingAsync(cancellationToken).ConfigureAwait(false);
        return new(RuntimeRoutingOperationStatus.Success, List());
    }

    public async Task RemoveSourceAsync(string sourceId, CancellationToken cancellationToken = default)
        => await RemoveAsync(route => route.SourceId.Equals(sourceId, StringComparison.OrdinalIgnoreCase),
            cancellationToken).ConfigureAwait(false);

    public async Task RemoveOutputAsync(string outputId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        RuntimeSourceRouteDefinition[] next;
        try
        {
            next = _routes.Select(route => route with
                {
                    OutputIds = route.OutputIds.Where(id => !id.Equals(outputId,
                        StringComparison.OrdinalIgnoreCase)).ToArray()
                })
                .Where(route => route.OutputIds.Length > 0).ToArray();
        }
        finally
        {
            _gate.Release();
        }
        await SetAsync(next, cancellationToken).ConfigureAwait(false);
    }

    private async Task RemoveAsync(Func<RuntimeSourceRouteDefinition, bool> predicate,
        CancellationToken cancellationToken)
    {
        RuntimeSourceRouteDefinition[] next;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { next = _routes.Where(route => !predicate(route)).Select(Clone).ToArray(); }
        finally { _gate.Release(); }
        await SetAsync(next, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyOutputRoutingAsync(CancellationToken cancellationToken)
    {
        var routedOutputs = Snapshot().SelectMany(route => route.OutputIds).ToArray();
        await _outputs.SetRuntimeRoutedOutputsAsync(routedOutputs, cancellationToken)
            .ConfigureAwait(false);
    }

    private string? Validate(RuntimeSourceRouteDefinition[] routes)
    {
        if (routes.Length > 128 || routes.Sum(route => route.OutputIds.Length) > 512)
            return "runtime routing supports at most 128 sources and 512 connections.";
        var assignedOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in routes)
        {
            if (!_sources.Contains(route.SourceId))
                return $"media source '{route.SourceId}' does not exist.";
            if (route.ScriptMatchMode is not ("exact" or "fuzzy" or "manual" or "global"))
                return $"script match mode '{route.ScriptMatchMode}' is not supported.";
            if (route.ScriptId is not null && !_scripts.Contains(route.ScriptId))
                return $"script '{route.ScriptId}' does not exist.";
            foreach (var outputId in route.OutputIds)
            {
                if (!_outputs.Contains(outputId))
                    return $"output target '{outputId}' does not exist.";
                if (!assignedOutputs.Add(outputId))
                    return $"output target '{outputId}' cannot be assigned to more than one source.";
            }
        }
        return null;
    }

    private static RuntimeSourceRouteDefinition[] Normalize(IEnumerable<RuntimeSourceRouteDefinition> routes)
        => routes.Where(route => !string.IsNullOrWhiteSpace(route.SourceId))
            .GroupBy(route => route.SourceId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new RuntimeSourceRouteDefinition(group.Key,
                group.SelectMany(route => route.OutputIds ?? [])
                    .Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                group.Any(route => route.LoopEnabled),
                group.Select(route => route.ScriptId?.Trim())
                    .FirstOrDefault(scriptId => !string.IsNullOrWhiteSpace(scriptId)),
                group.Select(route => (route.ScriptMatchMode ?? "global").Trim().ToLowerInvariant()).First()))
            .Where(route => route.OutputIds.Length > 0)
            .OrderBy(route => route.SourceId, StringComparer.OrdinalIgnoreCase).ToArray();

    private static RuntimeSourceRouteDefinition Clone(RuntimeSourceRouteDefinition route)
        => route with { OutputIds = [.. route.OutputIds] };

    private RuntimeSourceRouteDefinition[] ResolveScriptBindings(
        IEnumerable<RuntimeSourceRouteDefinition> routes)
        => routes.Select(route =>
        {
            if (route.ScriptMatchMode == "global")
                return route with { ScriptId = null };
            if (route.ScriptMatchMode == "manual")
                return route;
            if (route.ScriptMatchMode is not ("exact" or "fuzzy"))
                return route;

            var mediaPath = _sources.GetClock(route.SourceId)?.State.Path;
            if (string.IsNullOrWhiteSpace(mediaPath))
                return route;
            return route with
            {
                ScriptId = _matcher.Match(mediaPath, fuzzy: route.ScriptMatchMode == "fuzzy")
            };
        }).ToArray();

    private static IReadOnlyList<RuntimeSourceRouteResponse> ToResponses(
        IEnumerable<RuntimeSourceRouteDefinition> routes)
        => routes.Select(route => new RuntimeSourceRouteResponse(route.SourceId,
            [.. route.OutputIds], route.LoopEnabled, route.ScriptId,
            route.ScriptMatchMode)).ToArray();

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class RuntimeRouteTicker : BackgroundService
{
    private readonly RuntimeRoutingRegistry _routes;
    private readonly MediaSourceRegistry _sources;
    private readonly OutputTargetRegistry _outputs;
    private readonly HeadlessPlayerEngine _engine;
    private readonly RuntimeScriptEvaluator _scriptEvaluator;
    private readonly ILogger<RuntimeRouteTicker> _logger;
    private readonly TimeSpan _interval;
    private readonly HashSet<string> _loopTriggered = new(StringComparer.OrdinalIgnoreCase);

    public RuntimeRouteTicker(RuntimeRoutingRegistry routes, MediaSourceRegistry sources,
        OutputTargetRegistry outputs, HeadlessPlayerEngine engine,
        RuntimeScriptEvaluator scriptEvaluator, IConfiguration configuration,
        ILogger<RuntimeRouteTicker> logger)
    {
        _routes = routes;
        _sources = sources;
        _outputs = outputs;
        _engine = engine;
        _scriptEvaluator = scriptEvaluator;
        _logger = logger;
        _interval = TimeSpan.FromSeconds(1d / ServerOptions.ResolveTickHz(configuration));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try { await TickAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { _logger.LogError(exception, "Runtime route tick failed."); }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var axisConfigurations = _engine.AxisConfigurations;
        foreach (var route in _routes.Snapshot())
        {
            var internalSource = _sources.GetKind(route.SourceId)?.Equals("internal",
                StringComparison.OrdinalIgnoreCase) == true;
            MediaClockResponse? state = null;
            TimeSpan position;
            if (internalSource)
            {
                position = _engine.Current.Position;
            }
            else
            {
                var sample = _sources.GetClock(route.SourceId);
                if (sample is not { Connected: true })
                    continue;
                state = sample.State;
                position = TimeSpan.FromSeconds(Math.Max(0, state.PositionSeconds));
                if (state.Playing)
                {
                    var elapsed = DateTimeOffset.UtcNow - sample.UpdatedAtUtc;
                    position += TimeSpan.FromSeconds(Math.Max(0, elapsed.TotalSeconds) *
                        Math.Clamp(state.PlaybackSpeed, 0.25, 4));
                }
                if (state.DurationSeconds > 0)
                    position = TimeSpan.FromSeconds(Math.Min(position.TotalSeconds, state.DurationSeconds));
            }

            OutputFrame frame;
            if (route.ScriptId is not null)
            {
                if (!_scriptEvaluator.TryEvaluate(route.ScriptId, position, axisConfigurations, out frame))
                    continue;
            }
            else if (route.ScriptMatchMode == "global")
            {
                if (_engine.Current.ScriptName is null)
                    continue;
                frame = _engine.Evaluate(position);
            }
            else
            {
                continue;
            }
            foreach (var outputId in route.OutputIds)
                _outputs.PublishRouted(outputId, frame);

            if (state is null || !route.LoopEnabled || !state.Playing || state.DurationSeconds <= 0)
            {
                _loopTriggered.Remove(route.SourceId);
                continue;
            }
            var loopThreshold = Math.Max(0, state.DurationSeconds - 0.35);
            if (position.TotalSeconds >= loopThreshold && _loopTriggered.Add(route.SourceId))
                await _sources.SeekAsync(route.SourceId, TimeSpan.Zero, cancellationToken)
                    .ConfigureAwait(false);
            else if (position.TotalSeconds < Math.Max(0, state.DurationSeconds - 1))
                _loopTriggered.Remove(route.SourceId);
        }
    }
}
