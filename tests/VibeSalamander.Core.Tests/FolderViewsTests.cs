// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Panels;

namespace VibeSalamander.Core.Tests;

public sealed class FolderViewsTests
{
    private static FileEntry File(string name, FileAttributes attributes = FileAttributes.Normal) =>
        new(name, @"C:\Photos\" + name, false, 1, DateTime.Now, attributes);

    private static FileEntry Dir(string name) => new(name, @"C:\Photos\" + name, true, 0, DateTime.Now, FileAttributes.Directory);

    [Fact]
    public void Picture_folders_are_mostly_pictures_or_videos()
    {
        Assert.True(FolderViews.IsPictureFolder(new[] { File("a.jpg"), File("b.PNG"), File("c.mp4"), File("notes.txt") }));
        // Thumbs.db / desktop.ini and folders don't count
        Assert.True(FolderViews.IsPictureFolder(new[] { File("a.jpg"), File("Thumbs.db", FileAttributes.Hidden | FileAttributes.System), Dir("2025"), Dir("2026") }));
        Assert.False(FolderViews.IsPictureFolder(new[] { File("a.jpg"), File("b.docx"), File("c.xlsx") }));
        Assert.False(FolderViews.IsPictureFolder(new[] { Dir("only folders") }));
        Assert.False(FolderViews.IsPictureFolder(Array.Empty<FileEntry>()));
    }

    [Fact]
    public void Views_are_remembered_per_folder()
    {
        var views = new Dictionary<string, FolderView>();
        FolderViews.Remember(views, @"C:\Photos\", ViewMode.Tiles);
        Assert.Equal(ViewMode.Tiles, FolderViews.Recall(views, @"c:\photos"));
        Assert.Null(FolderViews.Recall(views, @"C:\Other"));
        FolderViews.Remember(views, @"C:\", ViewMode.Brief);
        Assert.Equal(ViewMode.Brief, FolderViews.Recall(views, @"c:\"));
        Assert.Equal(ViewMode.Brief, FolderViews.Recall(views, @"C:\"));
    }

    [Fact]
    public void The_least_recently_used_folders_are_forgotten()
    {
        var views = new Dictionary<string, FolderView>();
        for (int i = 0; i < FolderViews.MaxRemembered; i++)
            views[FolderViews.Key($@"C:\F{i}")] = new FolderView { View = ViewMode.Icons, Used = new DateTime(2026, 1, 1).AddMinutes(i) };
        FolderViews.Remember(views, @"C:\New", ViewMode.Tiles);
        Assert.True(views.Count <= FolderViews.MaxRemembered);
        Assert.Null(FolderViews.Recall(views, @"C:\F0")); // the oldest went
        Assert.Equal(ViewMode.Icons, FolderViews.Recall(views, $@"C:\F{FolderViews.MaxRemembered - 1}"));
        Assert.Equal(ViewMode.Tiles, FolderViews.Recall(views, @"C:\New"));
    }
}
