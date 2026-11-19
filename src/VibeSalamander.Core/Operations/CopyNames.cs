// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.Localization;

namespace VibeSalamander.Core.Operations;

/// <summary>
/// The name of a copy made in the item's own folder, as File Explorer names it: "Report - Copy.docx", then
/// "Report - Copy (2).docx"; "Photos - Copy", "Photos - Copy (2)" for a folder. In the user's language.
/// </summary>
public static class CopyNames
{
    /// <summary>The first free copy name next to <paramref name="path"/> (a full path).</summary>
    public static string FreeCopyOf(string path, bool isDirectory)
    {
        string folder = Path.GetDirectoryName(path)!;
        string name = Path.GetFileName(path);
        string stem = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
        string extension = isDirectory ? "" : Path.GetExtension(name);
        if (stem.Length == 0) // ".gitignore": the whole name is the stem
        {
            stem = name;
            extension = "";
        }
        for (int n = 1; ; n++)
        {
            string candidate = Path.Combine(folder, (n == 1 ? L.F("{0} - Copy", stem) : L.F("{0} - Copy ({1})", stem, n)) + extension);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }
}
