// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Operations;
using VibeSalamander.Core.Panels;
using VibeSalamander.Core.Search;
using VibeSalamander.Core.Viewing;

namespace VibeSalamander.Core.Tests;

/// <summary>The measured work runs alone (other tests running beside it would be measured too).</summary>
[CollectionDefinition("Performance", DisableParallelization = true)]
public sealed class PerformanceCollection : ICollectionFixture<PerformanceFiles>
{
}

/// <summary>
/// The files the measurements run on, made once: a folder of 20,000 files, 2,000 small text files, a tree of 1,000
/// files and a 64 MB text file (enough to see a slowdown, small enough to make in a few seconds). The times go to a table (the file named by VIBE_BENCHMARKS, else a temp file) that
/// the build shows in its summary.
/// </summary>
public sealed class PerformanceFiles : IDisposable
{
    public const int ManyCount = 20_000;
    public const int TextCount = 2_000;
    public const int TreeCount = 1_000;
    public const long BigBytes = 64L * 1024 * 1024;

    public string Root { get; } = Directory.CreateTempSubdirectory("vibe-speed").FullName;
    public string Many => Path.Combine(Root, "many");
    public string Texts => Path.Combine(Root, "texts");
    public string Tree => Path.Combine(Root, "tree");
    public string Big => Path.Combine(Root, "big.log");

    private readonly string _table =
        Environment.GetEnvironmentVariable("VIBE_BENCHMARKS") is { Length: > 0 } file ? file : Path.Combine(Path.GetTempPath(), "vibe-benchmarks.md");
    private readonly object _lock = new();

    public PerformanceFiles()
    {
        Directory.CreateDirectory(Many);
        Parallel.For(0, ManyCount, i => File.WriteAllBytes(Path.Combine(Many, $"file {i:00000}.{(i % 7 == 0 ? "log" : "txt")}"), Array.Empty<byte>()));

        Directory.CreateDirectory(Texts);
        string filler = string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog\r\n", 45)); // about 2 KB
        Parallel.For(0, TextCount, i => File.WriteAllText(Path.Combine(Texts, $"note {i:0000}.txt"), i % 100 == 0 ? filler + "needle\r\n" : filler + i + "\r\n"));

        Parallel.For(0, TreeCount, i =>
        {
            string dir = Path.Combine(Tree, $"a{i % 10}", $"b{i / 10 % 10}", $"c{i / 100 % 10}");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"item {i:0000}.txt"), filler);
        });

        using (var stream = new FileStream(Big, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            byte[] block = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("2026-11-16 12:00:00 INFO a line of a log file that is like many others\r\n", 14000)));
            while (stream.Length < BigBytes)
                stream.Write(block);
        }

        File.WriteAllLines(_table, new[] { "| Measured | Time | Limit |", "|---|---:|---:|" });
    }

    /// <summary>Runs the work, writes its time to the table and fails when it took longer than the limit.</summary>
    public async Task<T> MeasureAsync<T>(string what, double limitSeconds, Func<Task<T>> work)
    {
        var clock = Stopwatch.StartNew();
        T result = await work();
        clock.Stop();
        lock (_lock)
            File.AppendAllLines(_table, new[] { string.Create(CultureInfo.InvariantCulture, $"| {what} | {clock.Elapsed.TotalMilliseconds:N0} ms | {limitSeconds:0} s |") });
        Assert.True(clock.Elapsed.TotalSeconds <= limitSeconds,
            string.Create(CultureInfo.InvariantCulture, $"{what} took {clock.Elapsed.TotalSeconds:0.0} s (the limit is {limitSeconds:0} s)"));
        return result;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a temp folder left behind is not a test failure
        }
    }
}

/// <summary>
/// Speed is one of the program's principles, so it is measured (roadmap T4). The limits are many times what a
/// build machine needs: they don't judge the speed, they catch a change that makes something ten times slower.
/// The measured times are in the build's summary, to compare from release to release.
/// </summary>
[Collection("Performance")]
public sealed class PerformanceTests(PerformanceFiles files)
{
    private static Task<DirectoryListing> ListMany(PerformanceFiles from) =>
        new LocalFileSystemProvider().ListAsync(from.Many, new ListingOptions { ShowHidden = true }, CancellationToken.None);

    [Fact]
    public async Task Listing_a_large_folder()
    {
        DirectoryListing listing = await files.MeasureAsync("List a folder of 20,000 files", 10, () => ListMany(files));

        Assert.Null(listing.Error);
        Assert.Equal(PerformanceFiles.ManyCount, listing.Entries.Count(e => !e.IsUpDir));
    }

    [Fact]
    public async Task Sorting_a_large_folder()
    {
        List<FileEntry> entries = (await ListMany(files)).Entries.Where(e => !e.IsUpDir).ToList();

        List<FileEntry> byName = await files.MeasureAsync("Sort 20,000 files by name", 10,
            () => Task.FromResult(new EntryComparer(SortMode.Name, descending: false).Sort(entries)));
        await files.MeasureAsync("Sort 20,000 files by extension, descending", 10,
            () => Task.FromResult(new EntryComparer(SortMode.Extension, descending: true).Sort(entries)));

        Assert.Equal("file 00000.log", byName[0].Name);
        Assert.Equal("file 19999.log", byName[^1].Name);
    }

    [Fact]
    public async Task Searching_names_and_text()
    {
        int byName = 0, byText = 0;

        await files.MeasureAsync("Find *.log among 20,000 files", 10, () => FileSearcher.SearchAsync(
            new SearchCriteria { Directories = new[] { files.Many }, Mask = new WildcardMask("*.log") },
            _ => Interlocked.Increment(ref byName), null, CancellationToken.None));
        await files.MeasureAsync("Find a text in 2,000 files", 30, () => FileSearcher.SearchAsync(
            new SearchCriteria { Directories = new[] { files.Texts }, Texts = new[] { new TextCondition("needle") } },
            _ => Interlocked.Increment(ref byText), null, CancellationToken.None));

        Assert.Equal((PerformanceFiles.ManyCount + 6) / 7, byName);
        Assert.Equal(PerformanceFiles.TextCount / 100, byText);
    }

    [Fact]
    public async Task Finding_duplicates()
    {
        DuplicateResult result = await files.MeasureAsync("Find duplicates among 2,000 files (by content)", 60,
            () => DuplicateFinder.FindAsync(new DuplicateCriteria { Directories = new[] { files.Texts } }, null, CancellationToken.None));

        Assert.Single(result); // the files with "needle" are the same; the others have their number in them
        Assert.Equal(PerformanceFiles.TextCount / 100, result[0].Files.Count);
    }

    [Fact]
    public async Task Copying_a_tree_of_small_files()
    {
        string target = Path.Combine(files.Root, "copy of tree");

        OperationResult result = await files.MeasureAsync("Copy a tree of 1,000 small files", 120,
            () => new FileOperationEngine(new NoQuestions()).CopyAsync(new[] { files.Tree }, target));

        Assert.Equal(PerformanceFiles.TreeCount, result.Succeeded);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task Opening_a_large_text_file()
    {
        Task? indexing = null;
        using TextFile file = await files.MeasureAsync("Open a 64 MB text file and show its first lines", 5, async () =>
        {
            TextFile opened = TextFile.Open(files.Big);
            indexing = opened.IndexAsync(CancellationToken.None); // the lines are found in the background, as in the viewer
            while (opened.LineCount < 50 && !indexing.IsCompleted)
                await Task.Delay(1);
            Assert.Equal(50, opened.GetLines(0, 50).Count);
            return opened;
        });

        await files.MeasureAsync("Index all lines of the 64 MB text file", 120, async () =>
        {
            await indexing!;
            return file.LineCount;
        });

        Assert.True(file.LineCount > 500_000, $"{file.LineCount} lines");
    }

    private sealed class NoQuestions : IOperationCallbacks
    {
        public Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict) => Task.FromResult(ConflictChoice.Cancel);
        public Task<ErrorChoice> ResolveErrorAsync(OperationError error) => Task.FromResult(ErrorChoice.Cancel);
    }
}
