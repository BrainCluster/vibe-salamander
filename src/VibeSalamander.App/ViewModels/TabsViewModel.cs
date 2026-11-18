// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using VibeSalamander.Core.Panels;
using VibeSalamander.Services;

namespace VibeSalamander.ViewModels;

/// <summary>One tab in the row above a panel.</summary>
public sealed class TabItemViewModel : BindableBase
{
    private string _title = string.Empty;
    private string _toolTip = string.Empty;
    private bool _isShown;
    private bool _isLocked;

    public TabItemViewModel(PanelTab tab)
    {
        Tab = tab;
    }

    public PanelTab Tab { get; }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    /// <summary>The full path (and "locked").</summary>
    public string ToolTip
    {
        get => _toolTip;
        set => SetProperty(ref _toolTip, value);
    }

    /// <summary>The tab the panel shows.</summary>
    public bool IsShown
    {
        get => _isShown;
        set => SetProperty(ref _isShown, value);
    }

    public bool IsLocked
    {
        get => _isLocked;
        set
        {
            if (SetProperty(ref _isLocked, value))
                OnPropertyChanged(nameof(LockVisibility));
        }
    }

    public Visibility LockVisibility => _isLocked ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// The tabs of one side (left or right) and the panel they take turns in. Showing another tab saves the panel into the
/// tab it leaves (<see cref="PaneViewModel.SaveInto"/>) and shows the other one (<see cref="PaneViewModel.ShowTabAsync"/>).
/// </summary>
public sealed class TabsViewModel : BindableBase
{
    private readonly Func<bool> _alwaysShow;
    private readonly Func<string, string> _display;
    private Task _switching = Task.CompletedTask;

    /// <param name="display">A path as the user reads it (a server's, without its encoding).</param>
    public TabsViewModel(PaneViewModel pane, TabList tabs, Func<bool> alwaysShow, Func<string, string> display)
    {
        Pane = pane;
        List = tabs;
        _alwaysShow = alwaysShow;
        _display = display;
        tabs.Changed += (_, _) => Sync();
        pane.Navigated += (_, _) => UpdateShown();
        pane.LeavingTab = _ => KeepLockedTab();
        Sync();
    }

    public PaneViewModel Pane { get; }
    public TabList List { get; }
    public ObservableCollection<TabItemViewModel> Items { get; } = new();

    public int Count => List.Count;

    /// <summary>The tab row: from two tabs on (or always: Configuration → Panels).</summary>
    public Visibility RowVisibility => List.Count > 1 || _alwaysShow() ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Configuration changed (Always show tabs).</summary>
    public void ApplyOptions() => OnPropertyChanged(nameof(RowVisibility));

    /// <summary>The items follow the list (same tab objects, in its order).</summary>
    private void Sync()
    {
        for (int i = Items.Count - 1; i >= 0; i--)
            if (!List.Tabs.Contains(Items[i].Tab))
                Items.RemoveAt(i);
        for (int i = 0; i < List.Count; i++)
        {
            PanelTab tab = List.Tabs[i];
            int at = IndexOfItem(tab);
            if (at < 0)
                Items.Insert(i, new TabItemViewModel(tab));
            else if (at != i)
                Items.Move(at, i);
        }
        for (int i = 0; i < Items.Count; i++)
            Describe(Items[i], i == List.ActiveIndex);
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(RowVisibility));
    }

    private int IndexOfItem(PanelTab tab)
    {
        for (int i = 0; i < Items.Count; i++)
            if (ReferenceEquals(Items[i].Tab, tab))
                return i;
        return -1;
    }

    private void Describe(TabItemViewModel item, bool shown)
    {
        string path = shown ? (Pane.IsThisPc ? Core.FileSystem.ThisPcProvider.RootPath : Pane.Path) : item.Tab.Path;
        if (shown && path.Length > 0)
            item.Tab.Path = path;
        item.Title = PanelTab.Title(item.Tab.Path);
        string where = item.Tab.Path.StartsWith(Core.FileSystem.ThisPcProvider.RootPath, StringComparison.OrdinalIgnoreCase)
            ? Loc.T("This PC")
            : _display(item.Tab.Path);
        item.ToolTip = item.Tab.Locked ? where + "\n" + Loc.T("Locked: going elsewhere opens a new tab") : where;
        item.IsShown = shown;
        item.IsLocked = item.Tab.Locked;
    }

    /// <summary>The panel went elsewhere: the shown tab's caption follows.</summary>
    private void UpdateShown()
    {
        if (List.ActiveIndex < Items.Count)
            Describe(Items[List.ActiveIndex], shown: true);
    }

    /// <summary>
    /// The panel is about to leave its folder: when the shown tab is locked, it keeps the folder, and the panel goes
    /// on in a new tab right after it (with the same history, so Back still leads back).
    /// </summary>
    private void KeepLockedTab()
    {
        PanelTab shown = List.Active;
        if (!shown.Locked)
            return;
        Pane.SaveInto(shown);
        PanelTab next = shown.NewFromHere();
        next.Marked = new List<string>(shown.Marked);
        List.Insert(next); // shown at once: the panel already is in that state
    }

    // ---- the user's tab commands --------------------------------------------------------------------------

    /// <summary>Runs one switch at a time (two quick Ctrl+Tabs don't interleave their loads).</summary>
    private Task Serialize(Func<Task> action)
    {
        Task previous = _switching;
        async Task Run()
        {
            try
            {
                await previous;
            }
            catch (Exception)
            {
                // the previous switch's problem was reported there
            }
            await action();
        }
        _switching = Run();
        return _switching;
    }

    /// <summary>Shows another tab.</summary>
    public Task ShowAsync(int index) => Serialize(async () =>
    {
        if (index == List.ActiveIndex || index < 0 || index >= List.Count)
            return;
        Pane.SaveInto(List.Active);
        List.Select(index);
        await Pane.ShowTabAsync(List.Active);
    });

    public Task NextAsync() => ShowAsync(List.Next);
    public Task PreviousAsync() => ShowAsync(List.Previous);

    /// <summary>
    /// A new tab: of <paramref name="path"/> (focusing <paramref name="focusName"/>), else of the shown tab's folder.
    /// <paramref name="activate"/> false: it opens in the background (a middle click).
    /// </summary>
    public Task NewAsync(string? path = null, string? focusName = null, bool activate = true) => Serialize(async () =>
    {
        Pane.SaveInto(List.Active);
        PanelTab tab = List.Active.NewFromHere();
        tab.Back.Clear();
        tab.Forward.Clear();
        if (path != null && !string.Equals(path, tab.Path, StringComparison.OrdinalIgnoreCase))
        {
            tab.Back.Add(tab.Path); // Back leads to where it was opened from
            tab.Path = path;
            tab.Filter = string.Empty;
            tab.FocusName = focusName;
        }
        else if (focusName != null)
        {
            tab.FocusName = focusName;
        }
        List.Insert(tab, activate: activate);
        if (activate)
            await Pane.ShowTabAsync(tab);
    });

    /// <summary>Adds a whole tab (moved or copied from the other side, or saved), after the shown one.</summary>
    public Task AddAsync(PanelTab tab, bool activate = true) => Serialize(async () =>
    {
        Pane.SaveInto(List.Active);
        List.Insert(tab, activate: activate);
        if (activate)
            await Pane.ShowTabAsync(tab);
    });

    /// <summary>Closes a tab (not the last one).</summary>
    public Task CloseAsync(int index) => Serialize(async () =>
    {
        bool shown = index == List.ActiveIndex;
        if (shown)
            Pane.SaveInto(List.Active); // reopening brings it back as it was
        if (List.Close(index) && shown)
            await Pane.ShowTabAsync(List.Active);
    });

    public Task CloseOthersAsync(int keep) => Serialize(async () =>
    {
        bool switching = keep != List.ActiveIndex;
        Pane.SaveInto(List.Active);
        List.CloseOthers(keep);
        if (switching)
            await Pane.ShowTabAsync(List.Active);
    });

    public Task ReopenAsync() => Serialize(async () =>
    {
        if (!List.CanReopen)
            return;
        Pane.SaveInto(List.Active);
        PanelTab? tab = List.Reopen();
        if (tab != null)
            await Pane.ShowTabAsync(tab);
    });

    /// <summary>Takes a tab out (it moves to the other side); returns it, or null for the last tab.</summary>
    public async Task<PanelTab?> TakeAsync(int index)
    {
        PanelTab? taken = null;
        await Serialize(async () =>
        {
            bool shown = index == List.ActiveIndex;
            if (shown)
                Pane.SaveInto(List.Active);
            PanelTab tab = List.Tabs[index];
            if (List.Remove(index))
            {
                taken = tab;
                if (shown)
                    await Pane.ShowTabAsync(List.Active);
            }
        });
        return taken;
    }

    /// <summary>The state of a tab as it is now (the shown one saved from the panel first), for copying it.</summary>
    public PanelTab Snapshot(int index)
    {
        if (index == List.ActiveIndex)
            Pane.SaveInto(List.Active);
        PanelTab tab = List.Tabs[index].NewFromHere();
        tab.Marked = new List<string>();
        return tab;
    }

    public void Move(int from, int to) => List.Move(from, to);

    public void ToggleLock(int index)
    {
        if (index < 0 || index >= List.Count)
            return;
        List.Tabs[index].Locked = !List.Tabs[index].Locked;
        Describe(Items[index], index == List.ActiveIndex);
    }

    /// <summary>Saves the shown tab, so the list can be stored with the settings.</summary>
    public void SaveShown() => Pane.SaveInto(List.Active);

    /// <summary>The paths of every tab of this side but the shown one (servers still in use).</summary>
    public IEnumerable<string> BackgroundPaths => List.Tabs.Where((_, i) => i != List.ActiveIndex).Select(t => t.Path);
}
