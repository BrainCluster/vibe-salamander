// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Formatting;
using VibeSalamander.Core.Panels;

namespace VibeSalamander.Core.Tests;

public class PathUtilTests
{
    [Theory]
    [InlineData("c:", @"C:\")]
    [InlineData(@"c:\", @"C:\")]
    [InlineData(@"C:\Windows\", @"C:\Windows")]
    [InlineData(@"c:/Windows/System32", @"C:\Windows\System32")]
    public void NormalizeDirectory(string input, string expected) =>
        Assert.Equal(expected, PathUtil.NormalizeDirectory(input));

    [Fact]
    public void Resolve_Relative() =>
        Assert.Equal(@"C:\Windows\System32", PathUtil.Resolve(@"C:\Windows", "System32"));

    [Fact]
    public void Resolve_RootRelative() =>
        Assert.Equal(@"C:\Users", PathUtil.Resolve(@"C:\Windows\System32", @"\Users"));
}

public class LocalProviderTests
{
    private readonly LocalFileSystemProvider _provider = new();

    [Fact]
    public void Parent_OfDriveRoot_IsThisPc()
    {
        ParentLocation? parent = _provider.GetParent(@"C:\");
        Assert.NotNull(parent);
        Assert.Equal(ThisPcProvider.RootPath, parent!.Path);
        Assert.Equal("C:", parent.FocusName);
    }

    [Fact]
    public void Parent_OfSubdirectory_FocusesChild()
    {
        ParentLocation? parent = _provider.GetParent(@"C:\Windows\System32");
        Assert.Equal(@"C:\Windows", parent!.Path);
        Assert.Equal("System32", parent.FocusName);
    }

    [Fact]
    public async Task List_ReturnsUpDirFilesAndDirectories()
    {
        string dir = Directory.CreateTempSubdirectory("vibe").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            File.WriteAllText(Path.Combine(dir, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(dir, "h.txt"), "x");
            File.SetAttributes(Path.Combine(dir, "h.txt"), FileAttributes.Hidden);

            DirectoryListing listing = await _provider.ListAsync(dir, new ListingOptions(), CancellationToken.None);

            Assert.Null(listing.Error);
            Assert.True(listing.Entries[0].IsUpDir);
            Assert.Contains(listing.Entries, e => e.Name == "sub" && e.IsDirectory);
            FileEntry a = Assert.Single(listing.Entries, e => e.Name == "a.txt");
            Assert.Equal(5, a.Size);
            Assert.Equal("a", a.BaseName);
            Assert.Equal("txt", a.Extension);
            Assert.DoesNotContain(listing.Entries, e => e.Name == "h.txt");
            Assert.Equal(1, listing.HiddenCount);
        }
        finally
        {
            File.SetAttributes(Path.Combine(dir, "h.txt"), FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class ComparerTests
{
    private static FileEntry File(string name, long size = 0) =>
        new(name, @"C:\" + name, false, size, new DateTime(2026, 1, 1), FileAttributes.Archive);

    private static FileEntry Dir(string name) =>
        new(name, @"C:\" + name, true, 0, new DateTime(2026, 1, 1), FileAttributes.Directory);

    [Fact]
    public void UpDirThenDirectoriesThenFiles_InBothDirections()
    {
        var items = new[] { File("b.txt", 5), Dir("zdir"), FileEntry.CreateUpDir(@"C:\"), File("a.txt", 9), Dir("adir") };
        foreach (bool desc in new[] { false, true })
        {
            var sorted = new EntryComparer(SortMode.Size, desc).Sort(items);
            Assert.True(sorted[0].IsUpDir);
            Assert.True(sorted[1].IsDirectory && sorted[2].IsDirectory);
            Assert.False(sorted[3].IsDirectory);
        }
    }

    [Fact]
    public void SizeSort_OrdersFiles()
    {
        var sorted = new EntryComparer(SortMode.Size, false).Sort(new[] { File("big", 100), File("small", 1) });
        Assert.Equal("small", sorted[0].Name);
    }
}

public class FormatTests
{
    [Fact]
    public void Attributes_SalamanderOrder() =>
        Assert.Equal("RHSA", Formats.Attributes(FileAttributes.Archive | FileAttributes.System | FileAttributes.Hidden | FileAttributes.ReadOnly));

    [Fact]
    public void ShortSize_Units() => Assert.EndsWith("GB", Formats.ShortSize(514L * 1024 * 1024 * 1024));
}

public class HistoryTests
{
    [Fact]
    public void BackAndForward()
    {
        var h = new NavigationHistory();
        h.Push(@"C:\a");
        Assert.Equal(@"C:\a", h.GoBack(@"C:\b"));
        Assert.Equal(@"C:\b", h.GoForward(@"C:\a"));
    }
}
