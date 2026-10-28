// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Core.Commands;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
}

/// <summary>A key plus modifiers. <see cref="Key"/> uses Win32 virtual-key codes (same values as WinRT VirtualKey).</summary>
public readonly record struct KeyChord(int Key, KeyModifiers Modifiers);

/// <summary>Virtual-key codes used by the default map.</summary>
public static class VK
{
    public const int Back = 0x08, Tab = 0x09, Enter = 0x0D, Space = 0x20, PageUp = 0x21, PageDown = 0x22;
    public const int Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28, Insert = 0x2D, Delete = 0x2E;
    public const int A = 0x41, C = 0x43, D = 0x44, E = 0x45, F = 0x46, H = 0x48, L = 0x4C, M = 0x4D, N = 0x4E, P = 0x50, R = 0x52, S = 0x53, T = 0x54,
                     U = 0x55, V = 0x56, W = 0x57, X = 0x58;
    public const int Backslash = 0xDC;
    public const int Apps = 0x5D;
    public const int Escape = 0x1B, Home = 0x24, End = 0x23, NumPad0 = 0x60;
    public const int Multiply = 0x6A, Add = 0x6B, Subtract = 0x6D, Divide = 0x6F;
    public const int F1 = 0x70, F2 = 0x71, F3 = 0x72, F4 = 0x73, F5 = 0x74, F6 = 0x75, F7 = 0x76,
                     F8 = 0x77, F9 = 0x78, F10 = 0x79, F11 = 0x7A, F12 = 0x7B;
    public const int Period = 0xBE;
    public const int D0 = 0x30, D1 = 0x31, D2 = 0x32, D3 = 0x33, D4 = 0x34, D5 = 0x35;
}

/// <summary>A user change to the default key map (Options → Configuration → Keyboard).</summary>
public sealed class KeyBindingOverride
{
    public int Key { get; set; }
    public KeyModifiers Modifiers { get; set; }
    /// <summary>The command, or None when a default shortcut was removed.</summary>
    public CommandId Command { get; set; }
}

/// <summary>Whose shortcuts the key map starts from (Configuration → Keyboard).</summary>
public enum KeyScheme
{
    /// <summary>Altap / Open Salamander.</summary>
    Salamander,

    /// <summary>Total Commander (Ghisler).</summary>
    TotalCommander,
}

/// <summary>Keyboard shortcuts → commands. Defaults match Altap/Open Salamander, or Total Commander.</summary>
public sealed class KeyMap
{
    private readonly Dictionary<KeyChord, CommandId> _map = new();

    public static KeyMap CreateSalamanderDefaults()
    {
        var m = new KeyMap();
        const KeyModifiers N = KeyModifiers.None, C = KeyModifiers.Ctrl, S = KeyModifiers.Shift, A = KeyModifiers.Alt;

        m.Bind(VK.F1, A, CommandId.ChangeDriveLeft);
        m.Bind(VK.F2, A, CommandId.ChangeDriveRight);
        m.Bind(VK.Left, A, CommandId.GoBack);
        m.Bind(VK.Right, A, CommandId.GoForward);
        m.Bind(VK.Back, N, CommandId.ParentDirectory);
        m.Bind(VK.Back, S, CommandId.RootDirectory);
        m.Bind(VK.PageUp, C, CommandId.ParentDirectory);
        m.Bind(VK.PageDown, C, CommandId.Open);
        m.Bind(VK.Period, C, CommandId.PathFromOtherPanel);
        m.Bind(VK.Left, C, CommandId.OpenInOtherPanel);
        m.Bind(VK.Right, C, CommandId.OpenInOtherPanel);
        m.Bind(VK.F9, C, CommandId.Refresh);
        m.Bind(VK.R, C, CommandId.Refresh);
        m.Bind(VK.Tab, N, CommandId.SwitchPanel);
        m.Bind(VK.U, C, CommandId.SwapPanels);
        m.Bind(VK.F11, C, CommandId.MaximizePanel);
        m.Bind(VK.F3, C, CommandId.SortByName);
        m.Bind(VK.F4, C, CommandId.SortByExtension);
        m.Bind(VK.F5, C, CommandId.SortByDate);
        m.Bind(VK.F6, C, CommandId.SortBySize);
        m.Bind(VK.H, C, CommandId.ToggleHiddenFiles);

        m.Bind(VK.Enter, N, CommandId.Open);
        m.Bind(VK.F2, N, CommandId.QuickRename);
        m.Bind(VK.F3, N, CommandId.View);
        m.Bind(VK.F4, N, CommandId.Edit);
        m.Bind(VK.F4, S, CommandId.EditNewFile);
        m.Bind(VK.F5, N, CommandId.Copy);
        m.Bind(VK.F6, N, CommandId.Move);
        m.Bind(VK.F7, N, CommandId.CreateDirectory);
        m.Bind(VK.F7, S, CommandId.ChangeDirectory);
        m.Bind(VK.F8, N, CommandId.Delete);
        m.Bind(VK.Delete, N, CommandId.Delete);
        m.Bind(VK.Delete, S, CommandId.DeletePermanently);
        m.Bind(VK.Enter, A, CommandId.Properties);
        m.Bind(VK.F3, S, CommandId.OpenActiveFolder);
        m.Bind(VK.Divide, N, CommandId.CommandShell);

        m.Bind(VK.Insert, N, CommandId.ToggleMarkAndMoveDown);
        m.Bind(VK.Space, N, CommandId.ToggleMark);
        m.Bind(VK.A, C, CommandId.SelectAll);
        m.Bind(VK.D, C, CommandId.UnselectAll);
        m.Bind(VK.Multiply, N, CommandId.InvertSelection);
        m.Bind(VK.Add, N, CommandId.SelectByMask);
        m.Bind(VK.Subtract, N, CommandId.UnselectByMask);
        m.Bind(VK.W, C, CommandId.RestoreSelection);
        m.Bind(VK.Add, S, CommandId.SelectSameExtension);
        m.Bind(VK.Subtract, S, CommandId.UnselectSameExtension);
        m.Bind(VK.Add, C | S, CommandId.SelectSameName);
        m.Bind(VK.Subtract, C | S, CommandId.UnselectSameName);
        m.Bind(VK.Up, A, CommandId.GoToPreviousSelected);
        m.Bind(VK.Down, A, CommandId.GoToNextSelected);

        m.Bind(VK.X, C, CommandId.ClipboardCut);
        m.Bind(VK.C, C, CommandId.ClipboardCopy);
        m.Bind(VK.V, C, CommandId.ClipboardPaste);

        m.Bind(VK.F12, C, CommandId.Filter);
        m.Bind(VK.F3, A, CommandId.OpenWith);
        m.Bind(VK.F3, C | S, CommandId.OpenWith);
        m.Bind(VK.F2, C, CommandId.ChangeAttributes);
        m.Bind(VK.F7, C, CommandId.ChangeCase);
        m.Bind(VK.T, C, CommandId.GoToShortcutTarget);
        m.Bind(VK.F7, A, CommandId.FindFiles);
        m.Bind(VK.F7, A | S, CommandId.FindDuplicates);
        m.Bind(VK.C, C | S, CommandId.CompareFiles);
        m.Bind(VK.F10, S, CommandId.ContextMenu);
        m.Bind(VK.Apps, N, CommandId.ContextMenu);

        m.Bind(VK.Insert, A, CommandId.CopyFullNameAsText);
        m.Bind(VK.Insert, A | S, CommandId.CopyNameAsText);
        m.Bind(VK.Insert, C | A, CommandId.CopyPathAsText);

        m.Bind(VK.F1, N, CommandId.Help);

        // phase 3
        m.Bind(VK.D1, A, CommandId.ViewBrief);
        m.Bind(VK.D2, A, CommandId.ViewDetailed);
        m.Bind(VK.D3, A, CommandId.ViewIcons);
        m.Bind(VK.D4, A, CommandId.ViewThumbnails);
        m.Bind(VK.D5, A, CommandId.ViewTiles);
        m.Bind(VK.F10, A, CommandId.CalculateOccupiedSpace);
        m.Bind(VK.F10, C | S, CommandId.CalculateDirectorySizes);
        m.Bind(VK.F1, C, CommandId.DriveInformation);
        m.Bind(VK.F10, C, CommandId.CompareDirectories);
        m.Bind(VK.F9, N, CommandId.UserMenu);
        m.Bind(VK.F5, C | S, CommandId.SaveSelection);
        m.Bind(VK.F6, C | S, CommandId.LoadSelection);
        m.Bind(VK.M, C, CommandId.MakeFileList);
        m.Bind(VK.S, C, CommandId.PasteShortcut);
        m.Bind(VK.P, C, CommandId.Security);
        m.Bind(VK.E, C, CommandId.Email);
        m.Bind(VK.F11, A, CommandId.OpenedFilesList);
        m.Bind(VK.F12, A, CommandId.WorkingDirectoriesList);
        m.Bind(VK.F11, N, CommandId.ConnectNetworkDrive);
        m.Bind(VK.F12, N, CommandId.DisconnectNetworkDrive);
        m.Bind(VK.F9, C | S, CommandId.SharedDirectories);
        // phase 4: archives
        m.Bind(VK.F5, A, CommandId.Pack);
        m.Bind(VK.F6, A, CommandId.Unpack);
        m.Bind(VK.V, C | S, CommandId.VerifyChecksums); // Checksum plugin, like Open Salamander
        // hot paths 1-9 (Windows takes Ctrl+Shift+0 for switching the keyboard layout, so there is no hot path 0)
        for (int digit = 1; digit <= 9; digit++)
        {
            m.Bind(VK.D0 + digit, C, CommandId.GoToHotPath0 + digit);
            m.Bind(VK.D0 + digit, C | S, CommandId.SetHotPath0 + digit);
        }
        return m;
    }

    /// <summary>
    /// Total Commander's shortcuts, for the commands Vibe Salamander has: F2 refreshes, Shift+F6 renames in place,
    /// F9 opens the menu, Ctrl+F1 / F2 switch brief / full view, Ctrl+F3..F6 sort, Alt+F9 unpacks, Shift+F2 compares,
    /// Ctrl+L counts the occupied space, Num / restores the selection, Ctrl+Num + / − select and unselect everything.
    /// Keys Total Commander leaves free keep useful Vibe commands (Alt+1..5 views, Ctrl+1..9 hot paths).
    /// </summary>
    public static KeyMap CreateTotalCommanderDefaults()
    {
        var m = new KeyMap();
        const KeyModifiers N = KeyModifiers.None, C = KeyModifiers.Ctrl, S = KeyModifiers.Shift, A = KeyModifiers.Alt;

        // the function keys
        m.Bind(VK.F1, N, CommandId.Help);
        m.Bind(VK.F2, N, CommandId.Refresh);
        m.Bind(VK.F3, N, CommandId.View);
        m.Bind(VK.F4, N, CommandId.Edit);
        m.Bind(VK.F5, N, CommandId.Copy);
        m.Bind(VK.F6, N, CommandId.Move);
        m.Bind(VK.F7, N, CommandId.CreateDirectory);
        m.Bind(VK.F8, N, CommandId.Delete);
        m.Bind(VK.F9, N, CommandId.MainMenu);
        m.Bind(VK.F2, S, CommandId.CompareDirectories);
        m.Bind(VK.F3, S, CommandId.View);
        m.Bind(VK.F4, S, CommandId.EditNewFile);
        m.Bind(VK.F5, S, CommandId.Copy);
        m.Bind(VK.F6, S, CommandId.QuickRename);
        m.Bind(VK.F8, S, CommandId.DeletePermanently);
        m.Bind(VK.F10, S, CommandId.ContextMenu);
        m.Bind(VK.F1, A, CommandId.ChangeDriveLeft);
        m.Bind(VK.F2, A, CommandId.ChangeDriveRight);
        m.Bind(VK.F3, A, CommandId.OpenWith);
        m.Bind(VK.F5, A, CommandId.Pack);
        m.Bind(VK.F6, A, CommandId.Unpack);
        m.Bind(VK.F9, A, CommandId.Unpack);
        m.Bind(VK.F9, A | S, CommandId.TestArchive);
        m.Bind(VK.F7, A, CommandId.FindFiles);
        m.Bind(VK.F7, A | S, CommandId.FindDuplicates);
        m.Bind(VK.C, C | S, CommandId.CompareFiles);
        m.Bind(VK.F1, C, CommandId.ViewBrief);
        m.Bind(VK.F2, C, CommandId.ViewDetailed);
        m.Bind(VK.F3, C, CommandId.SortByName);
        m.Bind(VK.F4, C, CommandId.SortByExtension);
        m.Bind(VK.F5, C, CommandId.SortByDate);
        m.Bind(VK.F6, C, CommandId.SortBySize);
        m.Bind(VK.F12, C, CommandId.Filter);

        // files
        m.Bind(VK.Enter, N, CommandId.Open);
        m.Bind(VK.Enter, A, CommandId.Properties);
        m.Bind(VK.Enter, A | S, CommandId.CalculateDirectorySizes);
        m.Bind(VK.L, C, CommandId.CalculateOccupiedSpace);
        m.Bind(VK.Delete, N, CommandId.Delete);
        m.Bind(VK.Delete, S, CommandId.DeletePermanently);
        m.Bind(VK.Apps, N, CommandId.ContextMenu);
        m.Bind(VK.S, C, CommandId.Filter); // Total Commander's quick filter
        m.Bind(VK.F, C, CommandId.ConnectFtp);
        m.Bind(VK.N, C, CommandId.ConnectFtp);
        m.Bind(VK.F, C | S, CommandId.DisconnectNetworkDrive);
        m.Bind(VK.X, C, CommandId.ClipboardCut);
        m.Bind(VK.C, C, CommandId.ClipboardCopy);
        m.Bind(VK.V, C, CommandId.ClipboardPaste);
        m.Bind(VK.Insert, A, CommandId.CopyFullNameAsText);
        m.Bind(VK.Insert, A | S, CommandId.CopyNameAsText);
        m.Bind(VK.Insert, C | A, CommandId.CopyPathAsText);

        // panels and folders
        m.Bind(VK.Tab, N, CommandId.SwitchPanel);
        m.Bind(VK.Back, N, CommandId.ParentDirectory);
        m.Bind(VK.PageUp, C, CommandId.ParentDirectory);
        m.Bind(VK.PageDown, C, CommandId.Open);
        m.Bind(VK.Backslash, C, CommandId.RootDirectory);
        m.Bind(VK.Left, A, CommandId.GoBack);
        m.Bind(VK.Right, A, CommandId.GoForward);
        m.Bind(VK.Down, A, CommandId.WorkingDirectoriesList); // the folder history
        m.Bind(VK.Left, C, CommandId.OpenInOtherPanel);
        m.Bind(VK.Right, C, CommandId.OpenInOtherPanel);
        m.Bind(VK.U, C, CommandId.SwapPanels);
        m.Bind(VK.R, C, CommandId.Refresh);
        m.Bind(VK.Period, C, CommandId.PathFromOtherPanel);
        m.Bind(VK.H, C, CommandId.ToggleHiddenFiles);
        m.Bind(VK.D1, A, CommandId.ViewBrief);
        m.Bind(VK.D2, A, CommandId.ViewDetailed);
        m.Bind(VK.D3, A, CommandId.ViewIcons);
        m.Bind(VK.D4, A, CommandId.ViewThumbnails);
        m.Bind(VK.D5, A, CommandId.ViewTiles);

        // selection
        m.Bind(VK.Insert, N, CommandId.ToggleMarkAndMoveDown);
        m.Bind(VK.Space, N, CommandId.ToggleMark);
        m.Bind(VK.Add, N, CommandId.SelectByMask);
        m.Bind(VK.Subtract, N, CommandId.UnselectByMask);
        m.Bind(VK.Multiply, N, CommandId.InvertSelection);
        m.Bind(VK.Divide, N, CommandId.RestoreSelection);
        m.Bind(VK.A, C, CommandId.SelectAll);
        m.Bind(VK.Add, C, CommandId.SelectAll);
        m.Bind(VK.Subtract, C, CommandId.UnselectAll);
        m.Bind(VK.Add, A, CommandId.SelectSameExtension);
        m.Bind(VK.Subtract, A, CommandId.UnselectSameExtension);

        // hot paths 1-9, as in the Salamander map (Total Commander leaves these free)
        for (int digit = 1; digit <= 9; digit++)
        {
            m.Bind(VK.D0 + digit, C, CommandId.GoToHotPath0 + digit);
            m.Bind(VK.D0 + digit, C | S, CommandId.SetHotPath0 + digit);
        }
        return m;
    }

    public static KeyMap CreateDefaults(KeyScheme scheme) =>
        scheme == KeyScheme.TotalCommander ? CreateTotalCommanderDefaults() : CreateSalamanderDefaults();

    public void Bind(int key, KeyModifiers modifiers, CommandId command) => _map[new KeyChord(key, modifiers)] = command;

    public void Bind(KeyChord chord, CommandId command)
    {
        if (command == CommandId.None)
            _map.Remove(chord);
        else
            _map[chord] = command;
    }

    public void Unbind(KeyChord chord) => _map.Remove(chord);

    /// <summary>All bindings (for the keyboard settings page).</summary>
    public IReadOnlyDictionary<KeyChord, CommandId> Bindings => _map;

    /// <summary>All shortcuts of a command, in a stable order.</summary>
    public IReadOnlyList<KeyChord> GetShortcuts(CommandId command) =>
        _map.Where(p => p.Value == command).Select(p => p.Key)
            .OrderBy(c => (int)c.Modifiers).ThenBy(c => c.Key).ToList();

    public KeyMap Clone()
    {
        var copy = new KeyMap();
        foreach (var (chord, cmd) in _map)
            copy._map[chord] = cmd;
        return copy;
    }

    /// <summary>The Salamander defaults with the user's changes applied.</summary>
    public static KeyMap Create(IEnumerable<KeyBindingOverride>? overrides) => Create(KeyScheme.Salamander, overrides);

    /// <summary>A scheme's defaults with the user's changes applied.</summary>
    public static KeyMap Create(KeyScheme scheme, IEnumerable<KeyBindingOverride>? overrides)
    {
        KeyMap map = CreateDefaults(scheme);
        if (overrides != null)
            foreach (KeyBindingOverride o in overrides)
                map.Bind(new KeyChord(o.Key, o.Modifiers), o.Command);
        return map;
    }

    /// <summary>What differs from the defaults (stored in the settings; Command = None removes a default).</summary>
    public List<KeyBindingOverride> GetOverrides() => GetOverrides(KeyScheme.Salamander);

    /// <summary>What differs from a scheme's defaults.</summary>
    public List<KeyBindingOverride> GetOverrides(KeyScheme scheme)
    {
        KeyMap defaults = CreateDefaults(scheme);
        var result = new List<KeyBindingOverride>();
        foreach (var (chord, cmd) in _map)
            if (!defaults._map.TryGetValue(chord, out CommandId d) || d != cmd)
                result.Add(new KeyBindingOverride { Key = chord.Key, Modifiers = chord.Modifiers, Command = cmd });
        foreach (var (chord, _) in defaults._map)
            if (!_map.ContainsKey(chord))
                result.Add(new KeyBindingOverride { Key = chord.Key, Modifiers = chord.Modifiers, Command = CommandId.None });
        return result;
    }

    public CommandId Resolve(int key, KeyModifiers modifiers) =>
        _map.TryGetValue(new KeyChord(key, modifiers), out CommandId cmd) ? cmd : CommandId.None;

    /// <summary>First shortcut text for a command ("Ctrl+F9"), for menus and tooltips.</summary>
    public string? GetShortcutText(CommandId command)
    {
        // stable: the simplest shortcut (fewest modifiers) first, whatever order the map was edited in
        IReadOnlyList<KeyChord> shortcuts = GetShortcuts(command);
        return shortcuts.Count > 0 ? Describe(shortcuts[0]) : null;
    }

    public static string Describe(KeyChord chord)
    {
        string prefix = (chord.Modifiers.HasFlag(KeyModifiers.Ctrl) ? "Ctrl+" : "") +
                        (chord.Modifiers.HasFlag(KeyModifiers.Alt) ? "Alt+" : "") +
                        (chord.Modifiers.HasFlag(KeyModifiers.Shift) ? "Shift+" : "");
        string key = chord.Key switch
        {
            >= VK.F1 and <= VK.F12 => "F" + (chord.Key - VK.F1 + 1),
            VK.Back => "Backspace",
            VK.Tab => "Tab",
            VK.Enter => "Enter",
            VK.Space => "Space",
            VK.PageUp => "PgUp",
            VK.PageDown => "PgDn",
            VK.Left => "Left",
            VK.Right => "Right",
            VK.Up => "Up",
            VK.Down => "Down",
            VK.Insert => "Ins",
            VK.Delete => "Del",
            VK.Multiply => "Num *",
            VK.Add => "Num +",
            VK.Subtract => "Num -",
            VK.Divide => "Num /",
            >= VK.D0 and <= VK.D0 + 9 => ((char)chord.Key).ToString(),
            VK.Period => ".",
            VK.Apps => "Menu",
            VK.Escape => "Esc",
            VK.Home => "Home",
            VK.End => "End",
            >= VK.NumPad0 and <= VK.NumPad0 + 9 => "Num " + (chord.Key - VK.NumPad0),
            0xBA => ";", 0xBB => "=", 0xBC => ",", 0xBD => "-", 0xBF => "/", 0xC0 => "`", 0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'",
            _ => ((char)chord.Key).ToString(),
        };
        return prefix + key;
    }
}
