// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.Operations;

namespace VibeSalamander.Core.Tests;

/// <summary>Pack and Unpack decisions (Core.Operations.ArchiveRules).</summary>
public class ArchiveRulesTests
{
    [Theory]
    [InlineData(1, "report.txt", false, @"C:\docs", "report")]
    [InlineData(1, "photos", true, @"C:\docs", "photos")]
    [InlineData(1, "archive.tar.gz", false, @"C:\docs", "archive.tar")]
    [InlineData(3, "report.txt", false, @"C:\docs\", "docs")]
    [InlineData(3, "report.txt", false, @"D:\", "D")]
    public void The_archive_is_named_after_the_item_or_the_folder(int count, string first, bool isDirectory, string panel, string name) =>
        Assert.Equal(name, ArchiveRules.PackBaseName(count, first, isDirectory, panel));

    private static string ArchivePath(string typed, params string[] directories) =>
        ArchiveRules.PackArchivePath(@"C:\docs", typed, "report", ".zip",
            path => directories.Contains(path, StringComparer.OrdinalIgnoreCase),
            path => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void The_typed_archive_path()
    {
        Assert.Equal(@"C:\docs\x.zip", ArchivePath("x.zip"));
        Assert.Equal(@"C:\docs\x.zip", ArchivePath("x"));                     // the extension is added
        Assert.Equal(@"D:\out\report.zip", ArchivePath(@"D:\out\"));           // only a directory: the suggested name
        Assert.Equal(@"D:\out\report.zip", ArchivePath(@"D:\out", @"D:\out")); // an existing directory too
        Assert.Equal(@"C:\docs\sub\a.zip", ArchivePath(@"sub\a"));             // relative to the panel
    }

    [Fact]
    public void Format_settings_come_from_the_saved_ones_or_their_defaults()
    {
        var saved = new Dictionary<string, string> { ["7z.method"] = "LZMA2", ["zip.method"] = "Deflate" };
        Dictionary<string, string> options = ArchiveRules.FormatOptions("7z", new[] { ("method", "LZMA"), ("solid", "true") }, saved);
        Assert.Equal("LZMA2", options["method"]);
        Assert.Equal("true", options["solid"]);
        Assert.Equal(2, options.Count);
    }

    [Fact]
    public void Move_to_archive_deletes_only_after_a_complete_pack()
    {
        string[] sources = { @"C:\docs\a.txt", @"C:\docs\photos" };
        Assert.True(ArchiveRules.MayDeleteSources(true, new OperationResult(), sources, @"D:\a.zip"));
        Assert.False(ArchiveRules.MayDeleteSources(false, new OperationResult(), sources, @"D:\a.zip"));
        Assert.False(ArchiveRules.MayDeleteSources(true, new OperationResult { Failed = 1 }, sources, @"D:\a.zip"));
        Assert.False(ArchiveRules.MayDeleteSources(true, new OperationResult { Skipped = 1 }, sources, @"D:\a.zip"));
        Assert.False(ArchiveRules.MayDeleteSources(true, new OperationResult { Cancelled = true }, sources, @"D:\a.zip"));
        // the archive inside a packed directory: that directory must stay
        Assert.False(ArchiveRules.MayDeleteSources(true, new OperationResult(), sources, @"C:\docs\photos\all.zip"));
        Assert.True(ArchiveRules.MayDeleteSources(true, new OperationResult(), sources, @"C:\docs\photos2\all.zip"));
    }

    [Fact]
    public void A_single_root_folder_is_found()
    {
        Assert.Equal("photos", ArchiveRules.SingleRootFolder(new[] { ("photos/", true), ("photos/a.jpg", false), ("photos/2026/b.jpg", false) }));
        Assert.Equal("photos", ArchiveRules.SingleRootFolder(new[] { ("photos\\a.jpg", false), ("Photos\\b.jpg", false) })); // no directory entries
        Assert.Equal("src", ArchiveRules.SingleRootFolder(new[] { ("./src/a.cs", false) }));
        Assert.Null(ArchiveRules.SingleRootFolder(new[] { ("photos/a.jpg", false), ("readme.txt", false) }));
        Assert.Null(ArchiveRules.SingleRootFolder(new[] { ("a/x.txt", false), ("b/y.txt", false) }));
        Assert.Null(ArchiveRules.SingleRootFolder(new[] { ("report.pdf", false) }));
        Assert.Null(ArchiveRules.SingleRootFolder(Array.Empty<(string, bool)>()));
    }

    [Fact]
    public void Unpacking_goes_to_the_other_panel_when_it_is_on_a_disk()
    {
        Assert.Equal(@"D:\x\", ArchiveRules.UnpackDefaultTarget(@"C:\a", @"D:\x", otherPanelOnDisk: true));
        Assert.Equal(@"C:\a\", ArchiveRules.UnpackDefaultTarget(@"C:\a", "::ThisPC", otherPanelOnDisk: false));
        Assert.Equal(@"D:\", ArchiveRules.UnpackDefaultTarget(@"C:\a", @"D:\", otherPanelOnDisk: true));
    }
}
