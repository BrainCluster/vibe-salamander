using System.IO.Enumeration;

namespace VibeSalamander.Core.FileSystem;

/// <summary>
/// Disk directories ("C:\Windows", "\\server\share\dir"). Uses <see cref="FileSystemEnumerable{TResult}"/>
/// (NtQueryDirectoryFile) and builds entries straight from the native data, which keeps listing of
/// large directories fast.
/// </summary>
public sealed class LocalFileSystemProvider : IFileSystemProvider
{
    public string Id => "local";

    public bool IsLocalDisk => true;

    public bool CanHandle(string path) =>
        path.Length >= 2 && (path[1] == ':' || (path[0] == '\\' && path[1] == '\\'));

    public string GetDisplayPath(string path) => path;

    public ParentLocation? GetParent(string path)
    {
        string normalized = PathUtil.NormalizeDirectory(path);
        if (PathUtil.IsDriveRoot(normalized))
            return new ParentLocation(ThisPcProvider.RootPath, normalized[..2]); // "C:" is focused in This PC

        string trimmed = normalized.TrimEnd('\\');
        string? parent = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrEmpty(parent))
            return null; // UNC share root
        return new ParentLocation(PathUtil.NormalizeDirectory(parent), Path.GetFileName(trimmed));
    }

    public Task<DirectoryListing> ListAsync(string path, ListingOptions options, CancellationToken cancellationToken) =>
        Task.Run(() => List(path, options, cancellationToken), cancellationToken);

    private DirectoryListing List(string path, ListingOptions options, CancellationToken cancellationToken)
    {
        path = PathUtil.NormalizeDirectory(path);
        var entries = new List<FileEntry>(256);
        ParentLocation? parent = GetParent(path);
        if (parent != null)
            entries.Add(FileEntry.CreateUpDir(parent.Path));

        int hidden = 0;
        try
        {
            var enumOptions = new EnumerationOptions
            {
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
                BufferSize = 64 * 1024,
            };
            var enumerable = new FileSystemEnumerable<FileEntry>(path, ToEntry, enumOptions);
            foreach (FileEntry entry in enumerable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!options.ShowHidden && (entry.IsHidden || entry.IsSystem))
                {
                    hidden++;
                    continue;
                }
                entries.Add(entry);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new DirectoryListing(path, entries, ex.Message) { FreeSpace = GetFreeSpace(path), HiddenCount = hidden };
        }

        return new DirectoryListing(path, entries) { FreeSpace = GetFreeSpace(path), HiddenCount = hidden };
    }

    private static FileEntry ToEntry(ref FileSystemEntry e)
    {
        string name = e.FileName.ToString();
        return new FileEntry(
            name,
            e.ToFullPath(),
            e.IsDirectory,
            e.IsDirectory ? 0 : e.Length,
            e.LastWriteTimeUtc.LocalDateTime,
            e.Attributes);
    }

    public static long? GetFreeSpace(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
                return null;
            if (Interop.NativeMethods.GetDiskFreeSpaceEx(path, out ulong freeForUser, out _, out _))
                return (long)freeForUser;
        }
        catch
        {
            // free space is informational only
        }
        return null;
    }
}
