namespace VibeSalamander.Core.Localization;

/// <summary>
/// Translation hook for texts made in the core (command names, drive types, ...). The app sets
/// <see cref="Translate"/> to its language tables at startup; without it the English text is used.
/// </summary>
public static class L
{
    public static Func<string, string> Translate { get; set; } = s => s;

    /// <summary>The text in the user's language ("Copy" → "Kopiraj").</summary>
    public static string T(string english) => Translate(english);

    /// <summary>A translated format: F("{0} items", 3) → "3 stavke".</summary>
    public static string F(string englishFormat, params object?[] args) => string.Format(Translate(englishFormat), args);
}
