using MultiFunPlayer.Application;
using MultiFunPlayer.Common;
using MultiFunPlayer.Script;

namespace MultiFunPlayer.Server;

public sealed class RuntimeScriptEvaluator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ScriptEngine> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ScriptCatalog _catalog;
    private readonly ILogger<RuntimeScriptEvaluator> _logger;

    public RuntimeScriptEvaluator(ScriptCatalog catalog, ILogger<RuntimeScriptEvaluator> logger)
    {
        _catalog = catalog;
        _logger = logger;
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
    }

    private bool TryLoad(string scriptId, out ScriptEngine? evaluator)
    {
        evaluator = null;
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
        if (_failed.Add(scriptId))
            _logger.LogWarning("Runtime script {ScriptId} is unavailable: {Reason}", scriptId, reason);
        return false;
    }
}
