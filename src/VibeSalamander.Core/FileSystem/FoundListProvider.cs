// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;

namespace VibeSalamander.Core.FileSystem;

/// <summary>
/// "Show in panel" of Find: a panel lists the found items (with their folders below the searched one in the names),
/// and copy, move, delete, view and edit work on them as on any files. ".." goes to the searched folder. Items that
/// are gone since are left out when the list is shown again.
/// </summary>
public sealed class FoundListProvider : IFileSystemProvider
{
    public const string Prefix = "found:";

    private sealed record FoundList(string Title, string Root, IReadOnlyList<FileEntry> Entries);

    private static readonly ConcurrentDictionary<string, FoundList> Lists = new(StringComparer.OrdinalIgnoreCase);
    private static int _next;

    public string Id => "found";

    /// <summary>Not a folder on disk: no watching, no command line here (the items are on disk, though).</summary>
    public bool IsLocalDisk => false;

    public static bool IsFoundList(string path) => path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public bool CanHandle(string path) => IsFoundList(path);

    /// <summary>
    /// Keeps a list of found items and gives the panel path that shows it. The names become the paths below
    /// <paramref name="root"/> ("docs\readme.md").
    /// </summary>
    public static string Publish(string title, string root, IEnumerable<FileEntry> found)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(root);
        var entries = found.Select(e =>
        {
            string relative = e.FullPath.StartsWith(trimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? e.FullPath[(trimmed.Length + 1)..]
                : e.FullPath;
            return new FileEntry(relative, e.FullPath, e.IsDirectory, e.Size, e.LastWrite, e.Attributes);
        }).ToList();
        string path = Prefix + Interlocked.Increment(ref _next);
        Lists[path] = new FoundList(title, trimmed, entries);
        return path;
    }

    public Task<DirectoryListing> ListAsync(string path, ListingOptions options, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            if (!Lists.TryGetValue(path, out FoundList? list))
                return new DirectoryListing(path, Array.Empty<FileEntry>(), "The list of found items is no longer there.");
            var entries = new List<FileEntry> { FileEntry.CreateUpDir(list.Root) };
            foreach (FileEntry e in list.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Exists(e.FullPath))
                    continue;
                if (!options.ShowHidden && (e.IsHidden || e.IsSystem))
                    continue;
                entries.Add(e);
            }
            return new DirectoryListing(path, entries);
        }, cancellationToken);

    /// <summary>On disk still (an item inside a ZIP: while the ZIP is).</summary>
    private static bool Exists(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
            return true;
        for (string? parent = Path.GetDirectoryName(path); parent != null; parent = Path.GetDirectoryName(parent))
            if (File.Exists(parent))
                return true; // the archive the item is in
        return false;
    }

    public ParentLocation? GetParent(string path) =>
        Lists.TryGetValue(path, out FoundList? list) ? new ParentLocation(list.Root, null) : null;

    public string GetDisplayPath(string path) =>
        Lists.TryGetValue(path, out FoundList? list) ? list.Title : path;
}
