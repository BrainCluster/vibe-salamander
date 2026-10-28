// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Core.Tools;

/// <summary>How an item differs between the two sides.</summary>
public enum DifferenceKind
{
    OnlyLeft,
    OnlyRight,
    LeftNewer,
    RightNewer,

    /// <summary>Different size, content or attributes, with no newer side.</summary>
    Different,

    /// <summary>A file on one side, a directory on the other.</summary>
    FileAndDirectory,
}

/// <summary>One difference found by the tree comparison.</summary>
/// <param name="RelativePath">The item's path below both compared folders ("docs\readme.md").</param>
/// <param name="IsDirectory">A directory (only on one side; directories on both sides are compared inside).</param>
public sealed record CompareDifference(string RelativePath, bool IsDirectory, DifferenceKind Kind,
    long LeftSize, long RightSize, DateTime? LeftTime, DateTime? RightTime)
{
    public string Name => Path.GetFileName(RelativePath);
    public string Folder => Path.GetDirectoryName(RelativePath) ?? string.Empty;
}

/// <summary>Which way synchronizing copies.</summary>
public enum SyncDirection
{
    LeftToRight,
    RightToLeft,

    /// <summary>Each side gets what it misses and the newer versions.</summary>
    Both,
}

public enum SyncOperation
{
    CopyToRight,
    CopyToLeft,
    DeleteRight,
    DeleteLeft,
}

/// <summary>One step of a synchronization.</summary>
public sealed record SyncAction(SyncOperation Operation, CompareDifference Difference);

/// <summary>The steps of a synchronization, and the differences it leaves because neither side is clearly right.</summary>
public sealed record SyncPlan(IReadOnlyList<SyncAction> Actions, IReadOnlyList<CompareDifference> Conflicts)
{
    public int Copies => Actions.Count(a => a.Operation is SyncOperation.CopyToLeft or SyncOperation.CopyToRight);
    public int Deletes => Actions.Count(a => a.Operation is SyncOperation.DeleteLeft or SyncOperation.DeleteRight);
}

/// <summary>
/// Compares two directory trees item by item (the list of differences behind Compare Directories) and plans how to
/// synchronize them.
/// </summary>
public static class DirectorySync
{
    /// <summary>
    /// The differences between two trees. With <see cref="CompareOptions.Subdirectories"/> the directories on both
    /// sides are compared inside too (links and junctions are not followed). <paramref name="progress"/> is told the
    /// relative folder being compared.
    /// </summary>
    public static Task<IReadOnlyList<CompareDifference>> CompareTreeAsync(string leftRoot, string rightRoot, CompareOptions options,
        IProgress<string>? progress, CancellationToken ct) =>
        Task.Run<IReadOnlyList<CompareDifference>>(() =>
        {
            var result = new List<CompareDifference>();
            Compare(leftRoot, rightRoot, string.Empty, options, result, progress, ct);
            return result;
        }, ct);

    private static void Compare(string leftRoot, string rightRoot, string relative, CompareOptions options,
        List<CompareDifference> result, IProgress<string>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        progress?.Report(relative);
        string leftDir = Path.Combine(leftRoot, relative), rightDir = Path.Combine(rightRoot, relative);
        IReadOnlyList<DirectoryComparer.Item> left = DirectoryComparer.List(leftDir);
        var right = DirectoryComparer.List(rightDir).GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var leftNames = new HashSet<string>(left.Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
        var common = new List<string>();

        foreach (DirectoryComparer.Item l in left.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            string path = Path.Combine(relative, l.Name);
            if (!right.TryGetValue(l.Name, out DirectoryComparer.Item r))
            {
                result.Add(new CompareDifference(path, l.IsDirectory, DifferenceKind.OnlyLeft, l.Size, 0, l.LastWrite, null));
                continue;
            }
            if (l.IsDirectory != r.IsDirectory)
            {
                result.Add(new CompareDifference(path, false, DifferenceKind.FileAndDirectory, l.Size, r.Size, l.LastWrite, r.LastWrite));
                continue;
            }
            if (l.IsDirectory)
            {
                if (options.Subdirectories && !DirectoryComparer.IsReparsePoint(Path.Combine(leftDir, l.Name)) &&
                    !DirectoryComparer.IsReparsePoint(Path.Combine(rightDir, r.Name)))
                    common.Add(l.Name);
                continue;
            }
            DifferenceKind? kind = DirectoryComparer.CompareFiles(Path.Combine(leftDir, l.Name), l, Path.Combine(rightDir, r.Name), r, options, ct) switch
            {
                DirectoryComparer.Difference.LeftNewer => DifferenceKind.LeftNewer,
                DirectoryComparer.Difference.RightNewer => DifferenceKind.RightNewer,
                // different size, content or attributes: the newer side, if the times say so
                DirectoryComparer.Difference.Both => options.SameTime(l.LastWrite, r.LastWrite) ? DifferenceKind.Different
                    : l.LastWrite > r.LastWrite ? DifferenceKind.LeftNewer : DifferenceKind.RightNewer,
                _ => null,
            };
            if (kind is { } k)
                result.Add(new CompareDifference(path, false, k, l.Size, r.Size, l.LastWrite, r.LastWrite));
        }
        foreach (DirectoryComparer.Item r in right.Values.Where(r => !leftNames.Contains(r.Name)).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
            result.Add(new CompareDifference(Path.Combine(relative, r.Name), r.IsDirectory, DifferenceKind.OnlyRight, 0, r.Size, null, r.LastWrite));
        foreach (string name in common)
            Compare(leftRoot, rightRoot, Path.Combine(relative, name), options, result, progress, ct);
    }

    /// <summary>
    /// What synchronizing does with the chosen differences. One way: the missing items and the newer (or different)
    /// versions are copied; with <paramref name="mirror"/> the target becomes the same as the source: newer items
    /// there are overwritten and items only there are deleted. Both ways: each side gets what it misses and the newer
    /// versions; items that differ without a newer side are conflicts and left as they are.
    /// </summary>
    public static SyncPlan Plan(IEnumerable<CompareDifference> differences, SyncDirection direction, bool mirror)
    {
        var actions = new List<SyncAction>();
        var conflicts = new List<CompareDifference>();
        foreach (CompareDifference d in differences)
        {
            SyncOperation? op = (direction, d.Kind) switch
            {
                (_, DifferenceKind.FileAndDirectory) => null,
                (SyncDirection.LeftToRight, DifferenceKind.OnlyLeft or DifferenceKind.LeftNewer or DifferenceKind.Different) => SyncOperation.CopyToRight,
                (SyncDirection.LeftToRight, DifferenceKind.RightNewer) when mirror => SyncOperation.CopyToRight,
                (SyncDirection.LeftToRight, DifferenceKind.OnlyRight) when mirror => SyncOperation.DeleteRight,
                (SyncDirection.RightToLeft, DifferenceKind.OnlyRight or DifferenceKind.RightNewer or DifferenceKind.Different) => SyncOperation.CopyToLeft,
                (SyncDirection.RightToLeft, DifferenceKind.LeftNewer) when mirror => SyncOperation.CopyToLeft,
                (SyncDirection.RightToLeft, DifferenceKind.OnlyLeft) when mirror => SyncOperation.DeleteLeft,
                (SyncDirection.Both, DifferenceKind.OnlyLeft or DifferenceKind.LeftNewer) => SyncOperation.CopyToRight,
                (SyncDirection.Both, DifferenceKind.OnlyRight or DifferenceKind.RightNewer) => SyncOperation.CopyToLeft,
                _ => (SyncOperation?)null,
            };
            if (op is { } o)
                actions.Add(new SyncAction(o, d));
            else if (d.Kind == DifferenceKind.FileAndDirectory || (direction == SyncDirection.Both && d.Kind == DifferenceKind.Different))
                conflicts.Add(d);
        }
        return new SyncPlan(actions, conflicts);
    }
}
