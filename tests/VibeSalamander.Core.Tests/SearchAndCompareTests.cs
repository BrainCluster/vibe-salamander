// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Operations;
using VibeSalamander.Core.Search;
using VibeSalamander.Core.Tools;

namespace VibeSalamander.Core.Tests;

public sealed class SearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vibe-search-" + Guid.NewGuid().ToString("N")[..8]);

    public SearchTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Make(string relative, string content, Encoding? encoding = null)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, encoding ?? new UTF8Encoding(false));
        return path;
    }

    private async Task<List<SearchHit>> Search(Func<SearchCriteria, SearchCriteria>? change = null, params TextCondition[] texts)
    {
        var criteria = new SearchCriteria { Directories = new[] { _root }, Texts = texts };
        if (change != null)
            criteria = change(criteria);
        var hits = new List<SearchHit>();
        await FileSearcher.SearchAsync(criteria, h => { lock (hits) hits.Add(h); }, null, CancellationToken.None);
        return hits.OrderBy(h => h.Entry.FullPath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> Names(IEnumerable<SearchHit> hits) => hits.Select(h => h.Entry.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    private SearchCriteria FilesOnly(SearchCriteria c) => new()
    {
        Directories = c.Directories, Texts = c.Texts, IncludeDirectories = false, AllTexts = c.AllTexts, MatchCase = c.MatchCase,
        WholeWord = c.WholeWord,
    };

    [Fact]
    public async Task A_match_says_its_line_and_shows_it()
    {
        Make("a.txt", "first\n  second has the needle here\nthird");
        SearchHit hit = Assert.Single(await Search(FilesOnly, new TextCondition("NEEDLE")));
        Assert.Equal(2, hit.Match!.Line);
        Assert.StartsWith("second", hit.Match.Snippet, StringComparison.Ordinal);
        Assert.Equal("needle", hit.Match.Snippet.Substring(hit.Match.Column, hit.Match.Length));
    }

    [Fact]
    public async Task All_rows_or_any_row()
    {
        Make("both.txt", "apple and pear");
        Make("apple.txt", "apple only");
        Make("none.txt", "plum");
        var texts = new[] { new TextCondition("apple"), new TextCondition("pear") };
        Assert.Equal(new[] { "both.txt" }, Names(await Search(FilesOnly, texts)));
        Assert.Equal(new[] { "apple.txt", "both.txt" },
            Names(await Search(c => new SearchCriteria { Directories = c.Directories, Texts = texts, AllTexts = false, IncludeDirectories = false })));
    }

    [Fact]
    public async Task A_row_can_say_what_must_not_be_there()
    {
        Make("todo.txt", "apple TODO");
        Make("done.txt", "apple");
        var hits = await Search(FilesOnly, new TextCondition("apple"), new TextCondition("todo", Negate: true));
        Assert.Equal(new[] { "done.txt" }, Names(hits));
    }

    [Fact]
    public async Task Regular_expressions_and_whole_words()
    {
        Make("a.cs", "int count = 42;");
        Make("b.cs", "int counter = 7;");
        Assert.Equal(new[] { "a.cs" }, Names(await Search(FilesOnly, new TextCondition(@"count\s*=\s*\d+", TextMode.Regex))));
        Assert.Equal(new[] { "a.cs" },
            Names(await Search(c => new SearchCriteria { Directories = c.Directories, Texts = c.Texts, WholeWord = true, IncludeDirectories = false },
                new TextCondition("count"))));
    }

    [Fact]
    public async Task Hex_bytes_and_text_in_binary_files()
    {
        string bin = Path.Combine(_root, "app.bin");
        var bytes = new List<byte> { 0x4D, 0x5A, 0x90, 0x00, 0x00 };
        bytes.AddRange(Encoding.Unicode.GetBytes("Product Name"));
        bytes.AddRange(new byte[] { 0, 1, 2, 3 });
        File.WriteAllBytes(bin, bytes.ToArray());
        Make("text.txt", "MZ but not bytes");

        SearchHit hex = Assert.Single(await Search(FilesOnly, new TextCondition("4D 5A 90", TextMode.Hex)));
        Assert.Equal(0, hex.Match!.Offset);
        // UTF-16 text inside a binary file, any case
        SearchHit text = Assert.Single(await Search(FilesOnly, new TextCondition("product name")));
        Assert.Equal("app.bin", text.Entry.Name);
        Assert.Equal(5, text.Match!.Offset);
    }

    [Fact]
    public async Task Encodings_are_detected_per_file()
    {
        Make("utf16.txt", "line one\nžuta kuća\n", Encoding.Unicode);
        Make("ansi.txt", "x\ncafé", Encoding.Latin1);
        SearchHit utf16 = Assert.Single(await Search(FilesOnly, new TextCondition("kuća")));
        Assert.Equal(2, utf16.Match!.Line);
        // Latin-1 isn't the ANSI code page everywhere: only check the ASCII part is found with its line
        SearchHit ansi = Assert.Single(await Search(FilesOnly, new TextCondition("caf")));
        Assert.Equal("ansi.txt", ansi.Entry.Name);
        Assert.Equal(2, ansi.Match!.Line);
    }

    [Fact]
    public async Task Size_date_depth_and_skipped_folders()
    {
        Make("small.txt", "x");
        File.WriteAllBytes(Path.Combine(_root, "big.txt"), new byte[5000]);
        Make(@"deep\one\two\deep.txt", "x");
        Make(@"node_modules\pkg\index.js", "x");
        File.SetLastWriteTime(Path.Combine(_root, "small.txt"), new DateTime(2020, 1, 1));

        Assert.Equal(new[] { "big.txt" }, Names(await Search(c => new SearchCriteria { Directories = c.Directories, MinSize = 1000, IncludeDirectories = false })));
        Assert.Equal(new[] { "small.txt" }, Names(await Search(c => new SearchCriteria
        {
            Directories = c.Directories, ModifiedTo = new DateTime(2021, 1, 1), IncludeDirectories = false,
        })));
        var shallow = Names(await Search(c => new SearchCriteria { Directories = c.Directories, MaxDepth = 1, IncludeDirectories = false }));
        Assert.DoesNotContain("deep.txt", shallow);
        var skipped = Names(await Search(c => new SearchCriteria { Directories = c.Directories, SkipFolders = new WildcardMask("node_modules"), IncludeDirectories = false }));
        Assert.DoesNotContain("index.js", skipped);
        Assert.Contains("deep.txt", skipped);
    }

    [Fact]
    public async Task Several_folders_and_a_folder_inside_another_once()
    {
        Make(@"a\one.txt", "x");
        Make(@"b\two.txt", "x");
        var hits = new List<SearchHit>();
        await FileSearcher.SearchAsync(new SearchCriteria
        {
            Directories = new[] { Path.Combine(_root, "a"), Path.Combine(_root, "b"), _root },
            IncludeDirectories = false,
        }, h => { lock (hits) hits.Add(h); }, null, CancellationToken.None);
        Assert.Equal(new[] { "one.txt", "two.txt" }, Names(hits));
    }

    [Fact]
    public async Task Inside_zip_files()
    {
        string zip = Path.Combine(_root, "docs.zip");
        using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(archive.CreateEntry("notes/readme.txt").Open()))
                w.Write("hello\nthe secret word\n");
            using (var w = new StreamWriter(archive.CreateEntry("other.txt").Open()))
                w.Write("nothing");
        }
        SearchHit hit = Assert.Single(await Search(c => new SearchCriteria
        {
            Directories = c.Directories, Texts = new[] { new TextCondition("secret") }, InsideArchives = true, IncludeDirectories = false,
        }));
        Assert.Equal("readme.txt", hit.Entry.Name);
        Assert.Equal(zip, hit.Archive);
        Assert.Equal(Path.Combine(zip, "notes", "readme.txt"), hit.Entry.FullPath);
        Assert.Equal(2, hit.Match!.Line);

        var names = Names(await Search(c => new SearchCriteria { Directories = c.Directories, Mask = new WildcardMask("*.txt"), InsideArchives = true }));
        Assert.Equal(new[] { "other.txt", "readme.txt" }, names);
    }

    [Fact]
    public void Problems_are_said_before_searching()
    {
        Assert.NotNull(new SearchCriteria { Directories = new[] { _root }, Texts = new[] { new TextCondition("(", TextMode.Regex) } }.Problem());
        Assert.NotNull(new SearchCriteria { Directories = new[] { _root }, Texts = new[] { new TextCondition("xyz", TextMode.Hex) } }.Problem());
        Assert.NotNull(new SearchCriteria { Directories = new[] { _root }, MinSize = 10, MaxSize = 5 }.Problem());
        Assert.Null(new SearchCriteria { Directories = new[] { _root }, Texts = new[] { new TextCondition("4D5A", TextMode.Hex) } }.Problem());
    }

    [Fact]
    public async Task Duplicates_by_content_name_and_size()
    {
        string big = new string('x', 100_000);
        Make(@"a\photo.jpg", big + "1");
        Make(@"b\copy of photo.jpg", big + "1");
        Make(@"c\photo.jpg", big + "2"); // same size and edges? no: the last byte differs
        Make(@"d\note.txt", "same");
        Make(@"e\note.txt", "diff");

        var content = await DuplicateFinder.FindAsync(new DuplicateCriteria { Directories = new[] { _root } }, null, CancellationToken.None);
        DuplicateGroup group = Assert.Single(content);
        Assert.Equal(new[] { "copy of photo.jpg", "photo.jpg" }, group.Files.Select(f => f.Name).OrderBy(n => n).ToArray());
        Assert.Equal(100_001, group.Wasted);

        var byName = await DuplicateFinder.FindAsync(new DuplicateCriteria { Directories = new[] { _root }, Kind = DuplicateKind.Name }, null, CancellationToken.None);
        Assert.Equal(2, byName.Count);
        var byNameAndSize = await DuplicateFinder.FindAsync(new DuplicateCriteria
        {
            Directories = new[] { _root }, Kind = DuplicateKind.Name | DuplicateKind.Size,
        }, null, CancellationToken.None);
        Assert.Equal(2, byNameAndSize.Count); // photo.jpg (same size) and note.txt (same size)
    }

    [Fact]
    public async Task Small_files_are_compared_whole()
    {
        Make("x1.txt", "abc");
        Make("x2.txt", "abc");
        Make("x3.txt", "abd");
        DuplicateGroup group = Assert.Single(await DuplicateFinder.FindAsync(new DuplicateCriteria { Directories = new[] { _root } }, null, CancellationToken.None));
        Assert.Equal(2, group.Files.Count);
    }
}

public sealed class CompareAndSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vibe-sync-" + Guid.NewGuid().ToString("N")[..8]);
    private string Left => Path.Combine(_root, "L");
    private string Right => Path.Combine(_root, "R");

    public CompareAndSyncTests()
    {
        Directory.CreateDirectory(Left);
        Directory.CreateDirectory(Right);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static void Make(string path, string content, DateTime time)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTime(path, time);
    }

    [Fact]
    public async Task The_tree_comparison_lists_each_difference()
    {
        var old = new DateTime(2024, 1, 1, 12, 0, 0);
        var newer = old.AddDays(1);
        Make(Path.Combine(Left, "same.txt"), "same", old);
        Make(Path.Combine(Right, "same.txt"), "same", old);
        Make(Path.Combine(Left, "only-left.txt"), "x", old);
        Make(Path.Combine(Right, @"sub\only-right.txt"), "x", old);
        Make(Path.Combine(Left, @"sub\changed.txt"), "new version", newer);
        Make(Path.Combine(Right, @"sub\changed.txt"), "old", old);
        Make(Path.Combine(Left, "size.txt"), "aaaa", old);
        Make(Path.Combine(Right, "size.txt"), "a", old);
        Directory.CreateDirectory(Path.Combine(Left, "emptyleft"));

        var options = new CompareOptions { ByDate = true, BySize = true, Subdirectories = true };
        var diffs = await DirectorySync.CompareTreeAsync(Left, Right, options, null, CancellationToken.None);
        var byPath = diffs.ToDictionary(d => d.RelativePath, d => d.Kind);
        Assert.Equal(DifferenceKind.OnlyLeft, byPath["only-left.txt"]);
        Assert.Equal(DifferenceKind.OnlyLeft, byPath["emptyleft"]);
        Assert.Equal(DifferenceKind.OnlyRight, byPath[@"sub\only-right.txt"]);
        Assert.Equal(DifferenceKind.LeftNewer, byPath[@"sub\changed.txt"]);
        Assert.Equal(DifferenceKind.Different, byPath["size.txt"]);
        Assert.False(byPath.ContainsKey("same.txt"));

        // without subdirectories, "sub" on both sides isn't looked into
        var flat = await DirectorySync.CompareTreeAsync(Left, Right, new CompareOptions { Subdirectories = false }, null, CancellationToken.None);
        Assert.DoesNotContain(flat, d => d.RelativePath.StartsWith("sub", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Attributes_can_be_compared()
    {
        var time = new DateTime(2024, 1, 1);
        Make(Path.Combine(Left, "a.txt"), "x", time);
        Make(Path.Combine(Right, "a.txt"), "x", time);
        File.SetAttributes(Path.Combine(Right, "a.txt"), FileAttributes.ReadOnly);
        try
        {
            Assert.Empty(await DirectorySync.CompareTreeAsync(Left, Right, new CompareOptions(), null, CancellationToken.None));
            var diff = Assert.Single(await DirectorySync.CompareTreeAsync(Left, Right, new CompareOptions { ByAttributes = true }, null, CancellationToken.None));
            Assert.Equal(DifferenceKind.Different, diff.Kind);
        }
        finally
        {
            File.SetAttributes(Path.Combine(Right, "a.txt"), FileAttributes.Normal);
        }
    }

    private static CompareDifference D(string path, DifferenceKind kind) => new(path, false, kind, 1, 1, null, null);

    [Fact]
    public void Plans_one_way_mirror_and_both_ways()
    {
        var diffs = new[]
        {
            D("onlyL", DifferenceKind.OnlyLeft), D("onlyR", DifferenceKind.OnlyRight), D("newL", DifferenceKind.LeftNewer),
            D("newR", DifferenceKind.RightNewer), D("diff", DifferenceKind.Different), D("clash", DifferenceKind.FileAndDirectory),
        };
        string Ops(SyncPlan p) => string.Join(",", p.Actions.Select(a => a.Difference.RelativePath + ":" + a.Operation));

        SyncPlan update = DirectorySync.Plan(diffs, SyncDirection.LeftToRight, mirror: false);
        Assert.Equal("onlyL:CopyToRight,newL:CopyToRight,diff:CopyToRight", Ops(update));
        Assert.Equal(new[] { "clash" }, update.Conflicts.Select(c => c.RelativePath));

        SyncPlan mirror = DirectorySync.Plan(diffs, SyncDirection.LeftToRight, mirror: true);
        Assert.Equal("onlyL:CopyToRight,onlyR:DeleteRight,newL:CopyToRight,newR:CopyToRight,diff:CopyToRight", Ops(mirror));
        Assert.Equal(1, mirror.Deletes);

        SyncPlan back = DirectorySync.Plan(diffs, SyncDirection.RightToLeft, mirror: false);
        Assert.Equal("onlyR:CopyToLeft,newR:CopyToLeft,diff:CopyToLeft", Ops(back));

        SyncPlan both = DirectorySync.Plan(diffs, SyncDirection.Both, mirror: false);
        Assert.Equal("onlyL:CopyToRight,onlyR:CopyToLeft,newL:CopyToRight,newR:CopyToLeft", Ops(both));
        Assert.Equal(new[] { "diff", "clash" }, both.Conflicts.Select(c => c.RelativePath));
    }
}

public sealed class SpeedLimiterTests
{
    /// <summary>A clock that only moves when the limiter waits.</summary>
    private sealed class FakeClock
    {
        public long Ticks;
        public TimeSpan Waited;

        public long Now() => Ticks;

        public void Sleep(TimeSpan time, CancellationToken ct)
        {
            Waited += time;
            Ticks += (long)(time.TotalSeconds * Stopwatch.Frequency);
        }
    }

    [Fact]
    public void Without_a_limit_nothing_waits()
    {
        var clock = new FakeClock();
        var limiter = new SpeedLimiter(clock.Now, clock.Sleep);
        limiter.Take(100_000_000);
        Assert.Equal(TimeSpan.Zero, clock.Waited);
    }

    [Fact]
    public void A_limit_makes_copies_wait_for_their_bytes()
    {
        var clock = new FakeClock();
        var limiter = new SpeedLimiter(clock.Now, clock.Sleep) { BytesPerSecond = 1_000_000 };
        // ten megabytes in one-megabyte steps: about ten seconds (the first half second's burst is free)
        for (int i = 0; i < 10; i++)
            limiter.Take(1_000_000);
        Assert.InRange(clock.Waited.TotalSeconds, 9.4, 10.1);
    }

    [Fact]
    public void Raising_the_limit_while_waiting_shortens_the_wait()
    {
        var clock = new FakeClock();
        SpeedLimiter? limiter = null;
        limiter = new SpeedLimiter(clock.Now, (t, ct) =>
        {
            clock.Sleep(t, ct);
            if (clock.Waited >= TimeSpan.FromSeconds(1))
                limiter!.BytesPerSecond = 0;
        });
        limiter.BytesPerSecond = 100_000;
        limiter.Take(10_000_000); // 100 s at that speed
        Assert.InRange(clock.Waited.TotalSeconds, 1, 1.3);
    }

    [Fact]
    public void A_cancel_ends_the_wait()
    {
        var clock = new FakeClock();
        using var cts = new CancellationTokenSource();
        var limiter = new SpeedLimiter(clock.Now, (t, ct) =>
        {
            clock.Sleep(t, ct);
            cts.Cancel();
        }) { BytesPerSecond = 1000 };
        limiter.Take(1_000_000, cts.Token);
        Assert.True(clock.Waited.TotalSeconds < 1);
    }

    [Fact]
    public void The_history_keeps_the_latest_samples()
    {
        var history = new SpeedHistory(3);
        foreach (double s in new[] { 1.0, 2, 3, 4 })
            history.Add(s);
        Assert.Equal(new[] { 2.0, 3, 4 }, history.Samples);
        Assert.Equal(4, history.Max);
    }
}

public sealed class SpeedLimitsTests
{
    [Theory]
    [InlineData("5", 5L * 1024 * 1024)]
    [InlineData("500 KB", 500L * 1024)]
    [InlineData("1.5 MB/s", 1572864L)]
    [InlineData("1,5mb", 1572864L)]
    [InlineData("2G", 2L * 1024 * 1024 * 1024)]
    [InlineData("", 0L)]
    [InlineData("none", 0L)]
    [InlineData("0", 0L)]
    public void Typed_limits(string text, long expected) => Assert.Equal(expected, SpeedLimits.Parse(text));

    [Fact]
    public void Nonsense_isnt_a_limit()
    {
        Assert.Null(SpeedLimits.Parse("fast"));
        Assert.Null(SpeedLimits.Parse("-5"));
    }
}

public sealed class SearchFormTests
{
    [Fact]
    public void The_form_becomes_criteria()
    {
        string temp = Path.GetTempPath();
        var form = new VibeSalamander.Core.Settings.SavedSearch
        {
            Named = "*.cs",
            LookIn = temp + ";" + temp,
            Texts = { new() { Text = "TODO" }, new() { Text = "x+", Mode = "Regex", Negate = true }, new() { Text = "" } },
            MinSize = "1 KB",
            MaxSize = "2mb",
            Modified = "Week",
            Attributes = "R+H-",
            Depth = 2,
            SkipFolders = "bin;obj",
        };
        var now = new DateTime(2026, 10, 28, 15, 0, 0);
        SearchCriteria c = SearchForm.Criteria(form, now, 4, out string? problem)!;
        Assert.Null(problem);
        Assert.Equal(2, c.Texts.Count);
        Assert.Equal(TextMode.Regex, c.Texts[1].Mode);
        Assert.True(c.Texts[1].Negate);
        Assert.Equal(1024, c.MinSize);
        Assert.Equal(2 * 1024 * 1024, c.MaxSize);
        Assert.Equal(new DateTime(2026, 10, 21), c.ModifiedFrom);
        Assert.Equal(FileAttributes.ReadOnly, c.RequiredAttributes);
        Assert.Equal(FileAttributes.Hidden, c.ExcludedAttributes);
        Assert.Equal(2, c.MaxDepth);
        Assert.True(c.SkipFolders!.IsMatch("obj"));
        Assert.Equal("R+H-", SearchForm.FormatAttributes(c.RequiredAttributes, c.ExcludedAttributes));
    }

    [Fact]
    public void Problems_in_the_form()
    {
        var form = new VibeSalamander.Core.Settings.SavedSearch { LookIn = Path.GetTempPath(), MinSize = "big" };
        Assert.Null(SearchForm.Criteria(form, DateTime.Now, 0, out string? problem));
        Assert.NotNull(problem);
        form = new VibeSalamander.Core.Settings.SavedSearch { LookIn = @"Z:\surely\not\here" + Guid.NewGuid() };
        Assert.Null(SearchForm.Criteria(form, DateTime.Now, 0, out problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void Between_two_days_includes_the_last_one()
    {
        var form = new VibeSalamander.Core.Settings.SavedSearch { Modified = "Between", From = new DateTime(2026, 1, 1), To = new DateTime(2026, 1, 31) };
        var (from, to) = SearchForm.Dates(form, DateTime.Now);
        Assert.Equal(new DateTime(2026, 1, 1), from);
        Assert.Equal(new DateTime(2026, 1, 31, 23, 59, 59).AddTicks(9_999_999), to);
    }
}

public sealed class KeySchemeTests
{
    private static VibeSalamander.Core.Commands.CommandId Key(VibeSalamander.Core.Commands.KeyMap map, int key,
        VibeSalamander.Core.Commands.KeyModifiers mods = VibeSalamander.Core.Commands.KeyModifiers.None) => map.Resolve(key, mods);

    [Fact]
    public void Total_Commander_keys()
    {
        var tc = VibeSalamander.Core.Commands.KeyMap.CreateTotalCommanderDefaults();
        const VibeSalamander.Core.Commands.KeyModifiers S = VibeSalamander.Core.Commands.KeyModifiers.Shift, C = VibeSalamander.Core.Commands.KeyModifiers.Ctrl,
            A = VibeSalamander.Core.Commands.KeyModifiers.Alt;
        Assert.Equal(VibeSalamander.Core.Commands.CommandId.Refresh, Key(tc, VibeSalamander.Core.Commands.VK.F2));
        Assert.Equal(VibeSalamander.Core.Commands.CommandId.QuickRename, Key(tc, VibeSalamander.Core.Commands.VK.F6, S));
        Assert.Equal(VibeSalamander.Core.Commands.CommandId.MainMenu, Key(tc, VibeSalamander.Core.Commands.VK.F9));
        Assert.Equal(VibeSalamander.Core.Commands.CommandId.ViewBrief, Key(tc, VibeSalamander.Core.Commands.VK.F1, C));
        Assert.Equal(VibeSalamander.Core.Commands.CommandId.ViewDetailed, Key(tc, VibeSalamander.Core.Commands.VK.F2, C));
        Assert.Equal(VibeSalamander.Core.Commands.CommandId.Unpack, Key(tc, VibeSalamander.Core.Commands.VK.F9, A));
        Assert.Equal(VibeSalamander.Core.Commands.CommandId.CompareDirectories, Key(tc, VibeSalamander.Core.Commands.VK.F2, S));
        Assert.Equal(VibeSalamander.Core.Commands.CommandId.CalculateOccupiedSpace, Key(tc, VibeSalamander.Core.Commands.VK.L, C));
        Assert.Equal(VibeSalamander.Core.Commands.CommandId.RestoreSelection, Key(tc, VibeSalamander.Core.Commands.VK.Divide));
        Assert.Equal(VibeSalamander.Core.Commands.CommandId.SelectAll, Key(tc, VibeSalamander.Core.Commands.VK.Add, C));
        Assert.Equal(VibeSalamander.Core.Commands.CommandId.Copy, Key(tc, VibeSalamander.Core.Commands.VK.F5));
        // Salamander's are still Salamander's
        var salamander = VibeSalamander.Core.Commands.KeyMap.CreateSalamanderDefaults();
        Assert.Equal(VibeSalamander.Core.Commands.CommandId.QuickRename, Key(salamander, VibeSalamander.Core.Commands.VK.F2));
        Assert.Equal(VibeSalamander.Core.Commands.CommandId.UserMenu, Key(salamander, VibeSalamander.Core.Commands.VK.F9));
    }

    [Fact]
    public void Changes_are_kept_on_top_of_the_scheme()
    {
        var scheme = VibeSalamander.Core.Commands.KeyScheme.TotalCommander;
        var map = VibeSalamander.Core.Commands.KeyMap.CreateDefaults(scheme);
        Assert.Empty(map.GetOverrides(scheme));
        map.Bind(VibeSalamander.Core.Commands.VK.F2, VibeSalamander.Core.Commands.KeyModifiers.None, VibeSalamander.Core.Commands.CommandId.QuickRename);
        map.Unbind(new VibeSalamander.Core.Commands.KeyChord(VibeSalamander.Core.Commands.VK.F9, VibeSalamander.Core.Commands.KeyModifiers.None));
        var overrides = map.GetOverrides(scheme);
        Assert.Equal(2, overrides.Count);
        var restored = VibeSalamander.Core.Commands.KeyMap.Create(scheme, overrides);
        Assert.Equal(VibeSalamander.Core.Commands.CommandId.QuickRename, restored.Resolve(VibeSalamander.Core.Commands.VK.F2, VibeSalamander.Core.Commands.KeyModifiers.None));
        Assert.Equal(VibeSalamander.Core.Commands.CommandId.None, restored.Resolve(VibeSalamander.Core.Commands.VK.F9, VibeSalamander.Core.Commands.KeyModifiers.None));
        Assert.Equal(VibeSalamander.Core.Commands.CommandId.Copy, restored.Resolve(VibeSalamander.Core.Commands.VK.F5, VibeSalamander.Core.Commands.KeyModifiers.None));
    }
}

public sealed class FileComparisonTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vibe-filecmp-" + Guid.NewGuid().ToString("N")[..8]);

    public FileComparisonTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string F(string name, byte[] content)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public void Kinds()
    {
        string a = F("a.txt", Encoding.UTF8.GetBytes("hello\nworld\n"));
        string b = F("b.txt", Encoding.UTF8.GetBytes("hello\nthere\n"));
        string c = F("c.txt", Encoding.UTF8.GetBytes("hello\nworld\n"));
        string bin = F("x.bin", new byte[] { 0, 1, 2, 3, 0, 0 });
        Assert.Equal(ComparisonKind.Text, FileComparison.KindOf(a, b));
        Assert.Equal(ComparisonKind.Identical, FileComparison.KindOf(a, c));
        Assert.Equal(ComparisonKind.Binary, FileComparison.KindOf(a, bin));
    }

    [Fact]
    public void Binary_differences_line_up_on_both_sides()
    {
        var left = new byte[1000];
        var right = new byte[1010];
        right[100] = 7;
        right[101] = 8;
        right[900] = 9;
        BinaryComparison cmp = FileComparison.CompareBinary(F("l.bin", left), F("r.bin", right));
        Assert.Equal(new (long, long)[] { (100, 2), (900, 1), (1000, 10) }, cmp.Differences);
        Assert.Equal(3, cmp.DifferentBytes);
        Assert.Equal(cmp.Left.Split('\n').Length, cmp.Right.Split('\n').Length);
        Assert.Contains("···", cmp.Left);
        Assert.Contains("07 08", cmp.Right);
    }

    [Fact]
    public void Which_files_are_compared()
    {
        string l1 = F(@"L\a.txt", new byte[] { 1 });
        string l2 = F(@"L\b.txt", new byte[] { 2 });
        string r1 = F(@"R\a.txt", new byte[] { 3 });
        string r2 = F(@"R\z.txt", new byte[] { 4 });
        string right = Path.Combine(_root, "R");
        // two marked on the active side
        Assert.Equal((l1, l2), FileComparison.Pick(new[] { l1, l2 }, null, true, Array.Empty<string>(), null, right));
        // the caret's file and the same name on the other side; right panel active: left stays left
        Assert.Equal((l1, r1), FileComparison.Pick(Array.Empty<string>(), l1, true, Array.Empty<string>(), r2, right));
        Assert.Equal((r1, l1), FileComparison.Pick(Array.Empty<string>(), l1, false, Array.Empty<string>(), r2, right));
        // no same name: the other side's caret
        Assert.Equal((l2, r2), FileComparison.Pick(Array.Empty<string>(), l2, true, Array.Empty<string>(), r2, right));
        Assert.Null(FileComparison.Pick(Array.Empty<string>(), null, true, Array.Empty<string>(), null, right));
    }
}

public sealed class TransferSchedulerTests
{
    [Fact]
    public void Disks_of_a_transfer()
    {
        Assert.Equal(new[] { @"C:\", @"D:\" }, TransferScheduler.Disks(new[] { @"c:\a\b.txt", @"C:\x" }, @"D:\target"));
        Assert.Equal(new[] { @"\\server" }, TransferScheduler.Disks(new[] { @"\\server\share\a" }, null));
        Assert.Equal(new[] { @"C:\", "ftp://host" }, TransferScheduler.Disks(new[] { @"C:\a" }, "ftp://host/dir/"));
    }

    [Fact]
    public void One_per_disk_in_queue_order()
    {
        IReadOnlyList<string> D(params string[] d) => d;
        var running = new[] { D(@"C:\", @"D:\") };
        var waiting = new[] { D(@"C:\", @"E:\"), D(@"F:\", @"G:\"), D(@"E:\"), D(@"F:\") };
        // C: is busy; F:/G: can go; then E: alone; F: is taken by the second one now
        Assert.Equal(new[] { 1, 2 }, TransferScheduler.Startable(waiting, running, 8, onePerDisk: true));
        // without the per-disk rule: the first ones up to the limit
        Assert.Equal(new[] { 0, 1 }, TransferScheduler.Startable(waiting, running, 3, onePerDisk: false));
        // the limit counts the running ones
        Assert.Empty(TransferScheduler.Startable(waiting, running, 1, onePerDisk: true));
    }
}
