namespace MultiFunPlayer.Server;

public sealed class RuntimeScriptMatcher
{
    private static readonly HashSet<string> CompoundExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".funscript", ".json", ".mp4", ".mkv", ".webm", ".avi", ".mov", ".wmv", ".m4v"
    };
    private readonly ScriptCatalog _catalog;

    public RuntimeScriptMatcher(ScriptCatalog catalog)
    {
        _catalog = catalog;
    }

    public string? Match(string mediaPath, bool fuzzy)
    {
        var mediaName = NormalizeName(mediaPath);
        if (mediaName.Length == 0)
            return null;

        var candidates = _catalog.List().Select(script => new
        {
            script.Id,
            Name = NormalizeName(script.Name)
        }).Where(candidate => candidate.Name.Length > 0).ToArray();
        var exact = candidates.FirstOrDefault(candidate => candidate.Name == mediaName);
        if (exact is not null || !fuzzy)
            return exact?.Id;

        var best = candidates.Select(candidate => new
        {
            candidate.Id,
            Score = Similarity(mediaName, candidate.Name)
        }).OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        return best is { Score: >= 0.45 } ? best.Id : null;
    }

    internal static string NormalizeName(string pathOrName)
    {
        var value = pathOrName.Replace('\\', '/');
        try { value = Uri.UnescapeDataString(value); }
        catch (UriFormatException) { }
        var queryIndex = value.IndexOfAny(['?', '#']);
        if (queryIndex >= 0)
            value = value[..queryIndex];
        value = value[(value.LastIndexOf('/') + 1)..];
        while (CompoundExtensions.Contains(Path.GetExtension(value)))
            value = Path.GetFileNameWithoutExtension(value);
        return string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    }

    private static double Similarity(string left, string right)
    {
        if (left == right)
            return 1;
        if (left.Contains(right, StringComparison.Ordinal)
            || right.Contains(left, StringComparison.Ordinal))
            return (double)Math.Min(left.Length, right.Length) / Math.Max(left.Length, right.Length);

        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            var current = new int[right.Length + 1];
            current[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var substitution = previous[rightIndex - 1]
                    + (left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1);
                current[rightIndex] = Math.Min(Math.Min(current[rightIndex - 1] + 1,
                    previous[rightIndex] + 1), substitution);
            }
            previous = current;
        }
        return 1d - (double)previous[^1] / Math.Max(left.Length, right.Length);
    }
}
