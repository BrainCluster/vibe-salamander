// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.FileSystem;

namespace VibeSalamander.Core.Panels;

/// <summary>How the panels choose their view (Configuration → Views).</summary>
public enum ViewStyle
{
    /// <summary>Salamander: each panel has one view, for every folder and This PC.</summary>
    Classic,
    /// <summary>Explorer: This PC in Tiles, picture folders in Thumbnails, and every folder keeps the view chosen in it.</summary>
    Modern,
}

/// <summary>A view chosen in a folder (Modern views) and when the folder was last opened.</summary>
public sealed class FolderView
{
    public ViewMode View { get; set; }
    public DateTime Used { get; set; }
}

/// <summary>The rules of the Modern views: remembered views per folder, and the view a folder gets by its files.</summary>
public static class FolderViews
{
    /// <summary>Remembered folders at most; the ones not opened for the longest time are forgotten first.</summary>
    public const int MaxRemembered = 2000;

    private static readonly HashSet<string> PictureExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "jfif", "png", "gif", "bmp", "webp", "tif", "tiff", "heic", "heif", "avif", "ico", "jxl",
        "raw", "dng", "cr2", "cr3", "nef", "arw", "orf", "rw2", "raf", "psd",
        "mp4", "mkv", "avi", "mov", "wmv", "webm", "m4v", "mpg", "mpeg", "3gp", "mts", "m2ts",
    };

    /// <summary>A picture or a video, by its extension ("jpg", with or without the dot).</summary>
    public static bool IsPictureOrVideo(string extension) => PictureExtensions.Contains(extension.TrimStart('.'));

    /// <summary>The extensions of pictures and videos, with the dot (".jpg").</summary>
    public static IEnumerable<string> PictureAndVideoExtensions => PictureExtensions.Select(e => "." + e);

    /// <summary>The key of a folder: its path without a trailing separator, in capitals (paths compare without case).</summary>
    public static string Key(string path)
    {
        string trimmed = path.Length > 3 ? path.TrimEnd('\\', '/') : path;
        return trimmed.ToUpperInvariant();
    }

    /// <summary>
    /// True when most of the folder's files are pictures or videos (like Explorer's Pictures and Videos folders):
    /// at least 60 % of the visible files. Folders, hidden and system files (Thumbs.db, desktop.ini) don't count.
    /// </summary>
    public static bool IsPictureFolder(IEnumerable<FileEntry> entries)
    {
        int files = 0, pictures = 0;
        foreach (FileEntry entry in entries)
        {
            if (entry.IsDirectory || entry.IsUpDir || entry.IsHidden || entry.IsSystem)
                continue;
            files++;
            if (PictureExtensions.Contains(entry.Extension))
                pictures++;
        }
        return files > 0 && pictures * 10 >= files * 6;
    }

    /// <summary>The remembered view of the folder (and marks it as used now), or null.</summary>
    public static ViewMode? Recall(Dictionary<string, FolderView> views, string path)
    {
        if (!views.TryGetValue(Key(path), out FolderView? view) || view == null || !Enum.IsDefined(view.View))
            return null;
        view.Used = DateTime.UtcNow;
        return view.View;
    }

    /// <summary>Remembers the view chosen in a folder; beyond <see cref="MaxRemembered"/> the least recently used ones go.</summary>
    public static void Remember(Dictionary<string, FolderView> views, string path, ViewMode view)
    {
        views[Key(path)] = new FolderView { View = view, Used = DateTime.UtcNow };
        if (views.Count <= MaxRemembered)
            return;
        foreach (string old in views.OrderBy(v => v.Value?.Used ?? DateTime.MinValue).Take(views.Count - MaxRemembered + MaxRemembered / 10)
                     .Select(v => v.Key).ToList())
            views.Remove(old);
    }
}
