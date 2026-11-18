// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.Panels;
using VibeSalamander.Core.Settings;

namespace VibeSalamander.Core.Tests;

/// <summary>The tabs of a panel: where new tabs go, which one shows after a close, reopening, moving, titles.</summary>
public sealed class PanelTabsTests
{
    private static PanelTab T(string path) => new() { Path = path };

    private static TabList List(int active, params string[] paths) => new(paths.Select(T), active, T(@"C:\"));

    private static string[] Paths(TabList tabs) => tabs.Tabs.Select(t => t.Path).ToArray();

    [Fact]
    public void Without_saved_tabs_there_is_one_and_a_bad_index_is_clamped()
    {
        var none = new TabList(null, 3, T(@"D:\work"));
        var clamped = List(7, "a", "b");

        Assert.Equal(new[] { @"D:\work" }, Paths(none));
        Assert.Equal(0, none.ActiveIndex);
        Assert.Equal(1, clamped.ActiveIndex);
    }

    [Fact]
    public void A_new_tab_goes_right_after_the_shown_one_and_shows()
    {
        var tabs = List(0, "a", "b");

        int at = tabs.Insert(T("new"));

        Assert.Equal(1, at);
        Assert.Equal(new[] { "a", "new", "b" }, Paths(tabs));
        Assert.Equal("new", tabs.Active.Path);
    }

    [Fact]
    public void A_tab_opened_in_the_background_keeps_the_shown_one()
    {
        var tabs = List(1, "a", "b");

        tabs.Insert(T("bg"), 0, activate: false);

        Assert.Equal(new[] { "bg", "a", "b" }, Paths(tabs));
        Assert.Equal("b", tabs.Active.Path);
    }

    [Fact]
    public void Closing_the_shown_tab_shows_the_one_to_its_right_or_at_the_end_the_one_to_its_left()
    {
        var middle = List(1, "a", "b", "c");
        var last = List(2, "a", "b", "c");
        var before = List(2, "a", "b", "c");

        Assert.True(middle.Close(1));
        Assert.True(last.Close(2));
        Assert.True(before.Close(0));

        Assert.Equal("c", middle.Active.Path);
        Assert.Equal("b", last.Active.Path);
        Assert.Equal("c", before.Active.Path); // a tab before it closed: the same tab stays shown
    }

    [Fact]
    public void The_last_tab_stays()
    {
        var tabs = List(0, "a");

        Assert.False(tabs.Close(0));
        Assert.False(tabs.Remove(0));
        Assert.Equal(new[] { "a" }, Paths(tabs));
    }

    [Fact]
    public void A_closed_tab_reopens_where_it_was_newest_first()
    {
        var tabs = List(0, "a", "b", "c", "d");
        tabs.Close(1);
        tabs.Close(2); // "d", now at 2

        Assert.Equal("d", tabs.Reopen()!.Path);
        Assert.Equal("b", tabs.Reopen()!.Path);
        Assert.Null(tabs.Reopen());
        Assert.Equal(new[] { "a", "b", "c", "d" }, Paths(tabs));
        Assert.Equal("b", tabs.Active.Path);
    }

    [Fact]
    public void Close_others_keeps_one_and_they_can_come_back()
    {
        var tabs = List(0, "a", "b", "c");

        tabs.CloseOthers(1);

        Assert.Equal(new[] { "b" }, Paths(tabs));
        Assert.True(tabs.CanReopen);
    }

    [Fact]
    public void Moving_a_tab_keeps_the_shown_tab_shown()
    {
        var tabs = List(1, "a", "b", "c");

        tabs.Move(0, 2);

        Assert.Equal(new[] { "b", "c", "a" }, Paths(tabs));
        Assert.Equal("b", tabs.Active.Path);
    }

    [Fact]
    public void Next_and_previous_wrap_around()
    {
        var first = List(0, "a", "b", "c");
        var last = List(2, "a", "b", "c");

        Assert.Equal(1, first.Next);
        Assert.Equal(2, first.Previous);
        Assert.Equal(0, last.Next);
    }

    [Fact]
    public void Swapping_sides_exchanges_all_tabs_and_which_is_shown()
    {
        var left = List(1, "a", "b");
        var right = List(0, "x");

        left.SwapWith(right);

        Assert.Equal(new[] { "x" }, Paths(left));
        Assert.Equal(new[] { "a", "b" }, Paths(right));
        Assert.Equal("b", right.Active.Path);
    }

    [Fact]
    public void A_new_tab_from_here_has_the_place_and_view_but_isnt_locked_or_marked()
    {
        var tab = new PanelTab
        {
            Path = @"C:\a", SortMode = SortMode.Size, SortDescending = true, ViewMode = ViewMode.Brief, Locked = true,
            Marked = new List<string> { "x" }, Back = new List<string> { @"C:\" }, FocusName = "x",
        };

        PanelTab copy = tab.NewFromHere();

        Assert.Equal((@"C:\a", SortMode.Size, true, ViewMode.Brief), (copy.Path, copy.SortMode, copy.SortDescending, copy.ViewMode));
        Assert.False(copy.Locked);
        Assert.Empty(copy.Marked);
        Assert.Equal(new[] { @"C:\" }, copy.Back);
        copy.Back.Add("y");
        Assert.Single(tab.Back); // its own history
    }

    [Theory]
    [InlineData(@"C:\", "C:")]
    [InlineData(@"d:", "D:")]
    [InlineData(@"C:\Users\Ana\Documents\", "Documents")]
    [InlineData(@"\\server\share", "share")]
    [InlineData(@"C:\backup\photos.zip", "photos.zip")]
    [InlineData(@"C:\backup\photos.zip\2024", "2024")]
    [InlineData("thispc:", "This PC")]
    [InlineData("sftp://ana@example.org", "example.org")]
    [InlineData("ftp://ana%40mail.com@example.org:2121/", "example.org:2121")]
    [InlineData("sftp://ana@example.org/var/www", "www")]
    [InlineData("sftp://ana@example.org/my%20site", "my site")]
    public void Tab_titles(string path, string title) => Assert.Equal(title, PanelTab.Title(path));

    [Fact]
    public void Tabs_are_saved_with_place_sort_view_and_lock_only()
    {
        var settings = new AppSettings();
        settings.Left.Tabs = new List<PanelTab>
        {
            new() { Path = @"C:\a", SortMode = SortMode.Date, Locked = true, Marked = new List<string> { "m" }, Filter = "*.txt" },
            new() { Path = @"D:\b" },
        };
        settings.Left.ActiveTab = 1;
        string file = Path.Combine(Path.GetTempPath(), "vibe-tabs-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            SettingsStore.Save(settings, file);
            AppSettings loaded = SettingsStore.Load(file, out _);

            Assert.Equal(new[] { @"C:\a", @"D:\b" }, loaded.Left.Tabs!.Select(t => t.Path));
            Assert.Equal(1, loaded.Left.ActiveTab);
            Assert.Equal(SortMode.Date, loaded.Left.Tabs![0].SortMode);
            Assert.True(loaded.Left.Tabs[0].Locked);
            Assert.Empty(loaded.Left.Tabs[0].Marked); // marks and filters are for the session only
            Assert.Equal(string.Empty, loaded.Left.Tabs[0].Filter);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
