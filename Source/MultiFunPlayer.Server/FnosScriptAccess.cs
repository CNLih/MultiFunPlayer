using MultiFunPlayer.Infrastructure.Storage;

namespace MultiFunPlayer.Server;

public sealed class FnosScriptAccess : IHostedService
{
    public const string ExternalScriptsRoot = "/scripts/external";
    private const string StateFileName = ".fnos-script-access.state";
    private const string FnosVersionFile = "/usr/trim/BUILD_VERSION";
    private readonly string _statePath;
    private readonly bool _available;
    private readonly string? _systemVersion;
    private string _appliedGeneration = string.Empty;

    public FnosScriptAccess(StorageOptions storage)
        : this(storage.ConfigRoot, ReadEnvironment())
    {
    }

    public FnosScriptAccess(string configRoot, IReadOnlyDictionary<string, string?> environment)
    {
        _statePath = Path.Combine(configRoot, StateFileName);
        _systemVersion = ReadValue(environment, "MFP_FNOS_SYSTEM_VERSION")
            ?? ReadValue(environment, "TRIM_SYS_VERSION")
            ?? ReadMountedVersion();
        _available = string.Equals(ReadValue(environment, "MFP_PLATFORM"), "fnos",
                StringComparison.OrdinalIgnoreCase)
            || _systemVersion is not null
            || Directory.Exists("/usr/trim");
    }

    /// <summary>Read the fnOS version from a read-only mount of the host
    /// /usr/trim directory (present when the app runs on a real fnOS device).</summary>
    private static string? ReadMountedVersion()
    {
        try
        {
            return File.Exists(FnosVersionFile)
                ? File.ReadAllText(FnosVersionFile).Trim()
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_available)
            _appliedGeneration = ReadState().Generation;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public FnosScriptAccessResponse Snapshot()
    {
        if (!_available)
            return new(false, null, false, false, false, 0, 0, ExternalScriptsRoot);

        var state = ReadState();
        var restartRequired = !string.Equals(state.Generation, _appliedGeneration,
            StringComparison.Ordinal);
        var configured = state.AuthorizedDirectoryCount > 0;
        var active = configured && state.SelectedDirectoryAvailable && !restartRequired;
        return new(true, _systemVersion, configured, active, restartRequired,
            state.AuthorizedDirectoryCount, active ? 1 : 0, ExternalScriptsRoot);
    }

    private ScriptAccessState ReadState()
    {
        if (!File.Exists(_statePath))
            return ScriptAccessState.Empty;

        try
        {
            var values = File.ReadLines(_statePath)
                .Take(16)
                .Select(line => line.Split('=', 2, StringSplitOptions.TrimEntries))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);
            var generation = values.GetValueOrDefault("generation") ?? string.Empty;
            var count = int.TryParse(values.GetValueOrDefault("authorizedDirectoryCount"), out var parsed)
                ? Math.Clamp(parsed, 0, 64)
                : 0;
            var available = values.GetValueOrDefault("selectedDirectoryAvailable") == "1";
            return new(generation, count, available);
        }
        catch (IOException)
        {
            return ScriptAccessState.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return ScriptAccessState.Empty;
        }
    }

    private static string? ReadValue(IReadOnlyDictionary<string, string?> environment, string key)
        => environment.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static IReadOnlyDictionary<string, string?> ReadEnvironment()
        => new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["MFP_PLATFORM"] = Environment.GetEnvironmentVariable("MFP_PLATFORM"),
            ["MFP_FNOS_SYSTEM_VERSION"] = Environment.GetEnvironmentVariable("MFP_FNOS_SYSTEM_VERSION"),
            ["TRIM_SYS_VERSION"] = Environment.GetEnvironmentVariable("TRIM_SYS_VERSION"),
        };

    private sealed record ScriptAccessState(
        string Generation,
        int AuthorizedDirectoryCount,
        bool SelectedDirectoryAvailable)
    {
        public static ScriptAccessState Empty { get; } = new(string.Empty, 0, false);
    }
}
