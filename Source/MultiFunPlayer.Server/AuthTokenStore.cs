using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MultiFunPlayer.Infrastructure.Storage;

namespace MultiFunPlayer.Server;

public enum PairingResult
{
    Paired,
    PairingUnavailable,
    AlreadyConfigured,
    InvalidPairingToken,
    InvalidAuthToken,
}

/// <summary>
/// Resolves the static environment token or a one-time bootstrap pairing into
/// a persisted hash. The server never writes a bearer token to disk.
/// </summary>
public sealed class AuthTokenStore
{
    private const int MinimumAuthTokenLength = 16;
    private const int MinimumPairingTokenLength = 8;
    private readonly object _gate = new();
    private readonly string _path;
    private string? _persistedHash;
    private bool _loadFailed;
    private bool _storageUnavailable;

    public AuthTokenStore(StorageOptions storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        var root = Path.GetFullPath(storage.ConfigRoot);
        _path = Path.Combine(root, "auth-token.json");
        try
        {
            Directory.CreateDirectory(root);
            LoadPersistedHash();
        }
        catch (IOException) { _storageUnavailable = true; }
        catch (UnauthorizedAccessException) { _storageUnavailable = true; }
    }

    public bool IsConfigured
    {
        get
        {
            lock (_gate)
                return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MFP_AUTH_TOKEN"))
                    || _persistedHash is not null || _loadFailed;
        }
    }

    public bool Validate(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return false;
        var configured = Environment.GetEnvironmentVariable("MFP_AUTH_TOKEN");
        if (!string.IsNullOrEmpty(configured))
            return FixedEquals(token, configured);

        lock (_gate)
            return _persistedHash is not null && FixedEquals(Hash(token), _persistedHash);
    }

    public PairingResult TryPair(string? pairingToken, string? authToken)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MFP_PAIRING_TOKEN")))
            return PairingResult.PairingUnavailable;
        if (string.IsNullOrWhiteSpace(authToken) || authToken.Length < MinimumAuthTokenLength)
            return PairingResult.InvalidAuthToken;

        var configuredPairing = Environment.GetEnvironmentVariable("MFP_PAIRING_TOKEN")!;
        if (string.IsNullOrWhiteSpace(pairingToken) || !FixedEquals(pairingToken, configuredPairing))
            return PairingResult.InvalidPairingToken;

        lock (_gate)
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MFP_AUTH_TOKEN"))
                || _persistedHash is not null || _loadFailed)
                return PairingResult.AlreadyConfigured;
            if (_storageUnavailable)
                return PairingResult.PairingUnavailable;

            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var document = JsonSerializer.Serialize(new PersistedToken(1, Hash(authToken)), JsonOptions);
                File.WriteAllText(temporary, document, Encoding.UTF8);
                File.Move(temporary, _path, overwrite: true);
                _persistedHash = Hash(authToken);
                return PairingResult.Paired;
            }
            catch (IOException)
            {
                return PairingResult.PairingUnavailable;
            }
            catch (UnauthorizedAccessException)
            {
                return PairingResult.PairingUnavailable;
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private void LoadPersistedHash()
    {
        if (!File.Exists(_path))
            return;
        try
        {
            var persisted = JsonSerializer.Deserialize<PersistedToken>(File.ReadAllText(_path), JsonOptions);
            if (persisted?.SchemaVersion == 1 && !string.IsNullOrWhiteSpace(persisted.TokenHash))
                _persistedHash = persisted.TokenHash;
            else
                _loadFailed = true;
        }
        catch (IOException) { _loadFailed = true; }
        catch (UnauthorizedAccessException) { _loadFailed = true; }
        catch (JsonException) { _loadFailed = true; }
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool FixedEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private sealed record PersistedToken(int SchemaVersion, string TokenHash);
}
