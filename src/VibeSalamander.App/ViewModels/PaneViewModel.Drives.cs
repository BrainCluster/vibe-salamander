// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using VibeSalamander.Core;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Formatting;
using VibeSalamander.Core.Panels;
using VibeSalamander.Core.Settings;
using VibeSalamander.Services;

namespace VibeSalamander.ViewModels;

// PaneViewModel: the drive bar's drives.
public sealed partial class PaneViewModel
{
    // ---- drives ------------------------------------------------------------------------------

    public async Task LoadDrivesAsync()
    {
        IReadOnlyList<DriveEntryInfo> drives = await ThisPcProvider.QueryDrivesAsync();
        var buttons = drives.Select(d => new DriveButtonViewModel(d)).ToList();
        foreach (var b in buttons)
        {
            var entry = new FileEntry(b.Drive.Root, b.Drive.Root, true, 0, DateTime.MinValue, FileAttributes.Directory, tag: b.Drive);
            _icons.Request(entry, icon => b.Icon = icon);
        }
        Drives = buttons;
        OnPropertyChanged(nameof(VisibleDrives));
    }

    /// <summary>Drives for the drive bar and the Change Drive menu (Configuration → Change Drive Menu and Drive Bar).</summary>
    public IReadOnlyList<DriveButtonViewModel> VisibleDrives
    {
        get
        {
            DriveOptions? options = UiState.Drives;
            if (options == null)
                return _drives;
            foreach (DriveButtonViewModel d in _drives)
                d.SeparatorAfter = options.SeparatorAfter.Contains(d.Letter, StringComparer.OrdinalIgnoreCase);
            return _drives.Where(d => !options.HiddenDrives.Contains(d.Letter, StringComparer.OrdinalIgnoreCase)).ToList();
        }
    }

    /// <summary>The drive options changed: the drive bar is rebuilt.</summary>
    public void RefreshVisibleDrives() => OnPropertyChanged(nameof(VisibleDrives));
}
