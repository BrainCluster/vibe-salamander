// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Core.Tools;

/// <summary>What Compare Directories (Ctrl+F10) looks at.</summary>
public sealed class CompareOptions
{
    /// <summary>Mark the newer of two files with the same name.</summary>
    public bool ByDate { get; set; } = true;

    /// <summary>Mark both files when their sizes differ.</summary>
    public bool BySize { get; set; } = true;

    /// <summary>Mark both files when their contents differ (reads the files; slower).</summary>
    public bool ByContent { get; set; }

    /// <summary>Mark both files when their Read-only, Hidden or System attributes differ.</summary>
    public bool ByAttributes { get; set; }

    /// <summary>Also compare what is inside directories present on both sides (marks the directory when anything differs).</summary>
    public bool Subdirectories { get; set; }

    /// <summary>Times that differ by at most this many seconds are equal (FAT stores 2 s steps, copies may round).</summary>
    public int ToleranceSeconds { get; set; } = 2;

    /// <summary>Differences of exactly one or two hours (± the tolerance) count as equal (daylight saving time, FAT vs NTFS).</summary>
    public bool IgnoreDstHours { get; set; }

    /// <summary>True when two modification times are the same for the comparison.</summary>
    public bool SameTime(DateTime a, DateTime b)
    {
        double seconds = Math.Abs((a - b).TotalSeconds);
        double tolerance = Math.Max(0, ToleranceSeconds);
        if (seconds <= tolerance)
            return true;
        return IgnoreDstHours && (Math.Abs(seconds - 3600) <= tolerance || Math.Abs(seconds - 7200) <= tolerance);
    }
}

/// <summary>Result of a comparison: the names to mark in each panel.</summary>
public sealed class CompareResult
{
    public HashSet<string> Left { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Right { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Identical => Left.Count == 0 && Right.Count == 0;
}

/// <summary>
/// Salamander's Compare Directories: in each panel, marks the items that are missing on the other side
/// or differ (the newer one by date; both by size or content).
/// </summary>
public static class DirectoryComparer
{
    public readonly record struct Item(string Name, bool IsDirectory, long Size, DateTime LastWrite, FileAttributes Attributes = 0);

    /// <summary>The attributes compared by <see cref="CompareOptions.ByAttributes"/>.</summary>
    public const FileAttributes ComparedAttributes = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System;

    public static Task<CompareResult> CompareAsync(string leftDir, IReadOnlyList<Item> left, string rightDir,
        IReadOnlyList<Item> right, CompareOptions options, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var result = new CompareResult();
            var rightByName = right.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                                   .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var leftNames = new HashSet<string>(left.Select(i => i.Name), StringComparer.OrdinalIgnoreCase);

            foreach (Item l in left)
            {
                ct.ThrowIfCancellationRequested();
                if (!rightByName.TryGetValue(l.Name, out Item r) || r.IsDirectory != l.IsDirectory)
                {
                    result.Left.Add(l.Name); // only on the left
                    if (rightByName.ContainsKey(l.Name))
                        result.Right.Add(r.Name); // a file vs. a directory: mark both
                    continue;
                }
                if (l.IsDirectory)
                {
                    if (options.Subdirectories &&
                        !TreesEqual(Path.Combine(leftDir, l.Name), Path.Combine(rightDir, r.Name), options, ct))
                    {
                        result.Left.Add(l.Name);
                        result.Right.Add(r.Name);
                    }
                    continue;
                }
                switch (CompareFiles(Path.Combine(leftDir, l.Name), l, Path.Combine(rightDir, r.Name), r, options, ct))
                {
                    case Difference.LeftNewer: result.Left.Add(l.Name); break;
                    case Difference.RightNewer: result.Right.Add(r.Name); break;
                    case Difference.Both: result.Left.Add(l.Name); result.Right.Add(r.Name); break;
                }
            }
            foreach (Item r in right)
                if (!leftNames.Contains(r.Name))
                    result.Right.Add(r.Name); // only on the right
            return result;
        }, ct);

    internal enum Difference { None, LeftNewer, RightNewer, Both }

    internal static Difference CompareFiles(string leftPath, Item l, string rightPath, Item r, CompareOptions options, CancellationToken ct)
    {
        bool sizeDiffers = l.Size != r.Size;
        if (options.BySize && sizeDiffers)
            return Difference.Both;
        if (options.ByAttributes && (l.Attributes & ComparedAttributes) != (r.Attributes & ComparedAttributes))
            return Difference.Both;
        if (options.ByDate && !options.SameTime(l.LastWrite, r.LastWrite))
            return l.LastWrite > r.LastWrite ? Difference.LeftNewer : Difference.RightNewer;
        if (options.ByContent && (sizeDiffers || !ContentEqual(leftPath, rightPath, ct)))
            return Difference.Both;
        return Difference.None;
    }

    /// <summary>Byte-by-byte comparison; unreadable files count as different.</summary>
    public static bool ContentEqual(string a, string b, CancellationToken ct = default)
    {
        try
        {
            using var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            using var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            if (fa.Length != fb.Length)
                return false;
            var ba = new byte[1 << 16];
            var bb = new byte[1 << 16];
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int na = fa.ReadAtLeast(ba, ba.Length, throwOnEndOfStream: false);
                int nb = fb.ReadAtLeast(bb, bb.Length, throwOnEndOfStream: false);
                if (na != nb || !ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb)))
                    return false;
                if (na == 0)
                    return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static IReadOnlyList<Item> List(string dir)
    {
        try
        {
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
            return new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options)
                .Select(i => new Item(i.Name, i is DirectoryInfo, i is FileInfo f ? f.Length : 0, i.LastWriteTime, i.Attributes))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<Item>();
        }
    }

    internal static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // unreadable: don't descend
        }
    }

    /// <summary>True when both trees have the same names and equal files (by the chosen criteria).</summary>
    private static bool TreesEqual(string leftDir, string rightDir, CompareOptions options, CancellationToken ct)
    {
        IReadOnlyList<Item> left = List(leftDir);
        var right = List(rightDir).ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);
        if (left.Count != right.Count)
            return false;
        foreach (Item l in left)
        {
            ct.ThrowIfCancellationRequested();
            if (!right.TryGetValue(l.Name, out Item r) || r.IsDirectory != l.IsDirectory)
                return false;
            if (l.IsDirectory)
            {
                // don't follow junctions/symlinks (endless loops)
                if (IsReparsePoint(Path.Combine(leftDir, l.Name)))
                    continue;
                if (!TreesEqual(Path.Combine(leftDir, l.Name), Path.Combine(rightDir, r.Name), options, ct))
                    return false;
            }
            else if (CompareFiles(Path.Combine(leftDir, l.Name), l, Path.Combine(rightDir, r.Name), r, options, ct) != Difference.None)
            {
                return false;
            }
        }
        return true;
    }
}
