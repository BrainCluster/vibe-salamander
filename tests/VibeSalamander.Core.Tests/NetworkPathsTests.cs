// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.FileSystem;

namespace VibeSalamander.Core.Tests;

/// <summary>Copy network path as text (Ctrl+Shift+Ins): a mapped drive becomes \\server\share.</summary>
public sealed class NetworkPathsTests
{
    private static string? Mapped(string root) => root == @"Z:\" ? @"\\fileserver\team" : null;

    [Theory]
    [InlineData(@"Z:\Reports\q3.xlsx", @"\\fileserver\team\Reports\q3.xlsx")]
    [InlineData(@"z:\Reports", @"\\fileserver\team\Reports")]
    [InlineData(@"Z:\", @"\\fileserver\team")]
    [InlineData(@"C:\Users\Ana\a.txt", @"C:\Users\Ana\a.txt")]
    [InlineData(@"\\other\share\b.txt", @"\\other\share\b.txt")]
    [InlineData("sftp://ana@example.org/x", "sftp://ana@example.org/x")]
    public void Mapped_drives_become_network_paths(string path, string expected) =>
        Assert.Equal(expected, NetworkPaths.ToUnc(path, Mapped));
}
