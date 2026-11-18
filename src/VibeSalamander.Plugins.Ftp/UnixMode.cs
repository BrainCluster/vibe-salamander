// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Plugins.Ftp;

/// <summary>Unix permissions: the mode bits, "rwxr-xr-x" and "755".</summary>
internal static class UnixMode
{
    /// <summary>"rwxr-xr-x" of 0755 (the nine permission bits).</summary>
    public static string Text(int mode)
    {
        Span<char> text = stackalloc char[9];
        const string letters = "rwxrwxrwx";
        for (int i = 0; i < 9; i++)
            text[i] = (mode & (1 << (8 - i))) != 0 ? letters[i] : '-';
        return new string(text);
    }

    /// <summary>"755" of 0755; "4755" when setuid, setgid or sticky is set.</summary>
    public static string Octal(int mode) =>
        (mode & 0xE00) != 0 ? Convert.ToString(mode & 0xFFF, 8).PadLeft(4, '0') : Convert.ToString(mode & 0x1FF, 8).PadLeft(3, '0');

    /// <summary>The mode of "755" or "0644"; null for anything else (3 or 4 octal digits).</summary>
    public static int? Parse(string? octal)
    {
        string text = (octal ?? string.Empty).Trim();
        if (text.Length is < 3 or > 4 || text.Any(c => c is < '0' or > '7'))
            return null;
        return Convert.ToInt32(text, 8);
    }

    /// <summary>The mode of "rwxr-xr-x" or "drwxr-xr-x" (an ls line's first column); null when it isn't one.</summary>
    public static int? FromText(string? text)
    {
        string t = (text ?? string.Empty).Trim();
        if (t.Length == 10)
            t = t[1..];
        if (t.Length != 9)
            return null;
        int mode = 0;
        const string letters = "rwxrwxrwx";
        for (int i = 0; i < 9; i++)
        {
            char c = t[i];
            if (c == letters[i] || (i % 3 == 2 && c is 's' or 't'))
                mode |= 1 << (8 - i);
            else if (c != '-' && !(i % 3 == 2 && c is 'S' or 'T'))
                return null;
        }
        return mode;
    }
}
