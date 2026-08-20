namespace MultiFunPlayer.Infrastructure.Storage;

/// <summary>Filesystem roots used by the headless host.</summary>
public sealed record StorageOptions
{
    public string ConfigRoot { get; init; } = "/config";
    public string KeysRoot { get; init; } = "/keys";
    public string ScriptsRoot { get; init; } = "/scripts";
    public string LogsRoot { get; init; } = "/logs";

    public static StorageOptions FromEnvironment(IReadOnlyDictionary<string, string?>? environment = null)
    {
        return new StorageOptions
        {
            ConfigRoot = ValueOrDefault(environment, "MFP_CONFIG_ROOT", "MFP_CONFIG_PATH", "/config"),
            KeysRoot = ValueOrDefault(environment, "MFP_KEYS_ROOT", "MFP_KEYS_PATH", "/keys"),
            ScriptsRoot = ValueOrDefault(environment, "MFP_SCRIPTS_ROOT", "MFP_SCRIPTS_PATH", "/scripts"),
            LogsRoot = ValueOrDefault(environment, "MFP_LOGS_ROOT", "MFP_LOGS_PATH", "/logs")
        };
    }

    private static string ValueOrDefault(IReadOnlyDictionary<string, string?>? environment, string key, string alias, string fallback)
        => ValueOrNull(environment, key) ?? ValueOrNull(environment, alias) ?? fallback;

    private static string? ValueOrNull(IReadOnlyDictionary<string, string?>? environment, string key)
    {
        var found = environment is null
            ? Environment.GetEnvironmentVariable(key)
            : environment.TryGetValue(key, out var value) ? value : null;
        return string.IsNullOrWhiteSpace(found) ? null : found.Trim();
    }
}
