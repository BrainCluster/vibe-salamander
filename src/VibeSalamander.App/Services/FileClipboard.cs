// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace VibeSalamander.Services;

/// <summary>Files on the Windows clipboard (Ctrl+X / Ctrl+C / Ctrl+V), compatible with Explorer.</summary>
public static class FileClipboard
{
    public static async Task<IReadOnlyList<IStorageItem>> ToStorageItemsAsync(IEnumerable<string> paths)
    {
        var items = new List<IStorageItem>();
        foreach (string path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                    items.Add(await StorageFolder.GetFolderFromPathAsync(path));
                else if (File.Exists(path))
                    items.Add(await StorageFile.GetFileFromPathAsync(path));
            }
            catch (Exception)
            {
                // items that can't be represented are left out
            }
        }
        return items;
    }

    public static async Task SetFilesAsync(IReadOnlyList<string> paths, bool cut)
    {
        IReadOnlyList<IStorageItem> items = await ToStorageItemsAsync(paths);
        if (items.Count == 0)
            return;
        // copied like Explorer copies: "copy or link" (Preferred DropEffect 5), so Explorer's menus (the desktop's
        // too) offer Paste shortcut for it; cut is "move" only
        var package = new DataPackage
        {
            RequestedOperation = cut ? DataPackageOperation.Move : DataPackageOperation.Copy | DataPackageOperation.Link,
        };
        package.SetStorageItems(items, readOnly: !cut);
        Clipboard.SetContent(package);
        Clipboard.Flush(); // keep the data on the clipboard after the app exits
    }

    /// <summary>Paths on the clipboard and whether they were cut (move) rather than copied.</summary>
    public static async Task<(IReadOnlyList<string> Paths, bool Move)> GetFilesAsync()
    {
        DataPackageView content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.StorageItems))
            return (Array.Empty<string>(), false);
        IReadOnlyList<IStorageItem> items = await content.GetStorageItemsAsync();
        var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
        bool move = (content.RequestedOperation & DataPackageOperation.Move) != 0 &&
                    (content.RequestedOperation & DataPackageOperation.Copy) == 0;
        return (paths, move);
    }
}
