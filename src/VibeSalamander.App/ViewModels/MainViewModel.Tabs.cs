// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Panels;
using VibeSalamander.Core.Settings;
using VibeSalamander.Services;

namespace VibeSalamander.ViewModels;

// MainViewModel: the tabs of both panels, and the commands for them.
public sealed partial class MainViewModel
{
    public TabsViewModel LeftTabs { get; }
    public TabsViewModel RightTabs { get; }

    public TabsViewModel TabsOf(PaneViewModel pane) => pane == Left ? LeftTabs : RightTabs;

    private TabsViewModel OtherTabs(PaneViewModel pane) => pane == Left ? RightTabs : LeftTabs;

    private TabsViewModel CreateTabs(PaneViewModel pane, PanelSettings settings)
    {
        var shown = new PanelTab
        {
            Path = settings.Path,
            SortMode = settings.SortMode,
            SortDescending = settings.SortDescending,
            ViewMode = settings.ViewMode,
        };
        // the saved tabs; the shown one is the panel's own state (path, sort and view are kept for the panel)
        List<PanelTab>? saved = settings.Tabs is { Count: > 1 } tabs ? tabs : null;
        int active = saved == null ? 0 : Math.Clamp(settings.ActiveTab, 0, saved.Count - 1);
        if (saved != null)
        {
            shown.Locked = saved[active].Locked;
            saved[active] = shown;
        }
        var list = new TabList(saved, active, shown);
        return new TabsViewModel(pane, list, () => Settings.Panels.AlwaysShowTabs,
            path => RemoteProvider.CanHandle(path) ? RemoteProvider.GetDisplayPath(path) : path)
        {
            RowShown = settings.TabRow,
        };
    }

    /// <summary>The tabs into the settings (none when a side has one: nothing changes for those who don't use tabs).</summary>
    private static void StoreTabs(TabsViewModel tabs, PanelSettings settings)
    {
        tabs.SaveShown();
        settings.Tabs = tabs.Count > 1 ? tabs.List.Tabs.ToList() : null;
        settings.ActiveTab = tabs.List.ActiveIndex;
        settings.TabRow = tabs.RowShown;
    }

    /// <summary>A server is still used by another tab (of either panel) or the other panel: no "disconnect?" question.</summary>
    public bool ServerUsedElsewhere(PaneViewModel leaving, string serverPath)
    {
        PaneViewModel other = leaving == Left ? Right : Left;
        return (other.IsRemote && RemoteFileSystemProvider.SameServer(other.Path, serverPath)) ||
               LeftTabs.BackgroundPaths.Concat(RightTabs.BackgroundPaths).Any(p => RemoteFileSystemProvider.SameServer(p, serverPath));
    }

    /// <summary>
    /// The place a "new tab" command opens for the focused item: a folder, a drive, an archive; ".." the parent;
    /// a file its folder, focused on it. Null: nothing to open.
    /// </summary>
    private (string Path, string? Focus)? NewTabTarget(PaneViewModel pane)
    {
        FileItemViewModel? item = pane.FocusedItem;
        if (item == null)
            return (pane.IsThisPc ? ThisPcProvider.RootPath : pane.Path, null);
        if (item.Drive is { } drive)
            return (drive.Root, null);
        if (item.Entry.IsUpDir)
        {
            ParentLocation? parent = Registry.Find(pane.Path)?.GetParent(pane.Path);
            return parent == null ? null : (parent.Path, parent.FocusName);
        }
        if (item.Entry.IsDirectory || (!pane.IsArchive && !pane.IsRemote && Registry.Find(item.Entry.FullPath) is ArchiveFileSystemProvider))
            return (item.Entry.FullPath, null);
        return (pane.IsThisPc ? ThisPcProvider.RootPath : pane.Path, item.Entry.Name);
    }

    /// <summary>Ctrl+Up: the folder under the caret in a new tab of this panel.</summary>
    private async Task OpenInNewTabAsync(PaneViewModel pane)
    {
        if (NewTabTarget(pane) is { } target)
            await TabsOf(pane).NewAsync(target.Path, target.Focus);
    }

    /// <summary>Ctrl+Shift+Up: the folder under the caret in a new tab of the other panel (this panel stays active).</summary>
    private async Task OpenInNewTabOtherPanelAsync(PaneViewModel pane)
    {
        if (NewTabTarget(pane) is { } target)
            await OtherTabs(pane).NewAsync(target.Path, target.Focus);
        _host.FocusPane(pane);
    }

    /// <summary>A middle click on a folder: it opens in a new tab behind the shown one.</summary>
    public async Task OpenInBackgroundTabAsync(PaneViewModel pane, FileItemViewModel item)
    {
        if (item.Drive is { } drive)
            await TabsOf(pane).NewAsync(drive.Root, activate: false);
        else if (item.Entry.IsDirectory && !item.Entry.IsUpDir)
            await TabsOf(pane).NewAsync(item.Entry.FullPath, activate: false);
    }

    private async Task NewTabAsync(PaneViewModel pane)
    {
        await TabsOf(pane).NewAsync();
        _host.FocusPane(pane);
    }

    private async Task CloseTabAsync(PaneViewModel pane)
    {
        TabsViewModel tabs = TabsOf(pane);
        await tabs.CloseAsync(tabs.List.ActiveIndex);
        _host.FocusPane(pane);
    }

    private async Task CloseOtherTabsAsync(PaneViewModel pane)
    {
        TabsViewModel tabs = TabsOf(pane);
        await tabs.CloseOthersAsync(tabs.List.ActiveIndex);
        _host.FocusPane(pane);
    }

    private async Task SwitchTabAsync(PaneViewModel pane, bool next)
    {
        TabsViewModel tabs = TabsOf(pane);
        if (tabs.Count < 2)
            return;
        await (next ? tabs.NextAsync() : tabs.PreviousAsync());
        _host.FocusPane(pane);
    }

    private async Task ReopenClosedTabAsync(PaneViewModel pane)
    {
        await TabsOf(pane).ReopenAsync();
        _host.FocusPane(pane);
    }

    /// <summary>Show / hide tabs: the arrow at the end of the drive bar.</summary>
    private void ToggleTabRow(PaneViewModel pane) => TabsOf(pane).ToggleRow();

    private void ToggleLockTab(PaneViewModel pane)
    {
        TabsViewModel tabs = TabsOf(pane);
        tabs.ToggleLock(tabs.List.ActiveIndex);
    }

    /// <summary>A copy of a tab into the other panel's tabs, shown there.</summary>
    public async Task CopyTabToOtherPanelAsync(PaneViewModel pane, int? index = null)
    {
        TabsViewModel tabs = TabsOf(pane);
        PanelTab copy = tabs.Snapshot(index ?? tabs.List.ActiveIndex);
        await OtherTabs(pane).AddAsync(copy);
        _host.FocusPane(pane);
    }

    /// <summary>A tab dragged to the other panel's row: it moves there (copied with Ctrl, or when it is the last tab here).</summary>
    public async Task MoveTabToOtherSideAsync(PaneViewModel from, int index, int? at, bool copy)
    {
        TabsViewModel source = TabsOf(from);
        TabsViewModel target = OtherTabs(from);
        PanelTab? tab = copy || source.Count == 1 ? source.Snapshot(index) : await source.TakeAsync(index);
        if (tab == null)
            return;
        if (at is int position)
        {
            await target.AddAsync(tab);
            target.Move(target.List.ActiveIndex, position);
        }
        else
        {
            await target.AddAsync(tab);
        }
    }

    /// <summary>Ctrl+U with tabs: the panels change places with all their tabs.</summary>
    private async Task SwapTabsAsync()
    {
        LeftTabs.SaveShown();
        RightTabs.SaveShown();
        LeftTabs.List.SwapWith(RightTabs.List);
        await Task.WhenAll(Left.ShowTabAsync(LeftTabs.List.Active), Right.ShowTabAsync(RightTabs.List.Active));
    }

    /// <summary>
    /// A folder sent to the running program (a second start with a folder, later Explorer's "Open in Vibe
    /// Salamander"): a new tab, instead of replacing what the panel shows.
    /// </summary>
    public async Task OpenInNewTabAsync(PaneViewModel pane, string path, string? focusName = null)
    {
        string current = pane.IsThisPc ? ThisPcProvider.RootPath : pane.Path;
        if (string.Equals(current.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            if (focusName != null)
                await pane.NavigateAsync(pane.Path, focusName, addToHistory: false);
            return; // already shown
        }
        await TabsOf(pane).NewAsync(path, focusName);
    }
}
