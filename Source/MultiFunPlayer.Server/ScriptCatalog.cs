using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace MultiFunPlayer.Server;

/// <summary>
/// A library of scripts rooted at one or more directories (for example the
/// fnOS scripts share plus any granted shared folders). Script ids are the
/// paths relative to their root (forward slashes), so files inside
/// subdirectories are addressable and searchable. Every read/write re-resolves
/// and re-checks the path against its root to stay traversal-safe.
/// </summary>
public sealed class ScriptCatalog
{
    private static readonly ImmutableHashSet<string> Extensions =
        [".funscript", ".json"];
    private const long MaximumScriptBytes = 8 * 1024 * 1024;
    private readonly ImmutableArray<string> _roots;

    /// <summary>The absolute resolved script roots in configuration order.</summary>
    public IReadOnlyList<string> Roots => _roots;

    public ScriptCatalog(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration["MultiFunPlayer:ScriptsPath"]
            ?? Environment.GetEnvironmentVariable("MFP_SCRIPTS_ROOT")
            ?? Environment.GetEnvironmentVariable("MFP_SCRIPTS_PATH")
            ?? "scripts";
        var configuredList = configuration["MultiFunPlayer:ScriptsPaths"]
            ?? Environment.GetEnvironmentVariable("MFP_SCRIPTS_PATHS");
        var roots = SplitPathList(configuredList);
        if (roots.Length == 0)
            roots = [configured];
        _roots = roots
            .Select(path => Path.GetFullPath(Path.IsPathRooted(path)
                ? path
                : Path.Combine(environment.ContentRootPath, path)))
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();
        foreach (var root in _roots)
        {
            Directory.CreateDirectory(root);
            EnsureNotReparse(root);
        }
    }

    public ScriptCatalog(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("A scripts root is required.", nameof(root));
        _roots = [Path.GetFullPath(root)];
        Directory.CreateDirectory(_roots[0]);
        EnsureNotReparse(_roots[0]);
    }

    /// <summary>Split a ";" (or ":") separated list of paths, ignoring empties.</summary>
    private static string[] SplitPathList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];
        return value
            .Split([';', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();
    }

    /// <summary>All scripts across every root and subdirectory, ordered by id.</summary>
    public IReadOnlyList<ScriptDescriptor> List()
    {
        var result = new List<ScriptDescriptor>();
        foreach (var root in _roots)
        {
            if (!Directory.Exists(root))
                continue;
            var rootPath = Path.GetFullPath(root);
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (!Extensions.Contains(Path.GetExtension(path)) || IsReparsePoint(path))
                    continue;
                var file = new FileInfo(path);
                var relative = Path.GetRelativePath(rootPath, path).Replace('\\', '/');
                result.Add(new ScriptDescriptor(relative, file.Name, file.Length, file.LastWriteTimeUtc));
            }
        }
        return result.OrderBy(descriptor => descriptor.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Scripts whose id or file name contains <paramref name="query"/> (ordinal-ignore-case).</summary>
    public IReadOnlyList<ScriptDescriptor> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return List();
        return List()
            .Where(descriptor => descriptor.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                || descriptor.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    /// <summary>Relative directory paths across every root (used by the script folder browser).</summary>
    public IReadOnlyList<string> ListFolders()
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in _roots)
        {
            if (!Directory.Exists(root))
                continue;
            var rootPath = Path.GetFullPath(root);
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                if (IsReparsePoint(directory))
                    continue;
                folders.Add(Path.GetRelativePath(rootPath, directory).Replace('\\', '/'));
            }
        }
        return folders.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public bool Contains(string id) => TryResolve(id, out _);

    public bool TryRead(string id, out ScriptContentResponse? response)
    {
        response = null;
        if (!TryResolve(id, out var fullPath))
            return false;

        var file = new FileInfo(fullPath);
        var relative = Path.GetRelativePath(FindRootFor(fullPath)!, fullPath).Replace('\\', '/');
        response = new ScriptContentResponse(
            new ScriptDescriptor(relative, file.Name, file.Length, file.LastWriteTimeUtc),
            File.ReadAllText(fullPath));
        return true;
    }

    /// <summary>Create a (possibly nested) folder under the primary writable root.</summary>
    public bool TryCreateFolder(string? relativePath, out string? folder, out string? error)
    {
        folder = null;
        error = null;
        if (!TryNormalizeRelative(relativePath, out var normalized))
        {
            error = "Folder path is invalid.";
            return false;
        }
        if (normalized.Length == 0)
        {
            error = "A folder path is required.";
            return false;
        }
        var root = _roots[0];
        var target = Path.GetFullPath(Path.Combine(root, normalized));
        if (!IsUnderRoot(root, target) || IsReparsePoint(target))
        {
            error = "The folder path is outside the configured root.";
            return false;
        }
        try
        {
            Directory.CreateDirectory(target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = $"Could not create folder: {exception.Message}";
            return false;
        }
        folder = normalized;
        return true;
    }

    /// <summary>Remove a folder (recursively) from any writable root.</summary>
    public bool TryDeleteFolder(string? relativePath, out string? error)
    {
        error = null;
        if (!TryNormalizeRelative(relativePath, out var normalized) || normalized.Length == 0)
        {
            error = "Folder path is invalid.";
            return false;
        }
        foreach (var root in _roots)
        {
            var target = Path.GetFullPath(Path.Combine(root, normalized));
            if (!IsUnderRoot(root, target) || IsReparsePoint(target))
                continue;
            if (!Directory.Exists(target))
            {
                error = "Folder not found.";
                return false;
            }
            try
            {
                Directory.Delete(target, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                error = $"Could not delete folder: {exception.Message}";
                return false;
            }
            return true;
        }
        error = "Folder path is outside the configured roots.";
        return false;
    }

    private bool TryResolve(string id, out string fullPath)
    {
        fullPath = string.Empty;
        if (!TryNormalizeRelative(id, out var normalized) || normalized.Length == 0)
            return false;
        if (!Extensions.Contains(Path.GetExtension(normalized)))
            return false;
        foreach (var root in _roots)
        {
            var candidate = Path.GetFullPath(Path.Combine(root, normalized));
            if (IsUnderRoot(root, candidate) && File.Exists(candidate) && !IsReparsePoint(candidate))
            {
                fullPath = candidate;
                return true;
            }
        }
        return false;
    }

    public bool TryWrite(string? id, string? content, bool overwrite,
        out ScriptDescriptor? descriptor, out string? error, out bool conflict)
    {
        descriptor = null;
        error = null;
        conflict = false;
        if (!TryNormalizeRelative(id, out var normalized) || normalized.Length == 0
            || !Extensions.Contains(Path.GetExtension(normalized)))
        {
            error = "id must be a relative path ending in .funscript or .json.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            error = "content is required.";
            return false;
        }

        if (Encoding.UTF8.GetByteCount(content) > MaximumScriptBytes)
        {
            error = "content exceeds the 8 MiB upload limit.";
            return false;
        }

        var root = FindRootForParent(normalized);
        if (root is null)
        {
            error = "The script path is outside the configured roots.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var fullPath = Path.GetFullPath(Path.Combine(root, normalized));
            if (IsReparsePoint(fullPath))
            {
                error = "The script path is a symbolic link or reparse point.";
                return false;
            }

            if (File.Exists(fullPath) && !overwrite)
            {
                conflict = true;
                return false;
            }

            var directory = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $".{Path.GetFileName(normalized)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, content, Encoding.UTF8);
                File.Move(temporary, fullPath, overwrite: true);
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

            var file = new FileInfo(fullPath);
            descriptor = new ScriptDescriptor(normalized, file.Name, file.Length, file.LastWriteTimeUtc);
            return true;
        }
        catch (JsonException)
        {
            error = "content must be valid JSON.";
            return false;
        }
    }

    private bool TryNormalizeRelative(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var candidate = value.Replace('\\', '/').Trim();
        if (candidate.StartsWith('/') || candidate.Contains(".."))
            return false;
        if (candidate.Contains('\0'))
            return false;
        normalized = candidate.Trim('/');
        return true;
    }

    private string? FindRootFor(string fullPath)
    {
        foreach (var root in _roots)
            if (IsUnderRoot(root, fullPath))
                return root;
        return null;
    }

    private string? FindRootForParent(string relativePath)
    {
        // Prefer a root that already contains the parent directory; otherwise the primary root.
        var parent = relativePath.Contains('/') ? relativePath[..relativePath.LastIndexOf('/')] : string.Empty;
        foreach (var root in _roots)
        {
            if (parent.Length == 0)
                return root;
            var candidate = Path.GetFullPath(Path.Combine(root, parent));
            if (Directory.Exists(candidate) && IsUnderRoot(root, candidate))
                return root;
        }
        return _roots.FirstOrDefault();
    }

    private static bool IsUnderRoot(string root, string path)
    {
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureNotReparse(string path)
    {
        if (IsReparsePoint(path))
            throw new UnauthorizedAccessException("The scripts root cannot be a symbolic link or reparse point.");
    }

    private static bool IsReparsePoint(string path)
        => (File.Exists(path) || Directory.Exists(path))
            && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
}
