// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Panels;
using VibeSalamander.Core.Settings;

namespace VibeSalamander.Core.Operations;

/// <summary>Names for new items: "New Folder", "New Folder (2)", ...</summary>
public static class NewItemNames
{
    /// <summary>"name.ext", or "name (2).ext", "name (3).ext"... - the first path that doesn't exist yet.</summary>
    /// <param name="exists">Whether a file or directory of that path exists (the disk, normally).</param>
    public static string UniquePath(string directory, string name, string extension, Func<string, bool>? exists = null)
    {
        exists ??= path => File.Exists(path) || Directory.Exists(path);
        string path = Path.Combine(directory, name + extension);
        for (int i = 2; exists(path); i++)
            path = Path.Combine(directory, $"{name} ({i}){extension}");
        return path;
    }
}

/// <summary>What a typed new name means (quick rename, a new folder or file named in place).</summary>
public sealed record RenameTarget
{
    /// <summary>Nothing to do: empty, or the same name.</summary>
    public bool Unchanged { get; init; }

    /// <summary>Why the name can't be used, or null.</summary>
    public string? Problem { get; init; }

    /// <summary>The new full path.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>A path ("a\b") typed for a new folder: the directories in between are created.</summary>
    public bool Nested { get; init; }

    /// <summary>Only the case of the letters changes ("photos" → "Photos").</summary>
    public bool CaseOnly { get; init; }

    /// <summary>The item to focus in the panel afterwards (for a nested path: the first new directory).</summary>
    public string FocusName { get; init; } = string.Empty;
}

public static class RenameRules
{
    /// <summary>
    /// The target of renaming <paramref name="currentName"/> in <paramref name="directory"/> to the typed name.
    /// Trailing dots and spaces are dropped (Windows drops them anyway). With <paramref name="allowSubdirectories"/>
    /// (a new folder) "a\b" makes nested directories, as Salamander's F7 does.
    /// </summary>
    public static RenameTarget Resolve(string directory, string currentName, string? typed, bool isDirectory, bool allowSubdirectories)
    {
        string? name = typed?.Trim().TrimEnd('.', ' ');
        if (string.IsNullOrEmpty(name) || name == currentName)
            return new RenameTarget { Unchanged = true, Path = System.IO.Path.Combine(directory, currentName) };

        bool nested = allowSubdirectories && isDirectory && name.IndexOfAny(new[] { '\\', '/' }) >= 0;
        string target;
        if (nested)
            target = PathUtil.Resolve(directory, name);
        else if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            return new RenameTarget { Problem = $"\"{name}\" contains characters that are not allowed in file names (\\ / : * ? \" < > |)." };
        else
            target = System.IO.Path.Combine(directory, name);

        string relative = System.IO.Path.GetRelativePath(directory, target);
        return new RenameTarget
        {
            Path = target,
            Nested = nested,
            CaseOnly = string.Equals(target, System.IO.Path.Combine(directory, currentName), StringComparison.OrdinalIgnoreCase),
            FocusName = relative.Split(System.IO.Path.DirectorySeparatorChar)[0],
        };
    }
}

/// <summary>Where F8 sends each item: the Recycle Bin or gone for good (Configuration → Recycle Bin).</summary>
public static class DeletePlan
{
    /// <param name="permanent">Shift+Del: everything is deleted permanently.</param>
    /// <param name="masks">For <see cref="DeleteMode.RecycleMatching"/>: the files that go to the Recycle Bin (directories always do).</param>
    public static (List<T> Recycle, List<T> Remove) Split<T>(IReadOnlyList<T> items, bool permanent, DeleteMode mode, string masks)
        where T : IMarkable
    {
        var recycle = new List<T>();
        var remove = new List<T>();
        if (permanent || mode == DeleteMode.DeletePermanently)
        {
            remove.AddRange(items);
        }
        else if (mode == DeleteMode.RecycleMatching)
        {
            var mask = new WildcardMask(masks);
            foreach (T item in items)
                (item.Entry.IsDirectory || mask.IsMatch(item.Entry.Name) ? recycle : remove).Add(item);
        }
        else
        {
            recycle.AddRange(items);
        }
        return (recycle, remove);
    }
}

/// <summary>F5 / F6: where the items go.</summary>
public static class CopyTargetRules
{
    /// <summary>The dialog's suggestion: the other panel's directory with a "\" (into it), or empty when it isn't usable.</summary>
    public static string DefaultTarget(string otherPanelPath, bool otherPanelUsable)
    {
        if (!otherPanelUsable)
            return string.Empty;
        return otherPanelPath.Length > 0 && !otherPanelPath.EndsWith('\\') ? otherPanelPath + "\\" : otherPanelPath;
    }

    /// <summary>
    /// "Don't show this dialog again" goes straight into the other panel, unless that isn't a usable target (This
    /// PC, or the same directory): then the dialog is shown anyway.
    /// </summary>
    public static bool AskForTarget(bool askSetting, bool otherPanelUsable, string activePath, string otherPanelPath) =>
        askSetting || !otherPanelUsable ||
        string.Equals(otherPanelPath.TrimEnd('\\'), activePath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The items go into a directory that doesn't exist yet (and is created): the target ends with "\", or several
    /// items go to one name. One item to a new name is a copy under that name instead.
    /// </summary>
    public static bool IntoNewDirectory(string resolvedTarget, int itemCount, bool targetExists) =>
        (resolvedTarget.EndsWith('\\') || itemCount > 1) && !targetExists;
}
