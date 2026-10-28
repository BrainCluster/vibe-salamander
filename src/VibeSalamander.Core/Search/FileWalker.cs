// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Enumeration;
using VibeSalamander.Core.FileSystem;

namespace VibeSalamander.Core.Search;

/// <summary>Which directories a walk goes through, and which items it gives.</summary>
public sealed class WalkOptions
{
    /// <summary>How deep below each starting folder: 0 = only its own items, −1 = all the way down.</summary>
    public int MaxDepth { get; init; } = -1;

    /// <summary>Hidden and system items (and what is inside hidden directories).</summary>
    public bool IncludeHidden { get; init; } = true;

    /// <summary>Directory names that are not gone into ("node_modules;.git;bin;obj"); null = none.</summary>
    public WildcardMask? SkipFolders { get; init; }
}

/// <summary>
/// Goes through directory trees, in name order, without following links and junctions (no loops), skipping what
/// can't be read. Several starting folders are walked one after another; a folder inside another one given is
/// walked once.
/// </summary>
public static class FileWalker
{
    /// <summary>An item with how deep it is below its starting folder (0 = directly in it).</summary>
    public readonly record struct Found(FileEntry Entry, int Depth);

    /// <summary>The starting folders without the ones inside another (a folder given twice counts once).</summary>
    public static IReadOnlyList<string> Roots(IEnumerable<string> directories)
    {
        var roots = directories.Select(d => d.Trim()).Where(d => d.Length > 0)
            .Select(d => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return roots.Where(r => !roots.Any(other => !ReferenceEquals(other, r) && IsInside(r, other))).ToList();
    }

    private static bool IsInside(string path, string folder) =>
        path.Length > folder.Length && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) &&
        (path[folder.Length] == Path.DirectorySeparatorChar || folder.EndsWith(Path.DirectorySeparatorChar));

    /// <summary>
    /// Every item below the starting folders. <paramref name="directoryReached"/> is told each directory as the walk
    /// enters it.
    /// </summary>
    public static IEnumerable<Found> Walk(IEnumerable<string> directories, WalkOptions options, Action<string>? directoryReached,
        CancellationToken ct)
    {
        foreach (string root in Roots(directories))
        {
            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((root, 0));
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (dir, depth) = pending.Pop();
                directoryReached?.Invoke(dir);
                List<FileEntry> entries = List(dir);
                var subdirectories = new List<string>();
                foreach (FileEntry entry in entries)
                {
                    if (!options.IncludeHidden && (entry.IsHidden || entry.IsSystem))
                        continue;
                    if (entry.IsDirectory)
                    {
                        if (options.SkipFolders?.IsMatch(entry.Name) == true)
                            continue;
                        if ((options.MaxDepth < 0 || depth < options.MaxDepth) && (entry.Attributes & FileAttributes.ReparsePoint) == 0)
                            subdirectories.Add(entry.FullPath);
                    }
                    yield return new Found(entry, depth);
                }
                // pushed in reverse so directories are walked in name order
                subdirectories.Sort(StringComparer.CurrentCultureIgnoreCase);
                for (int i = subdirectories.Count - 1; i >= 0; i--)
                    pending.Push((subdirectories[i], depth + 1));
            }
        }
    }

    private static List<FileEntry> List(string dir)
    {
        try
        {
            var list = new FileSystemEnumerable<FileEntry>(dir, (ref FileSystemEntry e) => new FileEntry(
                    e.FileName.ToString(), e.ToFullPath(), e.IsDirectory, e.IsDirectory ? 0 : e.Length,
                    e.LastWriteTimeUtc.LocalDateTime, e.Attributes),
                new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }).ToList();
            list.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
            return list;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new List<FileEntry>();
        }
    }
}
