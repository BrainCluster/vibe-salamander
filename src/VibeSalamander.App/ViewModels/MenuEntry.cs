// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.ViewModels;

/// <summary>An item of a command menu shown by the window (user menu, lists of opened files / directories, shares).</summary>
/// <param name="Detail">small text at the right instead of a shortcut (a drive's free space)</param>
/// <param name="Context">the row's right-click menu (its first item is what a click does)</param>
public sealed record MenuEntry(string Text, Action? Invoke = null, string? Glyph = null, string? Shortcut = null,
    bool IsSeparator = false, bool IsEnabled = true, string? Detail = null, IReadOnlyList<MenuEntry>? Context = null)
{
    public static MenuEntry Separator { get; } = new(string.Empty, IsSeparator: true);
    public static MenuEntry Header(string text) => new(text, IsEnabled: false);
}
