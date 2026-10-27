// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Operations;
using VibeSalamander.Core.Panels;
using VibeSalamander.Core.Settings;

namespace VibeSalamander.Core.Tests;

/// <summary>The decisions of the file commands: new names, rename targets, what goes to the Recycle Bin, copy targets.</summary>
public class FileCommandRulesTests
{
    private sealed class Row(string name, bool directory = false) : IMarkable
    {
        public FileEntry Entry { get; } = new(name, @"C:\d\" + name, directory, 1, DateTime.Now, 0);
        public bool IsDrive => false;
        public bool IsMarked { get; set; }
    }

    // ---- new names ---------------------------------------------------------------------------------------

    [Fact]
    public void New_items_get_the_first_free_number()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\d\New Folder", @"C:\d\New Folder (2)" };
        Assert.Equal(@"C:\d\New Folder (3)", NewItemNames.UniquePath(@"C:\d", "New Folder", "", taken.Contains));
        Assert.Equal(@"C:\d\New Text Document.txt", NewItemNames.UniquePath(@"C:\d", "New Text Document", ".txt", taken.Contains));
    }

    // ---- rename ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a.txt")]
    [InlineData("a.txt. . ")] // trailing dots and spaces are dropped: the same name
    public void Empty_or_the_same_name_changes_nothing(string? typed) =>
        Assert.True(RenameRules.Resolve(@"C:\d", "a.txt", typed, isDirectory: false, allowSubdirectories: false).Unchanged);

    [Fact]
    public void A_plain_new_name()
    {
        RenameTarget t = RenameRules.Resolve(@"C:\d", "a.txt", "  b.txt ", isDirectory: false, allowSubdirectories: false);
        Assert.Equal(@"C:\d\b.txt", t.Path);
        Assert.Equal("b.txt", t.FocusName);
        Assert.False(t.CaseOnly);
        Assert.False(t.Nested);
        Assert.Null(t.Problem);
    }

    [Fact]
    public void Only_the_case_changes()
    {
        RenameTarget t = RenameRules.Resolve(@"C:\d", "photos", "Photos", isDirectory: true, allowSubdirectories: false);
        Assert.True(t.CaseOnly);
        Assert.Equal(@"C:\d\Photos", t.Path);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("what?")]
    [InlineData("x|y")]
    public void Characters_windows_forbids_are_a_problem(string typed)
    {
        RenameTarget t = RenameRules.Resolve(@"C:\d", "a.txt", typed, isDirectory: false, allowSubdirectories: false);
        Assert.NotNull(t.Problem);
        Assert.False(t.Unchanged);
    }

    [Fact]
    public void A_new_folder_may_be_a_path()
    {
        RenameTarget t = RenameRules.Resolve(@"C:\d", "New Folder", @"a\b\c", isDirectory: true, allowSubdirectories: true);
        Assert.True(t.Nested);
        Assert.Equal(@"C:\d\a\b\c", t.Path);
        Assert.Equal("a", t.FocusName); // the first new directory is focused
        // a file, or a rename (no subdirectories allowed): a backslash isn't allowed
        Assert.NotNull(RenameRules.Resolve(@"C:\d", "New Folder", @"a\b", isDirectory: true, allowSubdirectories: false).Problem);
        Assert.NotNull(RenameRules.Resolve(@"C:\d", "x.txt", @"a\b", isDirectory: false, allowSubdirectories: true).Problem);
    }

    // ---- delete ------------------------------------------------------------------------------------------

    private static readonly Row[] Items = { new("photos", directory: true), new("a.tmp"), new("b.doc") };

    private static string[] Names(IEnumerable<Row> rows) => rows.Select(r => r.Entry.Name).ToArray();

    [Fact]
    public void Shift_del_and_permanent_mode_delete_everything()
    {
        var (recycle, remove) = DeletePlan.Split(Items, permanent: true, DeleteMode.RecycleAll, "*.*");
        Assert.Empty(recycle);
        Assert.Equal(3, remove.Count);
        (recycle, remove) = DeletePlan.Split(Items, permanent: false, DeleteMode.DeletePermanently, "*.*");
        Assert.Empty(recycle);
        Assert.Equal(3, remove.Count);
    }

    [Fact]
    public void Recycle_all_sends_everything_to_the_bin()
    {
        var (recycle, remove) = DeletePlan.Split(Items, permanent: false, DeleteMode.RecycleAll, "");
        Assert.Equal(3, recycle.Count);
        Assert.Empty(remove);
    }

    [Fact]
    public void Matching_files_and_all_folders_go_to_the_bin()
    {
        var (recycle, remove) = DeletePlan.Split(Items, permanent: false, DeleteMode.RecycleMatching, "*.doc");
        Assert.Equal(new[] { "photos", "b.doc" }, Names(recycle));
        Assert.Equal(new[] { "a.tmp" }, Names(remove));
    }

    // ---- copy targets ------------------------------------------------------------------------------------

    [Fact]
    public void The_suggested_target_is_the_other_panel()
    {
        Assert.Equal(@"D:\x\", CopyTargetRules.DefaultTarget(@"D:\x", otherPanelUsable: true));
        Assert.Equal(@"D:\", CopyTargetRules.DefaultTarget(@"D:\", otherPanelUsable: true));
        Assert.Equal("", CopyTargetRules.DefaultTarget(@"D:\x", otherPanelUsable: false)); // This PC
    }

    [Theory]
    [InlineData(true, true, @"C:\a", @"D:\b", true)]     // the setting asks
    [InlineData(false, true, @"C:\a", @"D:\b", false)]   // straight into the other panel
    [InlineData(false, false, @"C:\a", @"D:\b", true)]   // the other panel is This PC: ask anyway
    [InlineData(false, true, @"C:\a", @"c:\A\", true)]   // the same directory: ask anyway
    public void When_the_target_is_asked(bool askSetting, bool otherUsable, string active, string other, bool asks) =>
        Assert.Equal(asks, CopyTargetRules.AskForTarget(askSetting, otherUsable, active, other));

    [Theory]
    [InlineData(@"D:\new\", 1, false, true)]   // "into" a directory that isn't there
    [InlineData(@"D:\new", 2, false, true)]    // several items to one name
    [InlineData(@"D:\copy.txt", 1, false, false)] // one item under a new name
    [InlineData(@"D:\new\", 1, true, false)]   // it exists already
    public void When_a_new_directory_is_made(string target, int count, bool exists, bool intoNew) =>
        Assert.Equal(intoNew, CopyTargetRules.IntoNewDirectory(target, count, exists));
}
