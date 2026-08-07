using System.Collections.Immutable;

namespace MultiFunPlayer.Server;

/// <summary>
/// Read-only catalog rooted at MFP_SCRIPTS_PATH. File names are used as opaque
/// ids; paths are resolved and checked again before every read.
/// </summary>
public sealed class ScriptCatalog
{
    private static readonly ImmutableHashSet<string> Extensions =
        [".funscript", ".json"];
    private readonly string _root;

    public ScriptCatalog(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration["MultiFunPlayer:ScriptsPath"]
            ?? Environment.GetEnvironmentVariable("MFP_SCRIPTS_PATH")
            ?? "scripts";
        _root = Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured));
        Directory.CreateDirectory(_root);
    }

    public IReadOnlyList<ScriptDescriptor> List()
    {
        if (!Directory.Exists(_root))
            return [];

        return Directory.EnumerateFiles(_root, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Extensions.Contains(Path.GetExtension(path)))
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Select(file => new ScriptDescriptor(file.Name, file.Name, file.Length, file.LastWriteTimeUtc))
            .ToArray();
    }

    public bool TryRead(string id, out ScriptContentResponse? response)
    {
        response = null;
        if (string.IsNullOrWhiteSpace(id) || id != Path.GetFileName(id))
            return false;

        var fullPath = Path.GetFullPath(Path.Combine(_root, id));
        if (!IsUnderRoot(fullPath) || !Extensions.Contains(Path.GetExtension(fullPath)) || !File.Exists(fullPath))
            return false;

        var file = new FileInfo(fullPath);
        response = new ScriptContentResponse(
            new ScriptDescriptor(file.Name, file.Name, file.Length, file.LastWriteTimeUtc),
            File.ReadAllText(fullPath));
        return true;
    }

    private bool IsUnderRoot(string path)
    {
        var root = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
