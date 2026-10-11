// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.FileSystem;
using VibeSalamander.Plugins;

namespace VibeSalamander.Services;

/// <summary>Where a panel is inside an archive: "C:\Downloads\photos.zip" + "2024/summer".</summary>
public sealed record ArchiveLocation(string ArchivePath, string InnerPath, IArchiver Archiver)
{
    /// <summary>"2024/summer/a.jpg" for an item in this directory.</summary>
    public string ItemPath(string name) => InnerPath.Length == 0 ? name : InnerPath + "/" + name;
}

/// <summary>
/// Browsing archives like directories (Salamander style): "C:\Downloads\photos.zip\2024" lists the
/// "2024" directory inside photos.zip. The archivers of the plugins read the archive; the listing is
/// cached per archive until the archive file changes. Read-only: files are copied out (F5) or opened.
/// </summary>
public sealed class ArchiveFileSystemProvider : IFileSystemProvider
{
    private readonly PluginManager _plugins;
    private readonly object _lock = new();
    private readonly Dictionary<string, CachedArchive> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ArchiveFileSystemProvider(PluginManager plugins) => _plugins = plugins;

    public string Id => "archive";

    public bool IsLocalDisk => false;

    public bool CanHandle(string path) => Locate(path) != null;

    /// <summary>The archive file in the path and the directory inside it, or null when the path isn't in an archive.</summary>
    public ArchiveLocation? Locate(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 3 || !(path[1] == ':' || path.StartsWith(@"\\", StringComparison.Ordinal)))
            return null;
        string trimmed = path.TrimEnd('\\');
        // the first existing file in the path that an archiver browses ("C:\a.zip" in "C:\a.zip\b\c")
        for (int i = 3; i <= trimmed.Length; i++)
        {
            if (i < trimmed.Length && trimmed[i] != '\\')
                continue;
            string prefix = trimmed[..i];
            IArchiver? archiver = _plugins.FindBrowser(prefix);
            if (archiver == null)
                continue;
            bool isFile;
            try
            {
                isFile = File.Exists(prefix);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                isFile = false;
            }
            if (!isFile)
                continue;
            string inner = i < trimmed.Length ? trimmed[(i + 1)..].Replace('\\', '/').Trim('/') : string.Empty;
            return new ArchiveLocation(prefix, inner, archiver);
        }
        return null;
    }

    public string GetDisplayPath(string path) => path.TrimEnd('\\');

    public ParentLocation? GetParent(string path)
    {
        ArchiveLocation? location = Locate(path);
        if (location == null)
        {
            // the archive was deleted (or its plugin disabled) while a panel shows it: ".." leads to a directory that exists
            string? existing = path.TrimEnd('\\');
            while (!string.IsNullOrEmpty(existing = Path.GetDirectoryName(existing)))
                if (Directory.Exists(existing))
                    return new ParentLocation(PathUtil.NormalizeDirectory(existing), null);
            return null;
        }
        if (location.InnerPath.Length == 0)
        {
            // out of the archive: its directory, with the archive focused
            string? directory = Path.GetDirectoryName(location.ArchivePath);
            return directory == null ? null : new ParentLocation(PathUtil.NormalizeDirectory(directory), Path.GetFileName(location.ArchivePath));
        }
        int slash = location.InnerPath.LastIndexOf('/');
        string parentInner = slash < 0 ? string.Empty : location.InnerPath[..slash];
        string name = slash < 0 ? location.InnerPath : location.InnerPath[(slash + 1)..];
        return new ParentLocation(Combine(location.ArchivePath, parentInner), name);
    }

    /// <summary>"C:\a.zip" + "docs/img" → "C:\a.zip\docs\img".</summary>
    public static string Combine(string archivePath, string innerPath) =>
        innerPath.Length == 0 ? archivePath : archivePath + "\\" + innerPath.Replace('/', '\\');

    public async Task<DirectoryListing> ListAsync(string path, ListingOptions options, CancellationToken cancellationToken)
    {
        path = path.TrimEnd('\\');
        var entries = new List<FileEntry>();
        ParentLocation? parent = GetParent(path);
        if (parent != null)
            entries.Add(FileEntry.CreateUpDir(parent.Path));
        ArchiveLocation? location = Locate(path);
        if (location == null)
            return new DirectoryListing(path, entries, "This archive can no longer be opened.");

        Node root;
        try
        {
            root = await GetTreeAsync(location, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            string message = ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
                ? "The file names in this archive are encrypted. Unpack it with Alt+F6 to enter the password."
                : $"The archive can't be read: {ex.Message}";
            return new DirectoryListing(path, entries, message);
        }

        Node? directory = root.Find(location.InnerPath);
        if (directory == null || !directory.IsDirectory)
            return new DirectoryListing(path, entries, $"\"{location.InnerPath}\" isn't in this archive.");
        foreach (Node child in directory.Children.Values)
        {
            entries.Add(new FileEntry(child.Name, path + "\\" + child.Name, child.IsDirectory, child.Size, child.LastWrite,
                child.IsDirectory ? FileAttributes.Directory | FileAttributes.ReadOnly : FileAttributes.ReadOnly));
        }
        return new DirectoryListing(path, entries);
    }

    /// <summary>Subdirectory names of a directory inside an archive (address bar chevrons).</summary>
    public async Task<List<string>> ListDirectoriesAsync(string path)
    {
        ArchiveLocation? location = Locate(path);
        if (location == null)
            return new List<string>();
        try
        {
            Node root = await GetTreeAsync(location, CancellationToken.None);
            return root.Find(location.InnerPath)?.Children.Values.Where(c => c.IsDirectory).Select(c => c.Name)
                       .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList() ?? new List<string>();
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }

    /// <summary>All files under these items (for sizes and counts), from the cached listing.</summary>
    public async Task<(int Files, long Bytes)> MeasureAsync(ArchiveLocation location, IEnumerable<string> innerItems)
    {
        Node root = await GetTreeAsync(location, CancellationToken.None);
        int files = 0;
        long bytes = 0;
        void Walk(Node n)
        {
            if (!n.IsDirectory)
            {
                files++;
                bytes += n.Size;
                return;
            }
            foreach (Node c in n.Children.Values)
                Walk(c);
        }
        foreach (string item in innerItems)
            if (root.Find(item) is { } node)
                Walk(node);
        return (files, bytes);
    }

    private async Task<Node> GetTreeAsync(ArchiveLocation location, CancellationToken ct)
    {
        var info = new FileInfo(location.ArchivePath);
        DateTime stamp = info.LastWriteTimeUtc;
        long length = info.Length;
        lock (_lock)
        {
            if (_cache.TryGetValue(location.ArchivePath, out CachedArchive? cached) && cached.Stamp == stamp && cached.Length == length)
                return cached.Root;
        }
        IReadOnlyList<ArchiveItem> items = await location.Archiver.ListAsync(location.ArchivePath, ct);
        Node root = Node.Build(items, info.LastWriteTime);
        lock (_lock)
        {
            if (_cache.Count > 16)
                _cache.Clear(); // a few archives at a time is plenty
            _cache[location.ArchivePath] = new CachedArchive(stamp, length, root);
        }
        return root;
    }

    private sealed record CachedArchive(DateTime Stamp, long Length, Node Root);

    /// <summary>The archive's directory tree (directories that only appear in paths are added).</summary>
    private sealed class Node
    {
        public required string Name { get; init; }
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public DateTime LastWrite { get; set; }
        public Dictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Node? Find(string innerPath)
        {
            Node current = this;
            foreach (string part in innerPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!current.Children.TryGetValue(part, out Node? next))
                    return null;
                current = next;
            }
            return current;
        }

        public static Node Build(IReadOnlyList<ArchiveItem> items, DateTime archiveTime)
        {
            var root = new Node { Name = string.Empty, IsDirectory = true, LastWrite = archiveTime };
            foreach (ArchiveItem item in items)
            {
                string[] parts = item.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || parts.Contains(".."))
                    continue;
                Node current = root;
                for (int i = 0; i < parts.Length; i++)
                {
                    bool last = i == parts.Length - 1;
                    if (!current.Children.TryGetValue(parts[i], out Node? child))
                    {
                        child = new Node { Name = parts[i], IsDirectory = !last || item.IsDirectory, LastWrite = item.LastWrite };
                        current.Children[parts[i]] = child;
                    }
                    else if (!last)
                        child.IsDirectory = true; // "a" listed as a file before "a/b.txt"
                    if (last)
                    {
                        child.IsDirectory = item.IsDirectory || child.Children.Count > 0;
                        if (!item.IsDirectory)
                            child.Size = item.Size;
                        child.LastWrite = item.LastWrite;
                    }
                    current = child;
                }
            }
            return root;
        }
    }
}
