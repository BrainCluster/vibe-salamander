// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Windows.Storage;
using Windows.Storage.Search;
using Windows.System;
using VibeSalamander.Core.Interop;
using VibeSalamander.Core.Panels;

namespace VibeSalamander.Services;

/// <summary>
/// Opens a file in its Windows app. A picture or a video goes with its folder, so the app can go to the next one:
/// Photos through its own link (ms-photos:viewer), other apps through the launcher's "neighboring files" (as
/// Explorer does); anything else, or when that fails, is opened the usual way.
/// </summary>
public static class FileLauncher
{
    public static void Open(string path, string? workingDirectory = null)
    {
        if (FolderViews.IsPictureOrVideo(Path.GetExtension(path)) && File.Exists(path))
        {
            // Photos (Windows 11) ignores the neighboring files of other programs, but its own link opens the
            // picture with the rest of the folder (Total Commander and OneCommander open it the same way)
            if (IsPhotosDefault(Path.GetExtension(path)))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-photos:viewer?fileName=" + path) { UseShellExecute = true })?.Dispose();
                    return;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // no Photos after all: the usual way
                }
            }
            _ = OpenWithFolderAsync(path, workingDirectory);
            return;
        }
        ShellOperations.Open(path, workingDirectory);
    }

    /// <summary>Microsoft Photos opens files of this type (".jpg") by default.</summary>
    private static bool IsPhotosDefault(string extension)
    {
        try
        {
            uint length = 512;
            var app = new System.Text.StringBuilder((int)length);
            const int AssocStrAppId = 21;
            return AssocQueryString(0, AssocStrAppId, extension, null, app, ref length) == 0 &&
                   app.ToString().Contains("Microsoft.Windows.Photos", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int AssocQueryString(int flags, int str, string assoc, string? extra, System.Text.StringBuilder? output, ref uint length);

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
