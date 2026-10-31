// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.Panels;
using Xunit;

namespace VibeSalamander.Core.Tests;

public class FileIconsTests
{
    [Fact]
    public void Kinds_come_from_the_extension()
    {
        Assert.Equal(FileKind.Picture, FileIcons.KindOf("JPG"));
        Assert.Equal(FileKind.Video, FileIcons.KindOf(".mkv"));
        Assert.Equal(FileKind.Archive, FileIcons.KindOf("7z"));
        Assert.Equal(FileKind.Code, FileIcons.KindOf("cs"));
        Assert.Equal(FileKind.Pdf, FileIcons.KindOf("pdf"));
        Assert.Equal(FileKind.Other, FileIcons.KindOf("xyz"));
        Assert.Equal(FileKind.Other, FileIcons.KindOf(""));
        // every kind has an icon, and the plain ones take the text color
        foreach (FileKind kind in Enum.GetValues<FileKind>())
            Assert.False(string.IsNullOrEmpty(FileIcons.Glyph(kind)));
        Assert.Null(FileIcons.Color(FileKind.Other));
        Assert.NotNull(FileIcons.Color(FileKind.Picture));
    }
}
