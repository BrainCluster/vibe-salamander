// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Panels;

namespace VibeSalamander.Core.Tests;

/// <summary>The selection rules (Core.Panels.Marking) and quick search, without a panel.</summary>
public class MarkingTests
{
    private sealed class Row : IMarkable
    {
        public Row(string name, bool directory = false, bool upDir = false, bool drive = false)
        {
            Entry = new FileEntry(name, @"C:\folder\" + name, directory || upDir, 10, DateTime.Now, 0, upDir);
            IsDrive = drive;
        }

        public FileEntry Entry { get; }
        public bool IsDrive { get; }
        public bool IsMarked { get; set; }
        public override string ToString() => Entry.Name;
    }

    /// <summary>"..", two folders, files.</summary>
    private static List<Row> Panel() => new()
    {
        new Row("..", upDir: true),
        new Row("Photos", directory: true),
        new Row("Projects.old", directory: true),
        new Row("a.txt"),
        new Row("b.TXT"),
        new Row("a.jpg"),
        new Row("readme"),
    };

    private static string[] MarkedNames(List<Row> rows) => rows.Where(r => r.IsMarked).Select(r => r.Entry.Name).ToArray();

    [Fact]
    public void Updir_and_drives_are_never_marked()
    {
        var rows = Panel();
        rows.Add(new Row("C:", drive: true));
        Assert.False(Marking.Toggle(rows, 0));
        Assert.False(Marking.Toggle(rows, rows.Count - 1));
        Marking.SetAll(rows, true);
        Marking.SetRange(rows, 0, rows.Count - 1, true);
        Marking.Set(rows, 0, true);
        Assert.False(rows[0].IsMarked);
        Assert.False(rows[^1].IsMarked);
        Assert.Equal(6, MarkedNames(rows).Length);
    }

    [Fact]
    public void Toggle_outside_the_list_does_nothing()
    {
        var rows = Panel();
        Assert.False(Marking.Toggle(rows, -1));
        Assert.False(Marking.Toggle(rows, rows.Count));
        Assert.True(Marking.Toggle(rows, 3));
        Assert.True(rows[3].IsMarked);
        Assert.True(Marking.Toggle(rows, 3));
        Assert.False(rows[3].IsMarked);
    }

    [Fact]
    public void Range_works_in_either_order_and_is_clamped()
    {
        var rows = Panel();
        Marking.SetRange(rows, 4, 2, true);
        Assert.Equal(new[] { "Projects.old", "a.txt", "b.TXT" }, MarkedNames(rows));
        Marking.SetRange(rows, -5, 100, false);
        Assert.Empty(MarkedNames(rows));
        Marking.SetRange(new List<Row>(), 0, 3, true); // an empty panel
    }

    [Fact]
    public void Operation_items_are_the_marks_or_else_the_focused_row()
    {
        var rows = Panel();
        Assert.Equal(new[] { rows[3] }, Marking.OperationItems(rows, rows[3]));
        Assert.Empty(Marking.OperationItems(rows, rows[0])); // ".." isn't something to copy
        Assert.Empty(Marking.OperationItems(rows, null));
        rows[5].IsMarked = rows[6].IsMarked = true;
        Assert.Equal(new[] { rows[5], rows[6] }, Marking.OperationItems(rows, rows[3]));
    }

    [Fact]
    public void Invert_leaves_folders_alone_unless_asked()
    {
        var rows = Panel();
        rows[3].IsMarked = true;
        Marking.Invert(rows, includeDirectories: false);
        Assert.Equal(new[] { "b.TXT", "a.jpg", "readme" }, MarkedNames(rows));
        Marking.Invert(rows, includeDirectories: true);
        Assert.Equal(new[] { "Photos", "Projects.old", "a.txt" }, MarkedNames(rows));
    }

    [Fact]
    public void Mask_marks_files_and_folders_only_when_asked()
    {
        var rows = Panel();
        Assert.Equal(2, Marking.SetByMask(rows, new WildcardMask("*.txt"), includeDirectories: false, mark: true));
        Assert.Equal(new[] { "a.txt", "b.TXT" }, MarkedNames(rows));
        Assert.Equal(0, Marking.SetByMask(rows, new WildcardMask("*.old"), includeDirectories: false, mark: true));
        Assert.Equal(1, Marking.SetByMask(rows, new WildcardMask("*.old"), includeDirectories: true, mark: true));
        Assert.Contains("Projects.old", MarkedNames(rows));
        Marking.SetByMask(rows, new WildcardMask("*.*"), includeDirectories: true, mark: false);
        Assert.Empty(MarkedNames(rows));
    }

    [Fact]
    public void Same_extension_or_name_matches_files_with_files()
    {
        var rows = Panel();
        Marking.SetSame(rows, rows[3], byExtension: true, mark: true); // a.txt: *.txt in any case
        Assert.Equal(new[] { "a.txt", "b.TXT" }, MarkedNames(rows));
        Marking.SetAll(rows, false);
        Marking.SetSame(rows, rows[3], byExtension: false, mark: true); // a.txt: a.*
        Assert.Equal(new[] { "a.txt", "a.jpg" }, MarkedNames(rows));
        Marking.SetAll(rows, false);
        Marking.SetSame(rows, rows[0], byExtension: true, mark: true); // on "..": nothing
        Assert.Empty(MarkedNames(rows));
    }

    [Fact]
    public void Names_are_added_or_set()
    {
        var rows = Panel();
        rows[6].IsMarked = true;
        Marking.AddNames(rows, new[] { "A.TXT", "missing", ".." });
        Assert.Equal(new[] { "a.txt", "readme" }, MarkedNames(rows));
        Assert.Equal(1, Marking.SetNames(rows, new[] { "photos", "missing" }));
        Assert.Equal(new[] { "Photos" }, MarkedNames(rows));
        Assert.Equal(new[] { "Photos" }, Marking.MarkedNames(rows));
    }

    [Fact]
    public void Find_marked_goes_both_ways_and_stops_at_the_ends()
    {
        var rows = Panel();
        rows[2].IsMarked = rows[5].IsMarked = true;
        Assert.Equal(5, Marking.FindMarked(rows, 3, +1));
        Assert.Equal(2, Marking.FindMarked(rows, 3, -1));
        Assert.Equal(-1, Marking.FindMarked(rows, 5, +1));
        Assert.Equal(-1, Marking.FindMarked(rows, 2, -1));
        Assert.Equal(2, Marking.FindMarked(rows, -1, +1)); // no caret yet
    }

    // ---- quick search ------------------------------------------------------------------------------------

    [Fact]
    public void Quick_search_grows_while_names_match()
    {
        var rows = Panel();
        Assert.Equal(new QuickSearchResult(2, "pr"), QuickSearchRules.Type(rows, "p", 'r', 0));
        Assert.Null(QuickSearchRules.Type(rows, "pr", 'x', 2)); // nothing starts with "prx": the key is ignored
        Assert.Equal(new QuickSearchResult(4, "B"), QuickSearchRules.Type(rows, "", 'B', 0)); // any case
    }

    [Fact]
    public void The_same_letter_again_goes_to_the_next_name_with_it()
    {
        var rows = Panel();
        Assert.Equal(new QuickSearchResult(3, "a"), QuickSearchRules.Type(rows, "", 'a', 0));
        Assert.Equal(new QuickSearchResult(5, "a"), QuickSearchRules.Type(rows, "a", 'a', 3)); // a.txt → a.jpg
        Assert.Equal(new QuickSearchResult(3, "a"), QuickSearchRules.Type(rows, "a", 'a', 5)); // wraps to the top
        var withAa = new List<Row> { new("a1"), new("aa"), new("a2") };
        Assert.Equal(new QuickSearchResult(1, "aa"), QuickSearchRules.Type(withAa, "a", 'a', 0)); // a name starts with "aa"
    }

    [Fact]
    public void Backspace_shortens_the_search()
    {
        var rows = Panel();
        Assert.Null(QuickSearchRules.Backspace(rows, "", 3));
        Assert.Equal(new QuickSearchResult(1, "p"), QuickSearchRules.Backspace(rows, "pr", 2));
        Assert.Equal(new QuickSearchResult(4, ""), QuickSearchRules.Backspace(rows, "b", 4)); // empty: the caret stays
    }
}
