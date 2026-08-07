using MultiFunPlayer.Infrastructure.Configuration;
using MultiFunPlayer.Infrastructure.Storage;

namespace MultiFunPlayer.Server;

public sealed record ConfigurationMigrationStatus(
    string State,
    string? SourceFile,
    string TargetFile,
    int WarningCount,
    string? Error);

/// <summary>
/// Performs an explicit, one-time import of the legacy desktop JSON. The
/// source path is opt-in and the source file is never modified or deleted.
/// </summary>
public sealed class ConfigurationMigrationHostedService : IHostedService
{
    public const string LegacyPathEnvironmentVariable = "MFP_LEGACY_CONFIG_PATH";
    public const string TargetFile = "migrated-configuration.json";
    private const long MaximumSourceBytes = 4 * 1024 * 1024;

    private readonly StorageOptions _storage;
    private readonly ILogger<ConfigurationMigrationHostedService> _logger;

    public ConfigurationMigrationHostedService(StorageOptions storage,
        ILogger<ConfigurationMigrationHostedService> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    public ConfigurationMigrationStatus Status { get; private set; }
        = new("disabled", null, TargetFile, 0, null);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var rawPath = Environment.GetEnvironmentVariable(LegacyPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(rawPath))
            return;

        string sourcePath;
        string? sourceFile = null;
        try
        {
            sourcePath = Path.GetFullPath(rawPath.Trim());
            sourceFile = Path.GetFileName(sourcePath);
            if (!File.Exists(sourcePath))
            {
                Status = new("source-not-found", sourceFile, TargetFile, 0,
                    "The configured legacy configuration file does not exist.");
                return;
            }

            if (IsReparsePoint(sourcePath))
            {
                Status = new("failed", sourceFile, TargetFile, 0,
                    "Legacy configuration symlinks and reparse points are not accepted.");
                return;
            }

            var info = new FileInfo(sourcePath);
            if (info.Length > MaximumSourceBytes)
            {
                Status = new("failed", sourceFile, TargetFile, 0,
                    "The legacy configuration exceeds the 4 MiB import limit.");
                return;
            }

            var store = new RootedFileStore(_storage.ConfigRoot);
            var targetPath = store.Resolve(TargetFile);
            if (File.Exists(targetPath))
            {
                Status = new("already-present", sourceFile, TargetFile, 0, null);
                return;
            }

            var json = await File.ReadAllTextAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            var result = new ConfigurationMigrationService().Read(json);
            if (!result.Success || result.Configuration is null)
            {
                Status = new("failed", sourceFile, TargetFile, 0, result.Error);
                return;
            }

            await store.WriteJsonAsync(TargetFile, result.Configuration, cancellationToken)
                .ConfigureAwait(false);
            Status = new("migrated", sourceFile, TargetFile,
                result.Configuration.Warnings.Count, null);
            _logger.LogInformation("Imported legacy desktop configuration into {TargetFile} with {WarningCount} warnings.",
                TargetFile, result.Configuration.Warnings.Count);
        }
        catch (IOException exception)
        {
            Status = new("failed", sourceFile, TargetFile, 0, exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            Status = new("failed", sourceFile, TargetFile, 0, exception.Message);
        }
        catch (ArgumentException exception)
        {
            Status = new("failed", null, TargetFile, 0, exception.Message);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static bool IsReparsePoint(string path)
        => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
}
