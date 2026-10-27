// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Core.Panels;

/// <summary>The address bar: the locations of a path, which of them fit, where clicking one goes.</summary>
public static class BreadcrumbRules
{
    /// <summary>
    /// The locations of a path on a disk or inside an archive on one: the drive (by its caption, "Local Disk (C:)") or
    /// share, then each folder. "This PC" before them is the caller's.
    /// </summary>
    /// <param name="driveCaption">The caption of a drive ("C:"), or null to show the letter.</param>
    public static List<(string Label, string Path)> DiskSegments(string path, Func<string, string?> driveCaption)
    {
        var segments = new List<(string, string)>();
        string root = System.IO.Path.GetPathRoot(path) ?? path;
        int rootLength = root.Length;
        string rootLabel;
        if (root.Length >= 2 && root[1] == ':')
        {
            string drive = root[..2].ToUpperInvariant();
            rootLabel = driveCaption(drive) ?? drive;
            root = drive + "\\";
        }
        else
        {
            rootLabel = root.TrimEnd('\\'); // \\server\share
        }
        segments.Add((rootLabel, root));

        string current = root;
        foreach (string part in path[Math.Min(path.Length, rootLength)..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = System.IO.Path.Combine(current, part);
            segments.Add((part, current));
        }
        return segments;
    }

    /// <summary>
    /// How many leading locations go into the "..." menu so that the rest fits <paramref name="available"/> (the
    /// "..." button then takes <paramref name="overflowWidth"/>). The last location (the current folder) always stays.
    /// </summary>
    /// <param name="widths">The width of each location (with its chevron), in order.</param>
    public static int HiddenCount(IReadOnlyList<double> widths, double available, double overflowWidth)
    {
        double total = widths.Sum();
        if (total <= available)
            return 0;
        total += overflowWidth;
        int hidden = 0;
        while (hidden < widths.Count - 1 && total > available)
            total -= widths[hidden++];
        return hidden;
    }

    /// <summary>
    /// Going up through the address bar focuses the folder we came from, like Backspace: the name of the item inside
    /// <paramref name="ancestor"/> that leads to <paramref name="path"/>, or null when it isn't on the way.
    /// </summary>
    public static string? ChildOnTheWay(string ancestor, string path)
    {
        string a = ancestor.TrimEnd('\\') + "\\";
        if (!path.StartsWith(a, StringComparison.OrdinalIgnoreCase) || path.Length <= a.Length)
            return null;
        return path[a.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    /// <summary>True when the text typed into the address bar means This PC (in English or the user's language).</summary>
    public static bool MeansThisPc(string typed, string localizedThisPc)
    {
        string text = typed.Trim();
        return text.Equals("this pc", StringComparison.OrdinalIgnoreCase) || text.Equals(localizedThisPc, StringComparison.CurrentCultureIgnoreCase);
    }
}
