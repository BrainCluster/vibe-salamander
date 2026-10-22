// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Enumeration;

namespace VibeSalamander.Plugins.DiskMap;

/// <summary>A file or directory of the map, with the size of everything in it.</summary>
internal sealed class Node
{
    private static readonly Node[] NoChildren = Array.Empty<Node>();

    public Node(string name, bool isDirectory, long size, Node? parent)
    {
        Name = name;
        IsDirectory = isDirectory;
        Size = size;
        Parent = parent;
        Kind = isDirectory ? FileKind.Other : FileKinds.Of(name);
    }

    /// <summary>The file or directory name; the full path for the scanned directory itself.</summary>
    public string Name { get; }

    public bool IsDirectory { get; }

    /// <summary>Bytes (a directory: everything in it).</summary>
    public long Size { get; set; }

    public Node? Parent { get; }

    public FileKind Kind { get; }

    /// <summary>Largest first.</summary>
    public Node[] Children { get; set; } = NoChildren;

    /// <summary>Files and directories inside (all levels).</summary>
    public long Files { get; set; }

    public long Directories { get; set; }

    /// <summary>The directory (or a directory in it) couldn't be read completely.</summary>
    public bool Incomplete { get; set; }

    public int Depth
    {
        get
        {
            int depth = 0;
            for (Node? n = Parent; n != null; n = n.Parent)
                depth++;
            return depth;
        }
    }

    public string FullPath => Parent == null ? Name : Path.Combine(Parent.FullPath, Name);

    /// <summary>True when this node is <paramref name="other"/> or inside it.</summary>
    public bool IsWithin(Node other)
    {
        for (Node? n = this; n != null; n = n.Parent)
            if (n == other)
                return true;
        return false;
    }

    public override string ToString() => Name;
}

/// <summary>Reads a directory tree with the sizes of everything in it (several directories at once).</summary>
internal sealed class Scanner
{
    private long _files;
    private long _bytes;
    private long _directories;

    public long FilesFound => Interlocked.Read(ref _files);

    public long BytesFound => Interlocked.Read(ref _bytes);

    public long DirectoriesFound => Interlocked.Read(ref _directories);

    /// <summary>The directory being read now (for the status line).</summary>
    public volatile string Current = string.Empty;

    private readonly record struct Entry(string Name, bool IsDirectory, long Length, FileAttributes Attributes);

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = 0, // hidden and system files take space too
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    public Node Scan(string path, CancellationToken cancellationToken)
    {
        var root = new Node(path, isDirectory: true, 0, null);
        ScanDirectory(root, path, 0, cancellationToken);
        return root;
    }

    private void ScanDirectory(Node node, string path, int level, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Current = path;
        var children = new List<Node>();
        var subdirectories = new List<(Node Node, string Path)>();
        try
        {
            var entries = new FileSystemEnumerable<Entry>(path,
                (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.IsDirectory, e.Length, e.Attributes), Options);
            foreach (Entry entry in entries)
            {
                if (entry.IsDirectory)
                {
                    var directory = new Node(entry.Name, isDirectory: true, 0, node);
                    children.Add(directory);
                    Interlocked.Increment(ref _directories);
                    // junctions and symbolic links point elsewhere (often back up the tree): not followed
                    if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
                        subdirectories.Add((directory, Path.Combine(path, entry.Name)));
                }
                else
                {
                    children.Add(new Node(entry.Name, isDirectory: false, entry.Length, node));
                    Interlocked.Increment(ref _files);
                    Interlocked.Add(ref _bytes, entry.Length);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            node.Incomplete = true;
        }

        // the first levels in parallel: separate branches of the tree are read at the same time
        if (level < 3 && subdirectories.Count > 1)
        {
            Parallel.ForEach(subdirectories, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 8 },
                d => ScanDirectory(d.Node, d.Path, level + 1, cancellationToken));
        }
        else
        {
            foreach ((Node child, string childPath) in subdirectories)
                ScanDirectory(child, childPath, level + 1, cancellationToken);
        }

        long size = 0, files = 0, directories = 0;
        bool incomplete = node.Incomplete;
        foreach (Node child in children)
        {
            size += child.Size;
            if (child.IsDirectory)
            {
                directories += 1 + child.Directories;
                files += child.Files;
                incomplete |= child.Incomplete;
            }
            else
            {
                files++;
            }
        }
        node.Size = size;
        node.Files = files;
        node.Directories = directories;
        node.Incomplete = incomplete;
        children.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        node.Children = children.ToArray();
    }
}
