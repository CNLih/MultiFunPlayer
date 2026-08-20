using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace MultiFunPlayer.Server;

/// <summary>
/// Rooted catalog resolved from the configured scripts path or its persistent
/// environment aliases. File names are opaque ids; paths are resolved and
/// checked again before every read or write.
/// </summary>
public sealed class ScriptCatalog
{
    private static readonly ImmutableHashSet<string> Extensions =
        [".funscript", ".json"];
    private const long MaximumScriptBytes = 8 * 1024 * 1024;
    private readonly string _root;

    public ScriptCatalog(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration["MultiFunPlayer:ScriptsPath"]
            ?? Environment.GetEnvironmentVariable("MFP_SCRIPTS_ROOT")
            ?? Environment.GetEnvironmentVariable("MFP_SCRIPTS_PATH")
            ?? "scripts";
        _root = Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured));
        Directory.CreateDirectory(_root);
        EnsureNotReparse(_root);
    }

    public ScriptCatalog(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("A scripts root is required.", nameof(root));
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        EnsureNotReparse(_root);
    }

    public IReadOnlyList<ScriptDescriptor> List()
    {
        if (!Directory.Exists(_root))
            return [];

        return Directory.EnumerateFiles(_root, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Extensions.Contains(Path.GetExtension(path)) && !IsReparsePoint(path))
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Select(file => new ScriptDescriptor(file.Name, file.Name, file.Length, file.LastWriteTimeUtc))
            .ToArray();
    }

    public bool Contains(string id) => TryResolve(id, out _);

    public bool TryRead(string id, out ScriptContentResponse? response)
    {
        response = null;
        if (!TryResolve(id, out var fullPath))
            return false;

        var file = new FileInfo(fullPath);
        response = new ScriptContentResponse(
            new ScriptDescriptor(file.Name, file.Name, file.Length, file.LastWriteTimeUtc),
            File.ReadAllText(fullPath));
        return true;
    }

    private bool TryResolve(string id, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(id) || id != Path.GetFileName(id))
            return false;

        fullPath = Path.GetFullPath(Path.Combine(_root, id));
        return IsUnderRoot(fullPath) && Extensions.Contains(Path.GetExtension(fullPath))
            && File.Exists(fullPath) && !IsReparsePoint(fullPath);
    }

    public bool TryWrite(string? id, string? content, bool overwrite,
        out ScriptDescriptor? descriptor, out string? error, out bool conflict)
    {
        descriptor = null;
        error = null;
        conflict = false;
        if (string.IsNullOrWhiteSpace(id) || id != Path.GetFileName(id)
            || !Extensions.Contains(Path.GetExtension(id)))
        {
            error = "id must be a file name ending in .funscript or .json.";
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

        try
        {
            using var document = JsonDocument.Parse(content);
            var fullPath = Path.GetFullPath(Path.Combine(_root, id));
            if (!IsUnderRoot(fullPath) || IsReparsePoint(fullPath))
            {
                error = "The script path is outside the configured root.";
                return false;
            }

            if (File.Exists(fullPath) && !overwrite)
            {
                conflict = true;
                return false;
            }

            var temporary = Path.Combine(_root, $".{id}.{Guid.NewGuid():N}.tmp");
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
            descriptor = new ScriptDescriptor(file.Name, file.Name, file.Length, file.LastWriteTimeUtc);
            return true;
        }
        catch (JsonException)
        {
            error = "content must be valid JSON.";
            return false;
        }
    }

    private bool IsUnderRoot(string path)
    {
        var root = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
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
