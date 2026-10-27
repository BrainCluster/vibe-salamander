// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.Panels;

namespace VibeSalamander.Core.Tests;

/// <summary>The address bar (Core.Panels.BreadcrumbRules).</summary>
public class BreadcrumbRulesTests
{
    private static string? Caption(string drive) => drive == "C:" ? "System (C:)" : null;

    [Fact]
    public void A_folder_on_a_drive()
    {
        var segments = BreadcrumbRules.DiskSegments(@"c:\Users\me\Documents", Caption);
        Assert.Equal(new[] { "System (C:)", "Users", "me", "Documents" }, segments.Select(s => s.Label));
        Assert.Equal(new[] { @"C:\", @"C:\Users", @"C:\Users\me", @"C:\Users\me\Documents" }, segments.Select(s => s.Path));
    }

    [Fact]
    public void A_drive_without_a_caption_shows_its_letter()
    {
        var segments = BreadcrumbRules.DiskSegments(@"D:\", Caption);
        Assert.Equal(("D:", @"D:\"), Assert.Single(segments));
    }

    [Fact]
    public void A_network_share()
    {
        var segments = BreadcrumbRules.DiskSegments(@"\\server\share\docs", Caption);
        Assert.Equal(new[] { @"\\server\share", "docs" }, segments.Select(s => s.Label));
        Assert.Equal(@"\\server\share\docs", segments[^1].Path);
    }

    [Fact]
    public void Inside_an_archive_the_archive_is_a_folder()
    {
        var segments = BreadcrumbRules.DiskSegments(@"C:\a\photos.zip\2024", Caption);
        Assert.Equal(new[] { "System (C:)", "a", "photos.zip", "2024" }, segments.Select(s => s.Label));
    }

    [Theory]
    [InlineData(new double[] { 50, 50, 50 }, 200, 30, 0)]   // fits
    [InlineData(new double[] { 50, 50, 50 }, 149, 30, 1)]   // too wide: one goes into "..."
    [InlineData(new double[] { 50, 50, 50 }, 90, 30, 2)]    // the current folder always stays
    [InlineData(new double[] { 50, 50, 50 }, 10, 30, 2)]
    [InlineData(new double[] { 400 }, 100, 30, 0)]          // only the current folder: it stays (trimmed)
    public void Leading_locations_move_into_the_menu(double[] widths, double available, double overflow, int hidden) =>
        Assert.Equal(hidden, BreadcrumbRules.HiddenCount(widths, available, overflow));

    [Theory]
    [InlineData(@"C:\a", @"C:\a\b\c", "b")]
    [InlineData(@"C:\a\", @"C:\A\b", "b")]
    [InlineData(@"C:\", @"C:\Users\me", "Users")]
    [InlineData(@"C:\a", @"C:\a", null)]
    [InlineData(@"C:\a", @"C:\ab\c", null)]
    [InlineData(@"C:\x", @"C:\a\b", null)]
    public void Going_up_focuses_the_folder_we_came_from(string ancestor, string path, string? child) =>
        Assert.Equal(child, BreadcrumbRules.ChildOnTheWay(ancestor, path));

    [Theory]
    [InlineData("This PC", "Ovo računalo", true)]
    [InlineData("  this pc ", "Ovo računalo", true)]
    [InlineData("ovo RAČUNALO", "Ovo računalo", true)]
    [InlineData(@"C:\", "Ovo računalo", false)]
    public void Typing_this_pc(string typed, string localized, bool means) =>
        Assert.Equal(means, BreadcrumbRules.MeansThisPc(typed, localized));
}
