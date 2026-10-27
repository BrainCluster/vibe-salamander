// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Panels;

namespace VibeSalamander.Core.Tests;

/// <summary>Rectangle selection and drag and drop rules (Core.Panels.MarqueeRules, DropRules).</summary>
public class PointerRulesTests
{
    private sealed class Row(string name, bool upDir = false, bool drive = false) : IMarkable
    {
        public FileEntry Entry { get; } = new(name, @"C:\folder\" + name, upDir, 1, DateTime.Now, 0, upDir);
        public bool IsDrive { get; } = drive;
        public bool IsMarked { get; set; }
    }

    // ---- rectangle ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(3.9, -3.9, false)]
    [InlineData(4, 0, true)]
    [InlineData(0, -4, true)]
    public void A_rectangle_starts_after_a_few_pixels(double dx, double dy, bool started) =>
        Assert.Equal(started, MarqueeRules.HasStarted(dx, dy));

    [Theory]
    [InlineData(true, false, false, true)]   // inside: marked
    [InlineData(true, false, true, true)]
    [InlineData(false, false, true, true)]   // outside: as it was
    [InlineData(false, false, false, false)]
    [InlineData(true, true, true, false)]    // Ctrl, inside: flipped
    [InlineData(true, true, false, true)]
    [InlineData(false, true, true, true)]    // Ctrl, outside: as it was
    public void Rows_inside_are_marked_or_flipped(bool inside, bool toggle, bool markedAtStart, bool marked) =>
        Assert.Equal(marked, MarqueeRules.MarkFor(inside, toggle, markedAtStart));

    [Fact]
    public void Overlap_needs_a_shared_area()
    {
        Assert.True(MarqueeRules.Overlaps(0, 0, 10, 10, 5, 5, 20, 20));
        Assert.False(MarqueeRules.Overlaps(0, 0, 10, 10, 10, 0, 20, 10)); // edges only touch
        Assert.True(MarqueeRules.Overlaps(0, 0, 100, 20, 50, -5, 50.5, 30)); // a thin rectangle through a row
    }

    [Theory]
    [InlineData(100, 500, 0)]
    [InlineData(12, 500, 0)]
    [InlineData(11, 500, -5)]
    [InlineData(-100, 500, -40)]   // far beyond: the most per step
    [InlineData(488, 500, 0)]
    [InlineData(490, 500, 6)]
    [InlineData(900, 500, 40)]
    public void The_list_scrolls_near_and_beyond_its_edges(double position, double length, double step) =>
        Assert.Equal(step, MarqueeRules.AutoScrollStep(position, length));

    // ---- drag and drop -----------------------------------------------------------------------------------

    [Fact]
    public void A_marked_row_drags_the_whole_selection()
    {
        var a = new Row("a.txt");
        var b = new Row("b.txt") { IsMarked = true };
        var c = new Row("c.txt") { IsMarked = true };
        Assert.Equal(new[] { @"C:\folder\a.txt" }, DropRules.DraggedPaths(new[] { a }, new[] { b, c }));
        Assert.Equal(new[] { @"C:\folder\b.txt", @"C:\folder\c.txt" }, DropRules.DraggedPaths(new[] { b }, new[] { b, c }));
        Assert.Empty(DropRules.DraggedPaths(new[] { new Row("..", upDir: true), new Row("D:", drive: true) }, Array.Empty<Row>()));
    }

    [Fact]
    public void Short_drags_from_a_panel_are_accidents()
    {
        Assert.True(DropRules.IsTooShort(300, fromAPanel: true, TimeSpan.FromMilliseconds(100)));
        Assert.False(DropRules.IsTooShort(300, fromAPanel: true, TimeSpan.FromMilliseconds(400)));
        Assert.False(DropRules.IsTooShort(300, fromAPanel: false, TimeSpan.FromMilliseconds(100))); // from Explorer
        Assert.False(DropRules.IsTooShort(0, fromAPanel: true, TimeSpan.Zero)); // turned off
    }

    [Theory]
    [InlineData(false, false, false, @"C:\a\x.txt", @"C:\b", true)]   // same drive: move
    [InlineData(false, false, false, @"C:\a\x.txt", @"D:\b", false)]  // another drive: copy
    [InlineData(false, false, false, @"c:\a\x.txt", @"C:\b", true)]   // drive letters in any case
    [InlineData(true, false, false, @"C:\a\x.txt", @"D:\b", true)]    // Shift: move
    [InlineData(false, true, false, @"C:\a\x.txt", @"C:\b", false)]   // Ctrl: copy
    [InlineData(true, true, false, @"C:\a\x.txt", @"C:\b", true)]     // Shift wins
    [InlineData(false, false, true, @"C:\a\x.txt", @"C:\b", false)]   // into an archive or onto a server: copy
    [InlineData(true, false, true, @"C:\a\x.txt", @"C:\b", true)]
    [InlineData(false, false, false, null, @"C:\b", false)]           // from another program: copy
    public void Copy_or_move_follows_explorer(bool shift, bool ctrl, bool intoArchiveOrServer, string? source, string target, bool move) =>
        Assert.Equal(move, DropRules.IsMove(shift, ctrl, intoArchiveOrServer, source, target));

    [Fact]
    public void Dropping_where_the_items_are_does_nothing()
    {
        Assert.True(DropRules.ChangesNothing(Array.Empty<string>(), @"C:\a"));
        Assert.True(DropRules.ChangesNothing(new[] { @"C:\a\x.txt", @"c:\A\y.txt" }, @"C:\a"));
        Assert.False(DropRules.ChangesNothing(new[] { @"C:\a\x.txt", @"C:\b\y.txt" }, @"C:\a"));
    }
}
