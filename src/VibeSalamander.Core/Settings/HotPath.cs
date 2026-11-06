// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Serialization;
using VibeSalamander.Core.Commands;

namespace VibeSalamander.Core.Settings;

/// <summary>
/// A hot path beyond Ctrl+1…9 (Configuration → Hot Paths → More hot paths): a folder, an optional name, and an
/// optional shortcut of its own. They are in the hot path menu (Shift+F9) and on the hot path bar too.
/// </summary>
public sealed class HotPath
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>The shortcut's key (0: none) and modifiers.</summary>
    public int Key { get; set; }

    public KeyModifiers Modifiers { get; set; }

    [JsonIgnore]
    public KeyChord? Shortcut => Key == 0 ? null : new KeyChord(Key, Modifiers);

    /// <summary>
    /// A shortcut a hot path may have: with Ctrl or Alt, or a function key (a letter or Enter alone is typing, quick
    /// search or a panel's own key).
    /// </summary>
    public static bool IsUsableShortcut(KeyChord chord) =>
        chord.Modifiers.HasFlag(KeyModifiers.Ctrl) || chord.Modifiers.HasFlag(KeyModifiers.Alt) ||
        chord.Key is >= VK.F1 and <= VK.F1 + 23;

    /// <summary>The hot path with this shortcut (and a folder), or null.</summary>
    public static HotPath? Find(IEnumerable<HotPath>? hotPaths, KeyChord chord) =>
        hotPaths?.FirstOrDefault(h => h.Key != 0 && h.Shortcut == chord && h.Path.Length > 0);
}
