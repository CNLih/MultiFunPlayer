namespace MultiFunPlayer.Infrastructure.Storage;

/// <summary>Filesystem roots used by the headless host.</summary>
public sealed record StorageOptions
{
    public string ConfigRoot { get; init; } = "/config";
    public string ScriptsRoot { get; init; } = "/scripts";
    public string LogsRoot { get; init; } = "/logs";

    public static StorageOptions FromEnvironment(IReadOnlyDictionary<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(x => (string)x.Key, x => x.Value?.ToString(), StringComparer.OrdinalIgnoreCase);

        return new StorageOptions
        {
            ConfigRoot = ValueOrDefault(environment, "MFP_CONFIG_ROOT", "/config"),
            ScriptsRoot = ValueOrDefault(environment, "MFP_SCRIPTS_ROOT", "/scripts"),
            LogsRoot = ValueOrDefault(environment, "MFP_LOGS_ROOT", "/logs")
        };
    }

    private static string ValueOrDefault(IReadOnlyDictionary<string, string?> environment, string key, string fallback)
        => environment.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : fallback;
}
