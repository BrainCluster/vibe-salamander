namespace VibeSalamander.ViewModels;

/// <summary>An item of a command menu shown by the window (user menu, lists of opened files / directories, shares).</summary>
public sealed record MenuEntry(string Text, Action? Invoke = null, string? Glyph = null, string? Shortcut = null,
    bool IsSeparator = false, bool IsEnabled = true)
{
    public static MenuEntry Separator { get; } = new(string.Empty, IsSeparator: true);
    public static MenuEntry Header(string text) => new(text, IsEnabled: false);
}
