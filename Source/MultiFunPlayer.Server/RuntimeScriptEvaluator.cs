using MultiFunPlayer.Application;
using MultiFunPlayer.Common;
using MultiFunPlayer.Script;

namespace MultiFunPlayer.Server;

public sealed class RuntimeScriptEvaluator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ScriptEngine> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _failed = new(StringComparer.OrdinalIgnoreCase);
    private const int CacheCapacity = 128;
    private readonly ScriptCatalog _catalog;
    private readonly ILogger<RuntimeScriptEvaluator> _logger;

    public RuntimeScriptEvaluator(ScriptCatalog catalog, ILogger<RuntimeScriptEvaluator> logger)
    {
        _catalog = catalog;
        _logger = logger;
    }

    public event Action? Changed;

    public void RetainScripts(IReadOnlySet<string> scriptIds)
    {
        lock (_gate)
        {
            foreach (var key in _cache.Keys.Where(key => !scriptIds.Contains(key)).ToArray())
                _cache.Remove(key);
            foreach (var key in _failed.Keys.Where(key => !scriptIds.Contains(key)).ToArray())
                _failed.Remove(key);
        }
    }

    public bool TryEvaluate(string scriptId, TimeSpan position,
        IReadOnlyDictionary<string, AxisConfiguration> axisConfigurations, out OutputFrame frame)
    {
        ScriptEngine? evaluator;
        lock (_gate)
        {
            if (!_cache.TryGetValue(scriptId, out evaluator) && !TryLoad(scriptId, out evaluator))
            {
                frame = null!;
                return false;
            }
        }

        frame = evaluator!.Evaluate(position, axisConfigurations);
        return true;
    }

    public void Invalidate(string scriptId)
    {
        lock (_gate)
        {
            _cache.Remove(scriptId);
            _failed.Remove(scriptId);
        }
        Changed?.Invoke();
    }

    private bool TryLoad(string scriptId, out ScriptEngine? evaluator)
    {
        evaluator = null;
        if (_failed.TryGetValue(scriptId, out var retryAfter) && DateTimeOffset.UtcNow < retryAfter)
            return false;
        try
        {
            if (!_catalog.TryRead(scriptId, out var content))
                return LogFailure(scriptId, "The script is no longer in the catalog.");
            var result = FunscriptReader.Default.FromText(content!.Script.Name,
                $"catalog/{content.Script.Id}", content.Content);
            if (!result.IsSuccess)
                return LogFailure(scriptId, "The script could not be parsed as a Funscript.");

            evaluator = new ScriptEngine();
            if (result.Resources is { Count: > 0 })
                evaluator.Load(result.Resources);
            else if (result.Resource is not null)
                evaluator.Load(result.Resource, "L0");
            else
                return LogFailure(scriptId, "The script contains no actions.");

            if (_cache.Count >= CacheCapacity)
                _cache.Remove(_cache.Keys.First());
            _cache[scriptId] = evaluator;
            _failed.Remove(scriptId);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return LogFailure(scriptId, exception.Message);
        }
    }

    private bool LogFailure(string scriptId, string reason)
    {
        if (!_failed.ContainsKey(scriptId))
            _logger.LogWarning("Runtime script {ScriptId} is unavailable: {Reason}", scriptId, reason);
        if (_failed.Count >= CacheCapacity)
            _failed.Remove(_failed.Keys.First());
        _failed[scriptId] = DateTimeOffset.UtcNow.AddSeconds(5);
        return false;
    }
}
