// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.Localization;
using System.IO.Compression;
using System.Threading.Channels;
using VibeSalamander.Core.FileSystem;

namespace VibeSalamander.Core.Search;

/// <summary>What Find (Alt+F7) looks for.</summary>
public sealed class SearchCriteria
{
    /// <summary>Where to look: one or more folders (a folder inside another one given is searched once).</summary>
    public required IReadOnlyList<string> Directories { get; init; }

    public WildcardMask Mask { get; init; } = new("*.*");

    /// <summary>How deep: 0 = only the folders themselves, −1 = all subdirectories.</summary>
    public int MaxDepth { get; init; } = -1;

    public bool IncludeDirectories { get; init; } = true;
    public bool IncludeHidden { get; init; } = true;

    /// <summary>Directory names not gone into ("node_modules;.git"); null = none.</summary>
    public WildcardMask? SkipFolders { get; init; }

    /// <summary>The text rows; empty = names only.</summary>
    public IReadOnlyList<TextCondition> Texts { get; init; } = Array.Empty<TextCondition>();

    /// <summary>Every text row must hold (else any one).</summary>
    public bool AllTexts { get; init; } = true;

    public bool MatchCase { get; init; }
    public bool WholeWord { get; init; }

    public long? MinSize { get; init; }
    public long? MaxSize { get; init; }
    public DateTime? ModifiedFrom { get; init; }

    /// <summary>The last moment that counts (inclusive).</summary>
    public DateTime? ModifiedTo { get; init; }

    /// <summary>Attributes every result has (Read-only, Hidden, Archive, Compressed...).</summary>
    public FileAttributes RequiredAttributes { get; init; }

    /// <summary>Attributes no result has.</summary>
    public FileAttributes ExcludedAttributes { get; init; }

    /// <summary>Look inside ZIP files (names and content of what is in them).</summary>
    public bool InsideArchives { get; init; }

    /// <summary>How many files are read at the same time for the text rows (0 = by the number of processors).</summary>
    public int Parallelism { get; init; }

    /// <summary>Why the criteria can't be used, or null.</summary>
    public string? Problem()
    {
        if (Directories.Count == 0 || Directories.All(string.IsNullOrWhiteSpace))
            return L.T("Choose where to look.");
        if (MinSize is long min && MaxSize is long max && min > max)
            return L.T("The smallest size is larger than the largest.");
        if (ModifiedFrom is DateTime from && ModifiedTo is DateTime to && from > to)
            return L.T("The first date is after the last one.");
        return ContentMatcher.Problem(Texts);
    }

    internal bool HasSizeFilter => MinSize != null || MaxSize != null;
}

/// <summary>A found item: a file or directory, or something inside a ZIP file.</summary>
/// <param name="Entry">For an item in an archive: its name, "archive.zip\inner\path", size and time.</param>
/// <param name="Archive">The ZIP file the item is in, or null.</param>
/// <param name="Match">Where the content matched (null when only names were searched).</param>
public sealed record SearchHit(FileEntry Entry, string? Archive, ContentMatch? Match);

/// <summary>How far the search is.</summary>
public readonly record struct SearchProgress(string Directory, long FilesChecked, long BytesRead);

/// <summary>What a finished search found.</summary>
public readonly record struct SearchSummary(int Found, long FilesChecked, long BytesRead);

/// <summary>
/// Find Files and Directories (Alt+F7): names by masks, size, date, attributes and depth, and text rows inside the
/// files, read by several workers at once. Results are reported as they are found, in no particular order.
/// </summary>
public static class FileSearcher
{
    private static readonly string[] ArchiveExtensions = { ".zip", ".jar" };

    private sealed class Counters
    {
        public int Found;
        public long Checked;
        public long Bytes;
        public long LastReport;
        public string Current = string.Empty;
    }

    public static Task<SearchSummary> SearchAsync(SearchCriteria criteria, Action<SearchHit> onFound,
        IProgress<SearchProgress>? progress, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var matcher = new ContentMatcher(criteria.Texts, criteria.AllTexts, criteria.MatchCase, criteria.WholeWord);
            var counters = new Counters();

            void Report(bool force = false)
            {
                long now = Environment.TickCount64;
                if (progress == null || (!force && now - Interlocked.Read(ref counters.LastReport) < 150))
                    return;
                Interlocked.Exchange(ref counters.LastReport, now);
                progress.Report(new SearchProgress(Volatile.Read(ref counters.Current), Interlocked.Read(ref counters.Checked),
                    Interlocked.Read(ref counters.Bytes)));
            }

            void Hit(SearchHit hit)
            {
                Interlocked.Increment(ref counters.Found);
                onFound(hit);
            }

            var walk = new WalkOptions
            {
                MaxDepth = criteria.MaxDepth,
                IncludeHidden = criteria.IncludeHidden,
                SkipFolders = criteria.SkipFolders,
            };
            IEnumerable<FileWalker.Found> items = FileWalker.Walk(criteria.Directories, walk, dir =>
            {
                Volatile.Write(ref counters.Current, dir);
                Report();
            }, ct);

            // the files whose content is read go to the workers; names alone are answered right away
            int workers = matcher.IsEmpty && !criteria.InsideArchives ? 0
                : Math.Clamp(criteria.Parallelism > 0 ? criteria.Parallelism : Environment.ProcessorCount, 1, 16);
            Channel<FileEntry>? queue = workers > 0
                ? Channel.CreateBounded<FileEntry>(new BoundedChannelOptions(256) { SingleWriter = true })
                : null;
            var tasks = new List<Task>();
            for (int w = 0; w < workers; w++)
                tasks.Add(Task.Run(() => WorkAsync(queue!.Reader, criteria, matcher, counters, Hit, Report, ct), ct));

            try
            {
                foreach (FileWalker.Found item in items)
                {
                    FileEntry entry = item.Entry;
                    if (entry.IsDirectory)
                    {
                        if (criteria.IncludeDirectories && matcher.IsEmpty && !criteria.HasSizeFilter && Fits(entry, criteria))
                            Hit(new SearchHit(entry, null, null));
                        continue;
                    }
                    bool fits = Fits(entry, criteria);
                    bool archive = criteria.InsideArchives && IsArchive(entry.Name);
                    if (!fits && !archive)
                        continue;
                    if (queue != null)
                        await queue.Writer.WriteAsync(entry, ct);
                    else
                        Hit(new SearchHit(entry, null, null));
                }
            }
            finally
            {
                queue?.Writer.TryComplete();
            }
            await Task.WhenAll(tasks);
            Volatile.Write(ref counters.Current, string.Empty);
            Report(force: true);
            return new SearchSummary(counters.Found, counters.Checked, counters.Bytes);
        }, ct);

    private static async Task WorkAsync(ChannelReader<FileEntry> reader, SearchCriteria criteria, ContentMatcher matcher,
        Counters counters, Action<SearchHit> hit, Action<bool> report, CancellationToken ct)
    {
        await foreach (FileEntry file in reader.ReadAllAsync(ct))
        {
            if (criteria.InsideArchives && IsArchive(file.Name))
            {
                SearchArchive(file, criteria, matcher, counters, hit, ct);
                // the archive itself is a file too
                if (!Fits(file, criteria))
                    continue;
            }
            if (matcher.IsEmpty)
            {
                hit(new SearchHit(file, null, null));
                continue;
            }
            Interlocked.Increment(ref counters.Checked);
            ContentMatch? match = matcher.Check(file.FullPath, ct);
            Interlocked.Add(ref counters.Bytes, file.Size);
            if (match != null)
                hit(new SearchHit(file, null, Shown(match)));
            report(false);
        }
    }

    /// <summary>A match worth showing (rows that only say what isn't there have nothing to show).</summary>
    private static ContentMatch? Shown(ContentMatch match) =>
        match.Line == 0 && match.Snippet.Length == 0 && match.Offset < 0 ? null : match;

    private static bool IsArchive(string name) =>
        ArchiveExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>The name, size, date and attribute filters (not the text rows).</summary>
    internal static bool Fits(FileEntry entry, SearchCriteria c)
    {
        if (!c.Mask.IsMatch(entry.Name))
            return false;
        if (!entry.IsDirectory && ((c.MinSize is long min && entry.Size < min) || (c.MaxSize is long max && entry.Size > max)))
            return false;
        if ((c.ModifiedFrom is DateTime from && entry.LastWrite < from) || (c.ModifiedTo is DateTime to && entry.LastWrite > to))
            return false;
        if ((entry.Attributes & c.RequiredAttributes) != c.RequiredAttributes || (entry.Attributes & c.ExcludedAttributes) != 0)
            return false;
        return true;
    }

    /// <summary>The items in a ZIP file that fit, with their content checked.</summary>
    private static void SearchArchive(FileEntry archive, SearchCriteria criteria, ContentMatcher matcher, Counters counters,
        Action<SearchHit> hit, CancellationToken ct)
    {
        try
        {
            using ZipArchive zip = ZipFile.OpenRead(archive.FullPath);
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int depthLimit = criteria.MaxDepth < 0 ? int.MaxValue : criteria.MaxDepth;
            foreach (ZipArchiveEntry item in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                string inner = item.FullName.Replace('/', '\\').Trim('\\');
                if (inner.Length == 0)
                    continue;
                string[] parts = inner.Split('\\');
                bool isDirectory = item.FullName.EndsWith('/') || item.FullName.EndsWith('\\');
                int folders = isDirectory ? parts.Length : parts.Length - 1;
                // the archive's directories, also the ones only seen in the paths of what is in them
                for (int i = 0; i < folders; i++)
                {
                    if (i > depthLimit || (criteria.SkipFolders != null && criteria.SkipFolders.IsMatch(parts[i])))
                        break;
                    string dir = string.Join('\\', parts, 0, i + 1);
                    if (!directories.Add(dir) || !criteria.IncludeDirectories || !matcher.IsEmpty || criteria.HasSizeFilter)
                        continue;
                    var dirEntry = new FileEntry(parts[i], Path.Combine(archive.FullPath, dir), true, 0,
                        item.LastWriteTime.LocalDateTime, FileAttributes.Directory);
                    if (Fits(dirEntry, criteria))
                        hit(new SearchHit(dirEntry, archive.FullPath, null));
                }
                if (isDirectory || parts.Length - 1 > depthLimit ||
                    (criteria.SkipFolders != null && parts.Take(parts.Length - 1).Any(criteria.SkipFolders.IsMatch)))
                    continue;
                var entry = new FileEntry(parts[^1], Path.Combine(archive.FullPath, inner), false, item.Length,
                    item.LastWriteTime.LocalDateTime, FileAttributes.Normal);
                if (!Fits(entry, criteria))
                    continue;
                if (matcher.IsEmpty)
                {
                    hit(new SearchHit(entry, archive.FullPath, null));
                    continue;
                }
                Interlocked.Increment(ref counters.Checked);
                ContentMatch? match;
                try
                {
                    using Stream stream = item.Open();
                    match = matcher.Check(stream, item.Open, ct);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
                {
                    continue; // encrypted or damaged
                }
                Interlocked.Add(ref counters.Bytes, item.Length);
                if (match != null)
                    hit(new SearchHit(entry, archive.FullPath, Shown(match)));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            // not a ZIP after all, or can't be read
        }
    }
}
