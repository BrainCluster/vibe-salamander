using System.Runtime.InteropServices;
using VibeSalamander.Core.Commands;

namespace VibeSalamander.Services;

/// <summary>Current state of modifier keys (Win32 GetKeyState).</summary>
public static class KeyboardState
{
    public const int Shift = 0x10, Control = 0x11, Alt = 0x12;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    public static bool IsDown(int virtualKey) => (GetKeyState(virtualKey) & 0x8000) != 0;

    public static KeyModifiers CurrentModifiers() =>
        (IsDown(Control) ? KeyModifiers.Ctrl : KeyModifiers.None) |
        (IsDown(Shift) ? KeyModifiers.Shift : KeyModifiers.None) |
        (IsDown(Alt) ? KeyModifiers.Alt : KeyModifiers.None);
}
