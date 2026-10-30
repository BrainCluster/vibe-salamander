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
        // grouped like Explorer (disks, removable and CD/DVD, network drives), by letter within a group
        Drives = drives.Select(d => new DriveButtonViewModel(d)).OrderBy(b => b.Group).ToList();
        OnPropertyChanged(nameof(VisibleDrives));
    }

    /// <summary>Drives for the drive bar and the Change Drive menu (Configuration → Change Drive Menu and Drive Bar).</summary>
    public IReadOnlyList<DriveButtonViewModel> VisibleDrives
    {
        get
        {
            DriveOptions? options = UiState.Drives;
            foreach (DriveButtonViewModel d in _drives)
                d.SeparatorAfter = options != null && options.SeparatorAfter.Contains(d.Letter, StringComparer.OrdinalIgnoreCase);
            var visible = options == null ? _drives.ToList()
                : _drives.Where(d => !options.HiddenDrives.Contains(d.Letter, StringComparer.OrdinalIgnoreCase)).ToList();
            for (int i = 0; i < visible.Count; i++)
                visible[i].GroupEnd = i + 1 < visible.Count && visible[i + 1].Group != visible[i].Group;
            return visible;
        }
    }

    /// <summary>The drive options changed: the drive bar is rebuilt.</summary>
    public void RefreshVisibleDrives() => OnPropertyChanged(nameof(VisibleDrives));
}
