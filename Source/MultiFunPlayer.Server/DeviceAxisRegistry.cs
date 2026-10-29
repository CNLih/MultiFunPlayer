using System.Text.Json;
using MultiFunPlayer.Application;
using MultiFunPlayer.Common;
using MultiFunPlayer.Infrastructure.Storage;

namespace MultiFunPlayer.Server;

public sealed record DeviceAxisResponse(
    string Axis,
    double Minimum,
    double Maximum,
    bool Inverted,
    string Interpolation,
    bool LimitEnabled,
    double LimitMinimum,
    double LimitMaximum,
    double? MaximumTravel);

public sealed record DeviceAxisRequest(
    string? Axis,
    double Minimum = 0,
    double Maximum = 1,
    bool Inverted = false,
    string Interpolation = "Linear",
    bool LimitEnabled = false,
    double LimitMinimum = 0,
    double LimitMaximum = 1,
    double? MaximumTravel = null);

public sealed record DeviceAxesRequest(IReadOnlyList<DeviceAxisRequest>? Axes);

public enum DeviceAxisOperationStatus
{
    Success,
    Invalid,
    NotFound,
    Unavailable
}

public sealed record DeviceAxisOperation(
    DeviceAxisOperationStatus Status,
    DeviceAxisResponse? Axis = null,
    string? Error = null);

public sealed record DeviceAxesOperation(
    DeviceAxisOperationStatus Status,
    IReadOnlyList<DeviceAxisResponse>? Axes = null,
    string? Error = null);

/// <summary>
/// Persists and applies the portable per-axis output policy. This registry is
/// intentionally independent from ASP.NET DTOs so the engine remains usable
/// by a CLI, service, or another host.
/// </summary>
public sealed class DeviceAxisRegistry : IHostedService, IAsyncDisposable
{
    private const string StoreFile = "device-axis-settings.json";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stopGate = new();
    private Task? _stopTask;
    private readonly Dictionary<string, AxisConfiguration> _axes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly RootedFileStore? _config;
    private readonly HeadlessPlayerEngine _engine;
    private readonly ILogger<DeviceAxisRegistry> _logger;
    private bool _started;
    private int _disposed;

    public DeviceAxisRegistry(StorageOptions storage, HeadlessPlayerEngine engine,
        ILogger<DeviceAxisRegistry> logger)
    {
        DeviceAxis.InitializeDefaults();
        _engine = engine;
        _logger = logger;
        foreach (var axis in DeviceAxis.All)
            _axes[axis.Name] = AxisConfiguration.Default(axis.Name);
        _engine.SetAxisConfigurations(_axes.Values);
        try
        {
            _config = new RootedFileStore(storage.ConfigRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Device axis settings persistence is unavailable.");
        }
    }

    public IReadOnlyList<DeviceAxisResponse> List()
    {
        _gate.Wait();
        try
        {
            return DeviceAxis.All.Select(axis => ToResponse(_axes[axis.Name])).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started)
                return;
            _started = true;
            if (_config is null)
                return;

            AxisSettingsDocument? document;
            try
            {
                document = await _config.ReadJsonAsync<AxisSettingsDocument>(StoreFile,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                document = null;
            }
            catch (JsonException exception)
            {
                _logger.LogWarning(exception, "Device axis settings are invalid; defaults will be used.");
                document = null;
            }

            if (document is null)
                return;
            if (document.SchemaVersion != 1)
            {
                _logger.LogWarning("Ignoring unsupported device axis settings schema {SchemaVersion}.",
                    document.SchemaVersion);
                return;
            }

            foreach (var definition in document.Axes ?? [])
            {
                if (TryConvert(definition, out var configuration, out var error))
                    _axes[configuration.Axis] = configuration;
                else
                    _logger.LogWarning("Ignoring invalid device axis setting: {Error}", error);
            }
            _engine.SetAxisConfigurations(_axes.Values);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // The host and DI disposal may both request shutdown. Share one flush
        // and never acquire a semaphore after it has been disposed.
        lock (_stopGate)
            return _stopTask ??= StopCoreAsync(cancellationToken);
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_config is not null && _started)
                await PersistLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DeviceAxisOperation> UpsertAsync(string axis, DeviceAxisRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(axis, request.Axis ?? axis, StringComparison.OrdinalIgnoreCase))
            return new(DeviceAxisOperationStatus.Invalid, Error: "The route axis must match the request axis.");
        var definition = new DeviceAxisDefinition(axis, request.Minimum, request.Maximum,
            request.Inverted, request.Interpolation, request.LimitEnabled,
            request.LimitMinimum, request.LimitMaximum, request.MaximumTravel);
        if (!TryConvert(definition, out var configuration, out var error))
            return new(DeviceAxisOperationStatus.Invalid, Error: error);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_config is null)
                return new(DeviceAxisOperationStatus.Unavailable,
                    Error: "The configured root is not writable.");
            var updated = new Dictionary<string, AxisConfiguration>(_axes,
                StringComparer.OrdinalIgnoreCase)
            {
                [configuration.Axis] = configuration
            };
            await PersistAsync(updated.Values, cancellationToken).ConfigureAwait(false);
            ReplaceLocked(updated.Values);
            return new(DeviceAxisOperationStatus.Success, ToResponse(configuration));
        }
        catch (IOException exception)
        {
            return new(DeviceAxisOperationStatus.Unavailable, Error: exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DeviceAxesOperation> ReplaceAsync(DeviceAxesRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Axes is null)
            return new(DeviceAxisOperationStatus.Invalid, Error: "axes is required.");

        var configurations = new Dictionary<string, AxisConfiguration>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in request.Axes)
        {
            var definition = new DeviceAxisDefinition(item.Axis ?? string.Empty,
                item.Minimum, item.Maximum, item.Inverted, item.Interpolation,
                item.LimitEnabled, item.LimitMinimum, item.LimitMaximum,
                item.MaximumTravel);
            if (!TryConvert(definition, out var configuration, out var error))
                return new(DeviceAxisOperationStatus.Invalid, Error: error);
            if (!configurations.TryAdd(configuration.Axis, configuration))
                return new(DeviceAxisOperationStatus.Invalid,
                    Error: $"Axis '{configuration.Axis}' is duplicated.");
        }

        var missing = DeviceAxis.All.Where(axis => !configurations.ContainsKey(axis.Name))
            .Select(axis => axis.Name).ToArray();
        if (missing.Length > 0 || configurations.Count != DeviceAxis.All.Length)
        {
            return new(DeviceAxisOperationStatus.Invalid,
                Error: $"The complete fixed axis set is required. Missing: {string.Join(", ", missing)}.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_config is null)
                return new(DeviceAxisOperationStatus.Unavailable,
                    Error: "The configured root is not writable.");
            await PersistAsync(configurations.Values, cancellationToken).ConfigureAwait(false);
            ReplaceLocked(configurations.Values);
            return new(DeviceAxisOperationStatus.Success, ListLocked());
        }
        catch (IOException exception)
        {
            return new(DeviceAxisOperationStatus.Unavailable, Error: exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DeviceAxisOperation> DeleteAsync(string axis,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(axis))
            return new(DeviceAxisOperationStatus.Invalid, Error: "An axis name is required.");
        var standardAxis = DeviceAxis.All.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, axis, StringComparison.OrdinalIgnoreCase));
        if (standardAxis is null)
            return new(DeviceAxisOperationStatus.NotFound, Error: "Device axis setting not found.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_config is null)
                return new(DeviceAxisOperationStatus.Unavailable,
                    Error: "The configured root is not writable.");
            var updated = new Dictionary<string, AxisConfiguration>(_axes,
                StringComparer.OrdinalIgnoreCase)
            {
                [standardAxis.Name] = AxisConfiguration.Default(standardAxis.Name)
            };
            await PersistAsync(updated.Values, cancellationToken).ConfigureAwait(false);
            ReplaceLocked(updated.Values);
            return new(DeviceAxisOperationStatus.Success);
        }
        catch (IOException exception)
        {
            return new(DeviceAxisOperationStatus.Unavailable, Error: exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _gate.Dispose();
    }

    private async Task PersistLockedAsync(CancellationToken cancellationToken)
        => await PersistAsync(_axes.Values, cancellationToken).ConfigureAwait(false);

    private async Task PersistAsync(IEnumerable<AxisConfiguration> configurations,
        CancellationToken cancellationToken)
    {
        if (_config is null)
            throw new IOException("The configured root is not writable.");
        var document = new AxisSettingsDocument(1,
            DeviceAxis.All.Select(axis => configurations.First(configuration =>
                    string.Equals(configuration.Axis, axis.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(ToDefinition).ToArray());
        await _config.WriteJsonAsync(StoreFile, document, cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<DeviceAxisResponse> ListLocked()
        => DeviceAxis.All.Select(axis => ToResponse(_axes[axis.Name])).ToArray();

    private void ReplaceLocked(IEnumerable<AxisConfiguration> configurations)
    {
        _axes.Clear();
        foreach (var configuration in configurations)
            _axes[configuration.Axis] = configuration;
        _engine.SetAxisConfigurations(_axes.Values);
    }

    private static bool TryConvert(DeviceAxisDefinition definition,
        out AxisConfiguration configuration, out string? error)
    {
        configuration = AxisConfiguration.Default(definition.Axis);
        error = null;
        var standardAxis = DeviceAxis.All.FirstOrDefault(axis =>
            string.Equals(axis.Name, definition.Axis, StringComparison.OrdinalIgnoreCase));
        if (standardAxis is null)
        {
            error = $"axis must be one of: {string.Join(", ", DeviceAxis.All.Select(axis => axis.Name))}.";
            return false;
        }
        if (!Enum.TryParse<InterpolationType>(definition.Interpolation, true, out var interpolation)
            || !Enum.IsDefined(interpolation))
        {
            error = "interpolation must be Linear, Pchip, Makima, or Step.";
            return false;
        }
        try
        {
            configuration = new AxisConfiguration(standardAxis.Name, definition.Minimum,
                definition.Maximum, definition.Inverted, interpolation, definition.LimitEnabled,
                definition.LimitMinimum, definition.LimitMaximum,
                definition.MaximumTravel).Normalize();
            return true;
        }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static DeviceAxisResponse ToResponse(AxisConfiguration configuration)
        => new(configuration.Axis, configuration.Minimum, configuration.Maximum,
            configuration.Inverted, configuration.Interpolation.ToString(),
            configuration.LimitEnabled, configuration.LimitMinimum, configuration.LimitMaximum,
            configuration.MaximumTravel);

    private static DeviceAxisDefinition ToDefinition(AxisConfiguration configuration)
        => new(configuration.Axis, configuration.Minimum, configuration.Maximum,
            configuration.Inverted, configuration.Interpolation.ToString(),
            configuration.LimitEnabled, configuration.LimitMinimum, configuration.LimitMaximum,
            configuration.MaximumTravel);

    private sealed record AxisSettingsDocument(int SchemaVersion, DeviceAxisDefinition[] Axes);

    private sealed record DeviceAxisDefinition(
        string Axis,
        double Minimum,
        double Maximum,
        bool Inverted,
        string Interpolation,
        bool LimitEnabled,
        double LimitMinimum,
        double LimitMaximum,
        double? MaximumTravel);
}
