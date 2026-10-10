using VibeSalamander.Core.Formatting;
using VibeSalamander.Core.Panels;
using VibeSalamander.Core.Settings;
using VibeSalamander.Core.Tools;

namespace VibeSalamander.Core.Tests;

public class ConfigurationTests
{
    [Theory]
    [InlineData("file2", "file10", -1)]
    [InlineData("file10", "file2", 1)]
    [InlineData("a", "a", 0)]
    [InlineData("img007", "img7", 0)]
    [InlineData("abc", "abd", -1)]
    [InlineData("x1", "x1y", -1)]
    public void NaturalCompare_orders_numbers_by_value(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(EntryComparer.NaturalCompare(a, b, StringComparer.OrdinalIgnoreCase)));

    [Theory]
    [InlineData("README.TXT", false, NameCasing.MixedIfDos, "Readme.txt")]
    [InlineData("ReadMe.txt", false, NameCasing.MixedIfDos, "ReadMe.txt")]
    [InlineData("Docs", true, NameCasing.LowerFilesUpperDirectories, "DOCS")]
    [InlineData("Notes.TXT", false, NameCasing.LowerFilesUpperDirectories, "notes.txt")]
    [InlineData("hello WORLD.Md", false, NameCasing.PartiallyMixed, "Hello world.md")]
    [InlineData("Abc", false, NameCasing.Upper, "ABC")]
    [InlineData("Abc", false, NameCasing.AsStored, "Abc")]
    [InlineData("..", true, NameCasing.Upper, "..")]
    public void ApplyCasing(string name, bool isDirectory, NameCasing casing, string expected) =>
        Assert.Equal(expected, Formats.ApplyCasing(name, isDirectory, casing));

    [Fact]
    public void Size_in_kilobytes_rounds_up()
    {
        Assert.EndsWith(" KB", Formats.Size(1, SizeFormat.KiloBytes));
        Assert.StartsWith("1", Formats.Size(1, SizeFormat.KiloBytes));
        Assert.StartsWith("2", Formats.Size(1025, SizeFormat.KiloBytes));
    }

    [Fact]
    public void HighlightRule_checks_masks_and_attributes()
    {
        var rule = new HighlightRule { Masks = "*.exe", Directory = false, Hidden = true };
        Assert.True(rule.Matches("a.exe", FileAttributes.Hidden, false));
        Assert.False(rule.Matches("a.exe", FileAttributes.Normal, false));
        Assert.False(rule.Matches("a.exe", FileAttributes.Hidden, true));
        Assert.False(rule.Matches("a.txt", FileAttributes.Hidden, false));
        rule.Masks = "*.txt";
        Assert.True(rule.Matches("a.txt", FileAttributes.Hidden, false));
    }

    [Fact]
    public void CompareOptions_SameTime_honors_tolerance_and_dst()
    {
        var t = new DateTime(2026, 1, 1, 12, 0, 0);
        var options = new CompareOptions { ToleranceSeconds = 2 };
        Assert.True(options.SameTime(t, t.AddSeconds(2)));
        Assert.False(options.SameTime(t, t.AddSeconds(3)));
        Assert.False(options.SameTime(t, t.AddHours(1)));
        options.IgnoreDstHours = true;
        Assert.True(options.SameTime(t, t.AddHours(1).AddSeconds(1)));
        Assert.False(options.SameTime(t, t.AddHours(3)));
    }
}
