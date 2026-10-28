// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Core.Operations;

/// <summary>
/// Which queued transfers may start: at most the configured number at once, and (by default) only one per disk, so
/// two copies on the same disk don't make its head jump between them (both get slower than one after another),
/// while copies between different disks run side by side. The queue's order decides who goes first.
/// </summary>
public static class TransferScheduler
{
    /// <summary>
    /// The disks a transfer reads and writes: "C:\" and "D:\" for a copy from C: to D:, "\\server" for a network share,
    /// "ftp://host" for a server. The same drive letter is one disk (two partitions of one physical disk count as two).
    /// </summary>
    public static IReadOnlyList<string> Disks(IEnumerable<string> sources, string? target)
    {
        var disks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in sources.Take(64).Append(target ?? string.Empty))
            if (Disk(path) is { } disk)
                disks.Add(disk);
        return disks.ToList();
    }

    private static string? Disk(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        int scheme = path.IndexOf("://", StringComparison.Ordinal);
        if (scheme > 0)
        {
            int end = path.IndexOf('/', scheme + 3);
            return (end < 0 ? path : path[..end]).ToLowerInvariant();
        }
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            int end = path.IndexOf('\\', 2);
            return end < 0 ? path : path[..end];
        }
        if (path.Length >= 2 && path[1] == ':')
            return char.ToUpperInvariant(path[0]) + @":\";
        return null;
    }

    /// <summary>
    /// The transfers of <paramref name="waiting"/> (in queue order) that can start now, next to the ones running.
    /// </summary>
    public static IReadOnlyList<int> Startable(IReadOnlyList<IReadOnlyList<string>> waiting, IEnumerable<IReadOnlyList<string>> running,
        int maxParallel, bool onePerDisk)
    {
        var busy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int count = 0;
        foreach (IReadOnlyList<string> disks in running)
        {
            count++;
            foreach (string disk in disks)
                busy.Add(disk);
        }
        var start = new List<int>();
        for (int i = 0; i < waiting.Count && count < Math.Max(1, maxParallel); i++)
        {
            IReadOnlyList<string> disks = waiting[i];
            if (onePerDisk && disks.Any(busy.Contains))
                continue;
            start.Add(i);
            count++;
            foreach (string disk in disks)
                busy.Add(disk);
        }
        return start;
    }
}
