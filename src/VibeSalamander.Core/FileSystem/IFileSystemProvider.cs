// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Core.FileSystem;

/// <summary>
/// Source of panel listings. Local disks, the This PC view and (later) archives, FTP and other
/// addon file systems implement this interface.
/// </summary>
public interface IFileSystemProvider
{
    /// <summary>Stable identifier ("local", "thispc", …).</summary>
    string Id { get; }

    /// <summary>True when the provider owns <paramref name="path"/>.</summary>
    bool CanHandle(string path);

    /// <summary>Lists the directory. Never throws for I/O problems; returns a listing with <see cref="DirectoryListing.Error"/>.</summary>
    Task<DirectoryListing> ListAsync(string path, ListingOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// Path of the parent ("up-dir" target) and the name to focus there, or null when there is no parent.
    /// </summary>
    ParentLocation? GetParent(string path);

    /// <summary>Text for the directory line and window title.</summary>
    string GetDisplayPath(string path);

    /// <summary>True for real disk directories (enables copy/move/delete, file watching, command line, …).</summary>
    bool IsLocalDisk { get; }
}

public sealed record ParentLocation(string Path, string? FocusName);

public sealed class ListingOptions
{
    /// <summary>Show items with the hidden or system attribute (Salamander: "Show hidden and system files").</summary>
    public bool ShowHidden { get; init; }
}

public sealed class DirectoryListing
{
    public DirectoryListing(string path, IReadOnlyList<FileEntry> entries, string? error = null)
    {
        Path = path;
        Entries = entries;
        Error = error;
    }

    public string Path { get; }

    /// <summary>All items including a leading ".." when the provider has a parent.</summary>
    public IReadOnlyList<FileEntry> Entries { get; }

    /// <summary>Free space of the volume, when known.</summary>
    public long? FreeSpace { get; init; }

    /// <summary>Number of items hidden by <see cref="ListingOptions"/>.</summary>
    public int HiddenCount { get; init; }

    public string? Error { get; }
}
