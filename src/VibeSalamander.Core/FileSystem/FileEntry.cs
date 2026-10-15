// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Core.FileSystem;

/// <summary>
/// One item of a panel listing: a file, a directory, the ".." up-dir or a provider-specific item
/// (for example a drive in This PC). Immutable after the listing is created.
/// </summary>
public sealed class FileEntry
{
    public FileEntry(string name, string fullPath, bool isDirectory, long size, DateTime lastWrite,
                     FileAttributes attributes, bool isUpDir = false, object? tag = null)
    {
        Name = name;
        // the folder part is shared by all entries of a listing; the full path is put together when asked for
        int cut = fullPath.Length - name.Length;
        if (!isUpDir && name.Length > 0 && cut > 0 && fullPath.EndsWith(name, StringComparison.Ordinal))
            _directory = SharedDirectory(fullPath.AsSpan(0, cut));
        else
            _fullPath = fullPath;
        IsDirectory = isDirectory;
        Size = size;
        LastWrite = lastWrite;
        Attributes = attributes;
        IsUpDir = isUpDir;
        Tag = tag;

        // Salamander splits files into Name + Ext columns; directories keep the whole name.
        int dot = isDirectory || isUpDir ? -1 : name.LastIndexOf('.');
        if (dot > 0) // ".gitignore" has no extension in the Ext column
        {
            BaseName = name[..dot];
            Extension = name[(dot + 1)..];
        }
        else
        {
            BaseName = name;
            Extension = string.Empty;
        }
    }

    /// <summary>File name with extension ("report.txt").</summary>
    public string Name { get; }

    /// <summary>Name without extension for files, full name for directories ("report").</summary>
    public string BaseName { get; }

    /// <summary>Extension without the dot ("txt"), empty for directories.</summary>
    public string Extension { get; }

    public string FullPath => _fullPath ?? string.Concat(_directory, Name);

    private readonly string? _directory;
    private readonly string? _fullPath;

    // a listing is built on one thread, entry after entry: the same folder string is reused for all of them
    [ThreadStatic] private static string? t_lastDirectory;

    private static string SharedDirectory(ReadOnlySpan<char> directory)
    {
        string? last = t_lastDirectory;
        if (last != null && directory.SequenceEqual(last))
            return last;
        return t_lastDirectory = directory.ToString();
    }
    public bool IsDirectory { get; }
    public bool IsUpDir { get; }
    public long Size { get; }

    /// <summary>Last write time in local time.</summary>
    public DateTime LastWrite { get; }

    public FileAttributes Attributes { get; }

    /// <summary>Provider data (e.g. <see cref="DriveEntryInfo"/> for drives in This PC).</summary>
    public object? Tag { get; }

    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;
    public bool IsSystem => (Attributes & FileAttributes.System) != 0;

    public static FileEntry CreateUpDir(string parentPath) =>
        new("..", parentPath, isDirectory: true, 0, DateTime.MinValue, FileAttributes.Directory, isUpDir: true);

    public override string ToString() => Name;
}
