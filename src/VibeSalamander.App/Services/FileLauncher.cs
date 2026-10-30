// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Windows.Storage;
using Windows.Storage.Search;
using Windows.System;
using VibeSalamander.Core.Interop;
using VibeSalamander.Core.Panels;

namespace VibeSalamander.Services;

/// <summary>
/// Opens a file in its Windows app. A picture or a video goes with its folder (the launcher's "neighboring files", as
/// Explorer does), so Photos or Media Player can go to the next one; anything else, or when that fails, is opened
/// the usual way.
/// </summary>
public static class FileLauncher
{
    public static void Open(string path, string? workingDirectory = null)
    {
        if (FolderViews.IsPictureOrVideo(Path.GetExtension(path)) && File.Exists(path))
        {
            _ = OpenWithFolderAsync(path, workingDirectory);
            return;
        }
        ShellOperations.Open(path, workingDirectory);
    }

    private static async Task OpenWithFolderAsync(string path, string? workingDirectory)
    {
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
            var options = new LauncherOptions { NeighboringFilesQuery = Query(folder) };
            if (await Launcher.LaunchFileAsync(file, options))
                return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // a folder the launcher can't query (a network share, say): opened without its neighbors
        }
        try
        {
            ShellOperations.Open(path, workingDirectory);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Core.Diagnostics.ErrorLog.Exception("Open " + path, ex);
        }
    }

    /// <summary>The folder's pictures and videos by name (the default order where by name isn't offered).</summary>
    private static StorageFileQueryResult Query(StorageFolder folder)
    {
        var types = FolderViews.PictureAndVideoExtensions.ToList();
        try
        {
            return folder.CreateFileQueryWithOptions(new QueryOptions(CommonFileQuery.OrderByName, types));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            return folder.CreateFileQueryWithOptions(new QueryOptions(CommonFileQuery.DefaultQuery, types));
        }
    }
}
