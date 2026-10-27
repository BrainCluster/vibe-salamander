// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.FileSystem;

namespace VibeSalamander.Core.Operations;

/// <summary>Alt+F5 and Alt+F6: archive names, where archives and their contents go.</summary>
public static class ArchiveRules
{
    /// <summary>
    /// The new archive's name without extension: "photos" for one item ("report" for report.txt), the panel's
    /// directory name for several, the drive letter at a drive's root.
    /// </summary>
    public static string PackBaseName(int itemCount, string firstName, bool firstIsDirectory, string panelPath)
    {
        string name = itemCount == 1
            ? (firstIsDirectory ? firstName : Path.GetFileNameWithoutExtension(firstName))
            : Path.GetFileName(panelPath.TrimEnd('\\'));
        if (string.IsNullOrWhiteSpace(name))
            name = panelPath.Length >= 1 ? panelPath[..1] : "Archive"; // a drive root: "C"
        return name;
    }

    /// <summary>
    /// The archive's full path from what was typed in the Pack dialog: only a directory (ending with "\" or existing)
    /// gets the suggested name; a name without the format's extension gets it added.
    /// </summary>
    /// <param name="isDirectory">Whether a path is an existing directory (the disk, normally).</param>
    /// <param name="hasExtension">Whether a path already ends with the format's extension.</param>
    public static string PackArchivePath(string panelPath, string typed, string baseName, string extension,
        Func<string, bool> isDirectory, Func<string, bool> hasExtension)
    {
        string path = PathUtil.Resolve(panelPath, typed);
        if (typed.TrimEnd().EndsWith('\\') || isDirectory(path))
            return Path.Combine(path, baseName + extension);
        return hasExtension(path) ? path : path + extension;
    }

    /// <summary>
    /// The settings of one format (by option id) from the saved ones, which are kept for all formats as "7z.method";
    /// options never saved get their default.
    /// </summary>
    public static Dictionary<string, string> FormatOptions(string formatId, IEnumerable<(string Id, string Default)> options,
        IReadOnlyDictionary<string, string> saved)
    {
        string prefix = formatId + ".";
        return options.ToDictionary(o => o.Id, o => saved.TryGetValue(prefix + o.Id, out string? value) ? value : o.Default);
    }

    /// <summary>
    /// "Move to archive" deletes the sources only when everything went in, and never when the archive is inside one of
    /// the packed directories.
    /// </summary>
    public static bool MayDeleteSources(bool moveToArchive, OperationResult result, IEnumerable<string> sources, string archivePath) =>
        moveToArchive && !result.Cancelled && result.Failed == 0 && result.Skipped == 0 &&
        !sources.Any(source => archivePath.StartsWith(source.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

    /// <summary>The Unpack dialog's suggestion: the other panel's directory when it is on a disk, else this one, with "\".</summary>
    public static string UnpackDefaultTarget(string activePath, string otherPanelPath, bool otherPanelOnDisk)
    {
        string target = otherPanelOnDisk ? otherPanelPath : activePath;
        return target.EndsWith('\\') ? target : target + "\\";
    }
}
