// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Formatting;
using VibeSalamander.Core.Search;
using VibeSalamander.Core.Tools;

namespace VibeSalamander.Core.Tests;

/// <summary>
/// Files only in the cloud are never downloaded by a tool that only wanted to look (roadmap T1). A test machine has no
/// OneDrive, so the files here carry the Offline attribute, which the rule treats the same way (reading an offline
/// file recalls it from its storage) and which, unlike the cloud placeholders' own attribute, a program can set.
/// </summary>
public sealed class CloudFilesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vibe-cloud").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string Make(string name, string content, bool onlineOnly = false)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (onlineOnly)
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Offline);
            Assert.True((File.GetAttributes(path) & FileAttributes.Offline) != 0, "the Offline attribute could not be set");
        }
        return path;
    }

    // ---- the rule ---------------------------------------------------------------------------------------------

    [Fact]
    public void Online_only_means_the_content_is_not_on_this_computer()
    {
        Assert.True(CloudFiles.IsOnlineOnly(CloudFiles.RecallOnDataAccess | FileAttributes.Archive));
        Assert.True(CloudFiles.IsOnlineOnly(FileAttributes.Offline));
        Assert.False(CloudFiles.IsOnlineOnly(FileAttributes.Archive));
        Assert.False(CloudFiles.IsOnlineOnly(CloudFiles.Pinned | FileAttributes.Archive)); // always kept here
        Assert.False(CloudFiles.IsOnlineOnly(FileAttributes.Directory | CloudFiles.RecallOnDataAccess)); // folders are listed, not read

        Assert.Equal(CloudState.OnlineOnly, CloudFiles.State(CloudFiles.RecallOnDataAccess | CloudFiles.Unpinned));
        Assert.Equal(CloudState.AlwaysKept, CloudFiles.State(CloudFiles.Pinned));
        Assert.Equal(CloudState.Local, CloudFiles.State(FileAttributes.Normal));

        var upDir = FileEntry.CreateUpDir(_root);
        Assert.False(CloudFiles.IsOnlineOnly(upDir));
    }

    [Fact]
    public void The_attribute_column_shows_the_cloud_state_as_attrib_does()
    {
        Assert.Equal("AO", Formats.Attributes(FileAttributes.Archive | CloudFiles.RecallOnDataAccess));
        Assert.Equal("AP", Formats.Attributes(FileAttributes.Archive | CloudFiles.Pinned));
        Assert.Equal("AOU", Formats.Attributes(FileAttributes.Archive | FileAttributes.Offline | CloudFiles.Unpinned));
    }

    // ---- Find ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Find_with_text_does_not_read_files_only_in_the_cloud_and_says_how_many()
    {
        Make("here.txt", "needle");
        Make("cloud.txt", "needle", onlineOnly: true);
        Make("cloud.zip", "not a zip", onlineOnly: true);

        (List<string> found, SearchSummary summary) = await Find(texts: true, includeOnlineOnly: false);

        Assert.Equal(new[] { "here.txt" }, found);
        Assert.Equal(1, summary.OnlineOnlySkipped); // the text file (the zip doesn't fit "*.txt")
        Assert.Equal(1, summary.FilesChecked);
    }

    [Fact]
    public async Task Find_reads_them_when_asked()
    {
        Make("here.txt", "needle");
        Make("cloud.txt", "needle", onlineOnly: true);

        (List<string> found, SearchSummary summary) = await Find(texts: true, includeOnlineOnly: true);

        Assert.Equal(new[] { "cloud.txt", "here.txt" }, found);
        Assert.Equal(0, summary.OnlineOnlySkipped);
    }

    [Fact]
    public async Task Find_by_name_finds_files_only_in_the_cloud_without_reading_them()
    {
        Make("here.txt", "x");
        Make("cloud.txt", "x", onlineOnly: true);

        (List<string> found, SearchSummary summary) = await Find(texts: false, includeOnlineOnly: false);

        Assert.Equal(new[] { "cloud.txt", "here.txt" }, found);
        Assert.Equal(0, summary.OnlineOnlySkipped);
    }

    [Fact]
    public async Task Find_inside_ZIP_files_does_not_open_one_only_in_the_cloud()
    {
        Make("cloud.zip", "not even a zip", onlineOnly: true);
        var hits = new List<SearchHit>();

        SearchSummary summary = await FileSearcher.SearchAsync(new SearchCriteria
        {
            Directories = new[] { _root },
            InsideArchives = true,
        }, h => { lock (hits) hits.Add(h); }, null, CancellationToken.None);

        Assert.Equal("cloud.zip", Assert.Single(hits).Entry.Name); // found by its name, not opened
        Assert.Equal(1, summary.OnlineOnlySkipped);
    }

    private async Task<(List<string> Found, SearchSummary Summary)> Find(bool texts, bool includeOnlineOnly)
    {
        var hits = new List<SearchHit>();
        SearchSummary summary = await FileSearcher.SearchAsync(new SearchCriteria
        {
            Directories = new[] { _root },
            Mask = new WildcardMask("*.txt"),
            Texts = texts ? new[] { new TextCondition("needle") } : Array.Empty<TextCondition>(),
            IncludeOnlineOnly = includeOnlineOnly,
        }, h => { lock (hits) hits.Add(h); }, null, CancellationToken.None);
        return (hits.Select(h => h.Entry.Name).Order(StringComparer.OrdinalIgnoreCase).ToList(), summary);
    }

    // ---- Find Duplicates ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Duplicates_by_content_leave_out_files_only_in_the_cloud_and_say_how_many()
    {
        Make(@"a\photo.jpg", "same bytes");
        Make(@"b\photo.jpg", "same bytes");
        Make(@"c\photo.jpg", "same bytes", onlineOnly: true);
        Make(@"d\other.jpg", "other size here", onlineOnly: true); // no file of its size: it would never be read

        DuplicateResult result = await DuplicateFinder.FindAsync(new DuplicateCriteria { Directories = new[] { _root } }, null, CancellationToken.None);

        DuplicateGroup group = Assert.Single(result);
        Assert.Equal(2, group.Files.Count);
        Assert.DoesNotContain(group.Files, f => f.FullPath.Contains(@"\c\", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, result.OnlineOnlySkipped);
    }

    [Fact]
    public async Task Duplicates_read_them_when_asked_and_by_name_need_no_reading()
    {
        Make(@"a\photo.jpg", "same bytes");
        Make(@"b\photo.jpg", "same bytes", onlineOnly: true);

        DuplicateResult included = await DuplicateFinder.FindAsync(new DuplicateCriteria
        {
            Directories = new[] { _root }, IncludeOnlineOnly = true,
        }, null, CancellationToken.None);
        DuplicateResult byName = await DuplicateFinder.FindAsync(new DuplicateCriteria
        {
            Directories = new[] { _root }, Kind = DuplicateKind.Name,
        }, null, CancellationToken.None);

        Assert.Equal(2, Assert.Single(included).Files.Count);
        Assert.Equal(0, included.OnlineOnlySkipped);
        Assert.Equal(2, Assert.Single(byName).Files.Count);
        Assert.Equal(0, byName.OnlineOnlySkipped);
    }

    // ---- Compare Directories ------------------------------------------------------------------------------------

    [Fact]
    public void Comparing_content_skips_a_file_only_in_the_cloud_and_counts_it()
    {
        var time = new DateTime(2024, 1, 1);
        // the paths don't exist: reading them would make the files "different"
        var here = new DirectoryComparer.Item("a.txt", false, 10, time, FileAttributes.Archive);
        var cloud = new DirectoryComparer.Item("a.txt", false, 10, time, FileAttributes.Archive | CloudFiles.RecallOnDataAccess);
        var options = new CompareOptions { ByContent = true, ByDate = true, BySize = true };

        DirectoryComparer.Difference skipped = DirectoryComparer.CompareFiles(@"Z:\nowhere\left\a.txt", here, @"Z:\nowhere\right\a.txt", cloud, options, CancellationToken.None);
        Assert.Equal(DirectoryComparer.Difference.None, skipped);
        Assert.Equal(1, options.OnlineOnlyNotCompared);

        options.ContentOfOnlineOnly = true;
        DirectoryComparer.Difference read = DirectoryComparer.CompareFiles(@"Z:\nowhere\left\a.txt", here, @"Z:\nowhere\right\a.txt", cloud, options, CancellationToken.None);
        Assert.Equal(DirectoryComparer.Difference.Both, read); // it was read (and couldn't be)
    }

    [Fact]
    public async Task Compare_directories_reports_the_files_it_did_not_read_and_still_compares_sizes()
    {
        string left = Path.Combine(_root, "left");
        string right = Path.Combine(_root, "right");
        var time = new DateTime(2024, 1, 1);
        foreach (string dir in new[] { left, right })
        {
            File.SetLastWriteTime(Make(Path.Combine(dir, "same.txt"), "1234"), time);
            File.SetLastWriteTime(Make(Path.Combine(dir, "cloud.txt"), "abcd", onlineOnly: dir == right), time);
        }
        File.SetLastWriteTime(Make(Path.Combine(left, "bigger.txt"), "12345"), time);
        File.SetLastWriteTime(Make(Path.Combine(right, "bigger.txt"), "1234", onlineOnly: true), time);
        var options = new CompareOptions { ByContent = true };

        CompareResult result = await DirectoryComparer.CompareAsync(left, Items(left), right, Items(right), options);

        Assert.Equal(new[] { "bigger.txt" }, result.Left.ToArray()); // the size still tells (nothing read for it)
        Assert.Equal(1, result.OnlineOnlyNotCompared); // cloud.txt
        IReadOnlyList<CompareDifference> tree = await DirectorySync.CompareTreeAsync(left, right, options, null, CancellationToken.None);
        Assert.Equal("bigger.txt", Assert.Single(tree).Name);
        Assert.Equal(1, options.OnlineOnlyNotCompared); // counted again for the tree, not added up
    }

    private static List<DirectoryComparer.Item> Items(string dir) =>
        new DirectoryInfo(dir).EnumerateFiles()
            .Select(f => new DirectoryComparer.Item(f.Name, false, f.Length, f.LastWriteTime, f.Attributes))
            .ToList();
}
