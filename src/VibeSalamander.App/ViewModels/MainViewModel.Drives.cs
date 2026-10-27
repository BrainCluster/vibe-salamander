// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using VibeSalamander.Core;
using VibeSalamander.Core.Commands;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Formatting;
using VibeSalamander.Core.Interop;
using VibeSalamander.Core.Operations;
using VibeSalamander.Core.Panels;
using VibeSalamander.Core.Settings;
using VibeSalamander.Core.Tools;
using VibeSalamander.Plugins;
using VibeSalamander.Services;

namespace VibeSalamander.ViewModels;

// MainViewModel: disk images (mount / unmount) and drives plugged in or pulled out.
public sealed partial class MainViewModel
{
    // ---- disk images ------------------------------------------------------------------------

    /// <summary>Enter on an .iso / .img / .vhd(x): mounts it (Configuration → General), else opens it with its default app.</summary>
    private async Task OpenDiskImageAsync(PaneViewModel pane, string path)
    {
        if (Settings.General.MountDiskImages)
        {
            await MountImageAsync(pane, path);
            return;
        }
        try
        {
            ShellOperations.Open(path, pane.Path);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _host.Notify(ex.Message);
        }
    }

    /// <summary>Mounts the image as a drive and opens the drive in the other panel.</summary>
    private async Task MountImageAsync(PaneViewModel pane, string path)
    {
        string name = Path.GetFileName(path);
        _host.Notify($"Mounting \"{name}\"...", isError: false);
        string root;
        try
        {
            root = await DiskImages.MountAsync(path);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException)
        {
            _host.Notify($"\"{name}\" could not be mounted: {ex.Message}");
            return;
        }
        for (int wait = 0; wait < 20 && !await Task.Run(() => IsReady(root)); wait++)
            await Task.Delay(250);
        PaneViewModel other = pane == Left ? Right : Left;
        await Task.WhenAll(Left.LoadDrivesAsync(), Right.LoadDrivesAsync());
        await other.NavigateAsync(root);
        _host.BringToFront(); // the shell's fallback may have opened an Explorer window
        Activate(other);
        _host.FocusPane(other);
        _host.Notify($"\"{name}\" is mounted as {root.TrimEnd('\\')}. Unmount it from the drive's context menu.", isError: false);
    }

    /// <summary>The drive to unmount: the focused drive (This PC), else the panel's own drive, when it is a mounted image.</summary>
    private static string? ImageDriveOf(PaneViewModel p)
    {
        string? root = p.FocusedItem?.Drive?.Root ?? (p.IsLocalDisk && p.Path.Length >= 2 && p.Path[1] == ':' ? p.Path[..2] + "\\" : null);
        return root != null && DiskImages.IsMountedImage(root) ? root : null;
    }

    /// <summary>Unmounts a mounted image; panels showing it go to This PC first (they would keep it busy).</summary>
    public async Task UnmountImageAsync(string root)
    {
        foreach (PaneViewModel pane in new[] { Left, Right })
            if (pane.IsLocalDisk && pane.Path.StartsWith(root[..2], StringComparison.OrdinalIgnoreCase))
                await pane.NavigateAsync(ThisPcProvider.RootPath, root[..2]);
        try
        {
            await DiskImages.UnmountAsync(root);
            _host.Notify($"{root.TrimEnd('\\')} is unmounted.", isError: false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException)
        {
            _host.Notify($"{root.TrimEnd('\\')} could not be unmounted: {ex.Message}");
        }
        await Task.WhenAll(Left.LoadDrivesAsync(), Right.LoadDrivesAsync());
        foreach (PaneViewModel pane in new[] { Left, Right })
            if (pane.IsThisPc)
                await pane.RefreshAsync();
    }

    // ---- drives plugged in and pulled out (live USB) ---------------------------------------------

    /// <summary>
    /// Drives appeared (Windows WM_DEVICECHANGE): the drive bars and This PC are updated, and a USB
    /// drive or memory card opens in the focused panel (Configuration → Drives). Mounted images and
    /// internal disks are not opened.
    /// </summary>
    public async Task OnDrivesArrivedAsync(IReadOnlyList<string> roots)
    {
        try
        {
            // a new drive needs a moment before it can be read
            for (int wait = 0; wait < 20 && !(await Task.Run(() => roots.All(IsReady))); wait++)
                await Task.Delay(250);
            await Task.WhenAll(Left.LoadDrivesAsync(), Right.LoadDrivesAsync());
            foreach (PaneViewModel pane in new[] { Left, Right })
                if (pane.IsThisPc)
                    await pane.RefreshAsync();

            if (!Settings.Drives.OpenPluggedInDrives)
                return;
            foreach (string root in roots)
            {
                if (!await Task.Run(() => IsReady(root) && DriveBus.IsPlugIn(root)))
                    continue;
                PaneViewModel target = _active;
                await target.NavigateAsync(root);
                _host.FocusPane(target);
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // a drive that came and went at once: nothing to show
        }
    }

    /// <summary>Drives are gone: panels showing them go to This PC, the drive bars and This PC are updated.</summary>
    public async Task OnDrivesRemovedAsync(IReadOnlyList<string> roots)
    {
        try
        {
            foreach (PaneViewModel pane in new[] { Left, Right })
            {
                if (pane.IsThisPc)
                    continue;
                string path = pane.Path;
                if (roots.Any(r => path.StartsWith(r[..2], StringComparison.OrdinalIgnoreCase)))
                    await pane.NavigateAsync(ThisPcProvider.RootPath);
            }
            await Task.WhenAll(Left.LoadDrivesAsync(), Right.LoadDrivesAsync());
            foreach (PaneViewModel pane in new[] { Left, Right })
                if (pane.IsThisPc)
                    await pane.RefreshAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // the next refresh shows the right state
        }
    }

    private static bool IsReady(string root)
    {
        try
        {
            return new DriveInfo(root).IsReady;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
