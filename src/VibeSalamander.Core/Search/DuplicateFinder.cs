// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Hashing;
using System.Runtime.InteropServices;
using VibeSalamander.Core.FileSystem;

namespace VibeSalamander.Core.Search;

/// <summary>What makes files duplicates of each other.</summary>
[Flags]
public enum DuplicateKind
{
    Name = 1,
    Size = 2,

    /// <summary>The same bytes (so also the same size).</summary>
    Content = 4,
}

/// <summary>What Find duplicates (Alt+Shift+F7) looks at.</summary>
public sealed class DuplicateCriteria
{
    public required IReadOnlyList<string> Directories { get; init; }
    public WildcardMask Mask { get; init; } = new("*.*");
    public DuplicateKind Kind { get; init; } = DuplicateKind.Content;
    public bool IncludeHidden { get; init; }
    public WildcardMask? SkipFolders { get; init; }

    /// <summary>Smaller files are left out (empty files are all "the same").</summary>
    public long MinSize { get; init; } = 1;

    /// <summary>How many files are read at the same time for the content.</summary>
    public int Parallelism { get; init; }
}

/// <summary>Files that are duplicates of each other.</summary>
/// <param name="Size">The files' size (when they have the same size).</param>
public sealed record DuplicateGroup(IReadOnlyList<FileEntry> Files, long Size)
{
    /// <summary>The space the copies take beyond one of them.</summary>
    public long Wasted => Files.Skip(1).Sum(f => f.Size);
}

/// <summary>How far the duplicate search is.</summary>
public readonly record struct DuplicateProgress(string Stage, long Done, long Total, string Current);

/// <summary>
/// Finds duplicate files by name, size or content. Content is compared in steps, so most files are read only a
/// little: same size first, then the first and last 16 KB, and only then the whole file (XXH128). Hard links to the
/// same file count once (they are one file, deleting one would free nothing).
/// </summary>
public static class DuplicateFinder
{
    private const int Edge = 16 * 1024;

    public static Task<IReadOnlyList<DuplicateGroup>> FindAsync(DuplicateCriteria criteria, IProgress<DuplicateProgress>? progress,
        CancellationToken ct) =>
        Task.Run<IReadOnlyList<DuplicateGroup>>(() =>
        {
            var walk = new WalkOptions { IncludeHidden = criteria.IncludeHidden, SkipFolders = criteria.SkipFolders };
            var files = new List<FileEntry>();
            long lastReport = 0;
            foreach (FileWalker.Found item in FileWalker.Walk(criteria.Directories, walk, dir =>
                     {
                         if (Environment.TickCount64 - lastReport > 150)
                         {
                             lastReport = Environment.TickCount64;
                             progress?.Report(new DuplicateProgress("Listing", files.Count, 0, dir));
                         }
                     }, ct))
            {
                FileEntry e = item.Entry;
                if (!e.IsDirectory && e.Size >= criteria.MinSize && criteria.Mask.IsMatch(e.Name))
                    files.Add(e);
            }
            return Group(files, criteria, progress, ct);
        }, ct);

    /// <summary>Groups listed files (testable without a walk).</summary>
    internal static IReadOnlyList<DuplicateGroup> Group(IReadOnlyList<FileEntry> files, DuplicateCriteria criteria,
        IProgress<DuplicateProgress>? progress, CancellationToken ct)
    {
        bool byName = criteria.Kind.HasFlag(DuplicateKind.Name);
        bool bySize = criteria.Kind.HasFlag(DuplicateKind.Size) || criteria.Kind.HasFlag(DuplicateKind.Content);
        IEnumerable<List<FileEntry>> groups = files
            .GroupBy(f => (Name: byName ? f.Name.ToUpperInvariant() : string.Empty, Size: bySize ? f.Size : -1))
            .Select(g => g.ToList())
            .Where(g => g.Count > 1);
        List<List<FileEntry>> candidates = groups.Select(WithoutHardLinks).Where(g => g.Count > 1).ToList();

        if (criteria.Kind.HasFlag(DuplicateKind.Content))
        {
            int parallel = Math.Clamp(criteria.Parallelism > 0 ? criteria.Parallelism : Environment.ProcessorCount, 1, 8);
            // the edges first (small files: all of it), then the whole file where the edges are the same
            candidates = Refine(candidates, f => f.Size <= 2 * Edge ? HashAll(f.FullPath, ct) : HashEdges(f.FullPath, f.Size), "Comparing",
                parallel, progress, ct);
            var big = candidates.Where(g => g[0].Size > 2 * Edge).ToList();
            var small = candidates.Where(g => g[0].Size <= 2 * Edge).ToList();
            small.AddRange(Refine(big, f => HashAll(f.FullPath, ct), "Reading", parallel, progress, ct));
            candidates = small;
        }

        return candidates
            .Select(g => new DuplicateGroup(g.OrderBy(f => f.FullPath, StringComparer.CurrentCultureIgnoreCase).ToList(), bySize ? g[0].Size : -1))
            .OrderByDescending(g => g.Wasted)
            .ThenBy(g => g.Files[0].Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Splits each group by a hash of its files; unreadable files drop out.</summary>
    private static List<List<FileEntry>> Refine(List<List<FileEntry>> groups, Func<FileEntry, string?> hash, string stage,
        int parallel, IProgress<DuplicateProgress>? progress, CancellationToken ct)
    {
        var all = groups.SelectMany((g, index) => g.Select(f => (Group: index, File: f))).ToList();
        var hashes = new string?[all.Count];
        long done = 0;
        Parallel.For(0, all.Count, new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = ct }, i =>
        {
            hashes[i] = hash(all[i].File);
            long d = Interlocked.Increment(ref done);
            if (d % 16 == 0)
                progress?.Report(new DuplicateProgress(stage, d, all.Count, all[i].File.FullPath));
        });
        return all.Select((x, i) => (x.Group, x.File, Hash: hashes[i]))
            .Where(x => x.Hash != null)
            .GroupBy(x => (x.Group, x.Hash))
            .Select(g => g.Select(x => x.File).ToList())
            .Where(g => g.Count > 1)
            .ToList();
    }

    private static string? HashEdges(string path, long size)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            var buffer = new byte[2 * Edge];
            int first = stream.ReadAtLeast(buffer.AsSpan(0, Edge), Edge, throwOnEndOfStream: false);
            stream.Seek(Math.Max(Edge, size - Edge), SeekOrigin.Begin);
            int last = stream.ReadAtLeast(buffer.AsSpan(Edge, Edge), Edge, throwOnEndOfStream: false);
            var hash = new XxHash128();
            hash.Append(buffer.AsSpan(0, first));
            hash.Append(buffer.AsSpan(Edge, last));
            return Convert.ToHexString(hash.GetCurrentHash());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? HashAll(string path, CancellationToken ct)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            var hash = new XxHash128();
            var buffer = new byte[1 << 20];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                hash.Append(buffer.AsSpan(0, read));
            }
            return Convert.ToHexString(hash.GetCurrentHash());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>One entry per file: names that are hard links to the same file are one file.</summary>
    private static List<FileEntry> WithoutHardLinks(List<FileEntry> group)
    {
        if (!OperatingSystem.IsWindows())
            return group;
        var seen = new HashSet<(uint, ulong)>();
        var result = new List<FileEntry>(group.Count);
        foreach (FileEntry f in group)
        {
            if (FileId(f.FullPath) is not { } id || seen.Add(id))
                result.Add(f);
        }
        return result;
    }

    /// <summary>The volume and the file's index on it (the same for every hard link), or null.</summary>
    internal static (uint Volume, ulong Index)? FileId(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(handle, out ByHandleFileInformation info))
                return null;
            return (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out ByHandleFileInformation information);
}
