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
    public const int A = 0x41, D = 0x44, H = 0x48, R = 0x52, U = 0x55;
    public const int Multiply = 0x6A, Add = 0x6B, Subtract = 0x6D, Divide = 0x6F;
    public const int F1 = 0x70, F2 = 0x71, F3 = 0x72, F4 = 0x73, F5 = 0x74, F6 = 0x75, F7 = 0x76,
                     F8 = 0x77, F9 = 0x78, F10 = 0x79, F11 = 0x7A, F12 = 0x7B;
    public const int Period = 0xBE;
}

/// <summary>Keyboard shortcuts → commands. Defaults match Altap/Open Salamander.</summary>
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
        m.Bind(VK.Add, N, CommandId.SelectAll);       // Phase 2: opens the "Select" mask dialog
        m.Bind(VK.Subtract, N, CommandId.UnselectAll); // Phase 2: opens the "Unselect" mask dialog

        m.Bind(VK.Insert, A, CommandId.CopyFullNameAsText);
        m.Bind(VK.Insert, A | S, CommandId.CopyNameAsText);
        m.Bind(VK.Insert, C | A, CommandId.CopyPathAsText);

        m.Bind(VK.F1, N, CommandId.Help);
        return m;
    }

    public void Bind(int key, KeyModifiers modifiers, CommandId command) => _map[new KeyChord(key, modifiers)] = command;

    public CommandId Resolve(int key, KeyModifiers modifiers) =>
        _map.TryGetValue(new KeyChord(key, modifiers), out CommandId cmd) ? cmd : CommandId.None;

    /// <summary>First shortcut text for a command ("Ctrl+F9"), for menus and tooltips.</summary>
    public string? GetShortcutText(CommandId command)
    {
        foreach (var (chord, cmd) in _map)
            if (cmd == command)
                return Describe(chord);
        return null;
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
            VK.Period => ".",
            _ => ((char)chord.Key).ToString(),
        };
        return prefix + key;
    }
}
