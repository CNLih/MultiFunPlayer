using System.Collections;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace MultiFunPlayer.Server;

public sealed record RuntimeLogEntry(
    DateTimeOffset TimestampUtc,
    string Level,
    string Category,
    int EventId,
    string Message,
    string? TraceId,
    IReadOnlyDictionary<string, object?> Properties);

public sealed record RuntimeLogsResponse(
    IReadOnlyList<RuntimeLogEntry> Items,
    bool Truncated,
    int Capacity);

/// <summary>
/// Bounded in-memory diagnostic log. Entries are sanitized at the storage
/// boundary, so API consumers and future sinks cannot accidentally expose a
/// credential that was present in a logger state or exception message.
/// </summary>
public sealed class RuntimeLogStore
{
    private readonly object _gate = new();
    private readonly Queue<RuntimeLogEntry> _entries = new();
    private readonly int _capacity;
    private readonly SecretRedactor _redactor;
    private long _dropped;

    public RuntimeLogStore(IConfiguration configuration)
    {
        _capacity = Math.Clamp(configuration.GetValue("MultiFunPlayer:LogCapacity", 500), 50, 10_000);
        _redactor = new SecretRedactor(Environment.GetEnvironmentVariable("MFP_AUTH_TOKEN"));
    }

    public int Capacity => _capacity;

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public long DroppedCount => Interlocked.Read(ref _dropped);

    public void Add(LogLevel level, string category, EventId eventId, string message,
        IReadOnlyDictionary<string, object?>? properties = null, Exception? exception = null)
    {
        var safeProperties = _redactor.RedactProperties(properties);
        var safeMessage = _redactor.Redact(message);
        if (exception is not null)
            safeMessage = $"{safeMessage} | { _redactor.Redact(exception.ToString())}";

        var entry = new RuntimeLogEntry(DateTimeOffset.UtcNow, level.ToString(), category,
            eventId.Id, safeMessage, System.Diagnostics.Activity.Current?.TraceId.ToString(), safeProperties);
        lock (_gate)
        {
            while (_entries.Count >= _capacity)
            {
                _entries.Dequeue();
                Interlocked.Increment(ref _dropped);
            }
            _entries.Enqueue(entry);
        }
    }

    public IReadOnlyList<RuntimeLogEntry> Read(int limit)
    {
        limit = Math.Clamp(limit, 1, _capacity);
        lock (_gate)
            return _entries.TakeLast(limit).ToArray();
    }

    private sealed class SecretRedactor
    {
        private static readonly Regex Bearer = new(
            @"(?i)(bearer\s+)[^\s,;]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex KeyValue = new(
            @"(?i)((?:token|secret|password|passwd|api[-_]?key|authorization|cookie|credential)\s*[:=]\s*)[^\s,;]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private readonly string? _configuredToken;

        public SecretRedactor(string? configuredToken) => _configuredToken = configuredToken;

        public string Redact(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return value ?? string.Empty;
            var safe = _configuredToken is { Length: > 0 }
                ? value.Replace(_configuredToken, "[REDACTED]", StringComparison.Ordinal)
                : value;
            safe = Bearer.Replace(safe, "$1[REDACTED]");
            return KeyValue.Replace(safe, "$1[REDACTED]");
        }

        public IReadOnlyDictionary<string, object?> RedactProperties(IReadOnlyDictionary<string, object?>? properties)
        {
            if (properties is null || properties.Count == 0)
                return new Dictionary<string, object?>();
            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in properties)
                result[key] = IsSensitiveKey(key) ? "[REDACTED]" : RedactValue(value);
            return result;
        }

        private object? RedactValue(object? value)
        {
            if (value is null or string or char)
                return Redact(value?.ToString());
            if (value is IDictionary dictionary)
            {
                var nested = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry item in dictionary)
                {
                    var key = item.Key?.ToString() ?? "value";
                    nested[key] = IsSensitiveKey(key) ? "[REDACTED]" : RedactValue(item.Value);
                }
                return nested;
            }
            return value is IFormattable formattable
                ? Redact(formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture))
                : Redact(value.ToString());
        }

        private static bool IsSensitiveKey(string key)
            => key.Contains("token", StringComparison.OrdinalIgnoreCase)
            || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || key.Contains("password", StringComparison.OrdinalIgnoreCase)
            || key.Contains("passwd", StringComparison.OrdinalIgnoreCase)
            || key.Contains("api-key", StringComparison.OrdinalIgnoreCase)
            || key.Contains("api_key", StringComparison.OrdinalIgnoreCase)
            || key.Equals("authorization", StringComparison.OrdinalIgnoreCase)
            || key.Equals("cookie", StringComparison.OrdinalIgnoreCase)
            || key.Contains("credential", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class RuntimeLogProvider : ILoggerProvider
{
    private readonly RuntimeLogStore _store;

    public RuntimeLogProvider(RuntimeLogStore store) => _store = store;

    public ILogger CreateLogger(string categoryName) => new RuntimeLogger(_store, categoryName);

    public void Dispose() { }

    private sealed class RuntimeLogger : ILogger
    {
        private readonly RuntimeLogStore _store;
        private readonly string _category;

        public RuntimeLogger(RuntimeLogStore store, string category)
        {
            _store = store;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var pair in values)
                    if (!pair.Key.Equals("{OriginalFormat}", StringComparison.Ordinal))
                        properties[pair.Key] = pair.Value;
            }
            _store.Add(logLevel, _category, eventId, formatter(state, exception), properties, exception);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
