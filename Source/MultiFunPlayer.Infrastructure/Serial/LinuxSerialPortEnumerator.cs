namespace MultiFunPlayer.Infrastructure.Serial;

public sealed record SerialPortDescriptor(string StableId, string DevicePath, string? LinkTarget);

public interface ISerialPortEnumerator
{
    IReadOnlyList<SerialPortDescriptor> Enumerate(CancellationToken cancellationToken = default);
}

/// Enumerates stable Linux udev names without depending on WMI or registry APIs.
public sealed class LinuxSerialPortEnumerator : ISerialPortEnumerator
{
    private readonly string _byIdRoot;

    public LinuxSerialPortEnumerator(string byIdRoot = "/dev/serial/by-id")
    {
        _byIdRoot = Path.GetFullPath(byIdRoot);
    }

    public IReadOnlyList<SerialPortDescriptor> Enumerate(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_byIdRoot))
            return [];

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(_byIdRoot);
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }

        var ports = new List<SerialPortDescriptor>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
                    continue;

                var info = new FileInfo(entry);
                if (string.IsNullOrWhiteSpace(info.LinkTarget))
                    continue;
                ports.Add(new SerialPortDescriptor(
                    Path.GetFileName(entry),
                    entry,
                    info.LinkTarget));
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return ports.OrderBy(port => port.StableId, StringComparer.Ordinal).ToArray();
    }
}
