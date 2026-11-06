// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.Commands;
using VibeSalamander.Core.Settings;
using Xunit;

namespace VibeSalamander.Core.Tests;

public class HotPathTests
{
    [Fact]
    public void A_hot_path_is_found_by_its_own_shortcut()
    {
        var list = new List<HotPath>
        {
            new() { Path = @"C:\Projects", Key = VK.P, Modifiers = KeyModifiers.Ctrl | KeyModifiers.Alt },
            new() { Path = "", Key = VK.Q, Modifiers = KeyModifiers.Ctrl | KeyModifiers.Alt }, // no folder yet
            new() { Path = @"D:\Music" },                                                       // no shortcut
        };
        Assert.Equal(@"C:\Projects", HotPath.Find(list, new KeyChord(VK.P, KeyModifiers.Ctrl | KeyModifiers.Alt))?.Path);
        Assert.Null(HotPath.Find(list, new KeyChord(VK.P, KeyModifiers.Ctrl)));
        Assert.Null(HotPath.Find(list, new KeyChord(VK.Q, KeyModifiers.Ctrl | KeyModifiers.Alt)));
        Assert.Null(HotPath.Find(null, new KeyChord(VK.P, KeyModifiers.Ctrl)));
    }

    [Fact]
    public void A_shortcut_needs_Ctrl_or_Alt_or_a_function_key()
    {
        Assert.True(HotPath.IsUsableShortcut(new KeyChord(VK.P, KeyModifiers.Ctrl)));
        Assert.True(HotPath.IsUsableShortcut(new KeyChord(VK.P, KeyModifiers.Alt | KeyModifiers.Shift)));
        Assert.True(HotPath.IsUsableShortcut(new KeyChord(VK.F11, KeyModifiers.None)));
        Assert.False(HotPath.IsUsableShortcut(new KeyChord(VK.P, KeyModifiers.None)));
        Assert.False(HotPath.IsUsableShortcut(new KeyChord(VK.P, KeyModifiers.Shift)));
        Assert.False(HotPath.IsUsableShortcut(new KeyChord(VK.Enter, KeyModifiers.None)));
    }
}
