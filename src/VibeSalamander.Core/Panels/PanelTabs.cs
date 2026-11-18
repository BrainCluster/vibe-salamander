// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Serialization;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Localization;

namespace VibeSalamander.Core.Panels;

/// <summary>
/// One tab of a panel: the panel's state while another tab is shown. A side has one live panel; switching tabs
/// saves its state into the tab it leaves and restores the one it goes to. Path, sort, view and lock are saved with
/// the settings; the rest lives only while the program runs.
/// </summary>
public sealed class PanelTab
{
    public string Path { get; set; } = string.Empty;
    public SortMode SortMode { get; set; } = SortMode.Name;
    public bool SortDescending { get; set; }
    public ViewMode ViewMode { get; set; } = ViewMode.Detailed;

    /// <summary>The tab keeps its folder: going elsewhere from it opens a new tab next to it.</summary>
    public bool Locked { get; set; }

    /// <summary>The file or folder the caret was on.</summary>
    [JsonIgnore] public string? FocusName { get; set; }

    /// <summary>Back / forward history (Alt+Left / Alt+Right), oldest first.</summary>
    [JsonIgnore] public List<string> Back { get; set; } = new();
    [JsonIgnore] public List<string> Forward { get; set; } = new();

    /// <summary>The marked names.</summary>
    [JsonIgnore] public List<string> Marked { get; set; } = new();

    /// <summary>The quick filter (Ctrl+F12), empty when none.</summary>
    [JsonIgnore] public string Filter { get; set; } = string.Empty;

    /// <summary>A copy: a new tab "of" this one (same place, sort and view; its own history; not locked, nothing marked).</summary>
    public PanelTab NewFromHere() => new()
    {
        Path = Path,
        SortMode = SortMode,
        SortDescending = SortDescending,
        ViewMode = ViewMode,
        FocusName = FocusName,
        Back = new List<string>(Back),
        Forward = new List<string>(Forward),
        Filter = Filter,
    };

    /// <summary>
    /// The tab's caption: the folder's name; "C:" for a drive; This PC; an archive's name at its root; a server's
    /// host at its root.
    /// </summary>
    public static string Title(string path)
    {
        if (string.IsNullOrEmpty(path))
            return "…";
        if (path.StartsWith(ThisPcProvider.RootPath, StringComparison.OrdinalIgnoreCase))
            return L.T("This PC");
        int scheme = path.IndexOf("://", StringComparison.Ordinal);
        if (scheme > 0)
        {
            string rest = path[(scheme + 3)..].TrimEnd('/');
            int slash = rest.LastIndexOf('/');
            string name = slash < 0 ? rest : rest[(slash + 1)..];
            if (slash < 0)
            {
                int at = name.LastIndexOf('@');
                if (at < 0)
                    at = name.LastIndexOf("%40", StringComparison.Ordinal) is int encoded and >= 0 ? encoded + 2 : -1;
                if (at >= 0)
                    name = name[(at + 1)..]; // the host, without the user
            }
            return Uri.UnescapeDataString(name);
        }
        string trimmed = path.TrimEnd('\\');
        if (trimmed.Length == 2 && trimmed[1] == ':')
            return trimmed.ToUpperInvariant(); // a drive
        string last = System.IO.Path.GetFileName(trimmed);
        return last.Length > 0 ? last : trimmed;
    }
}

/// <summary>
/// The tabs of one side and which one is shown. Pure list logic (no panel): which tab becomes active after a close,
/// where a new tab goes, reopening a closed tab, moving tabs.
/// </summary>
public sealed class TabList
{
    private const int ClosedKept = 10;
    private readonly List<PanelTab> _tabs = new();
    private readonly List<(PanelTab Tab, int Index)> _closed = new();
    private int _active;

    /// <summary>The tabs saved in the settings (none: one tab, from <paramref name="fallback"/>).</summary>
    public TabList(IEnumerable<PanelTab>? tabs, int activeIndex, PanelTab fallback)
    {
        if (tabs != null)
            _tabs.AddRange(tabs.Where(t => t != null && !string.IsNullOrWhiteSpace(t.Path)));
        if (_tabs.Count == 0)
            _tabs.Add(fallback);
        _active = Math.Clamp(activeIndex, 0, _tabs.Count - 1);
    }

    public IReadOnlyList<PanelTab> Tabs => _tabs;
    public int Count => _tabs.Count;
    public int ActiveIndex => _active;
    public PanelTab Active => _tabs[_active];
    public bool CanReopen => _closed.Count > 0;

    /// <summary>Something changed: tabs, their order or the active one.</summary>
    public event EventHandler? Changed;

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>The active tab, by index (does nothing for an index out of range).</summary>
    public void Select(int index)
    {
        if (index < 0 || index >= _tabs.Count || index == _active)
            return;
        _active = index;
        RaiseChanged();
    }

    /// <summary>Adds a tab right after the active one (or at <paramref name="index"/>); returns where it went.</summary>
    public int Insert(PanelTab tab, int? index = null, bool activate = true)
    {
        int at = Math.Clamp(index ?? _active + 1, 0, _tabs.Count);
        _tabs.Insert(at, tab);
        if (activate)
            _active = at;
        else if (at <= _active)
            _active++;
        RaiseChanged();
        return at;
    }

    /// <summary>
    /// Closes a tab; the last one stays (false). Closing the active tab shows the one to its right, or the one to
    /// its left when it was the last, as browsers do.
    /// </summary>
    public bool Close(int index)
    {
        if (_tabs.Count <= 1 || index < 0 || index >= _tabs.Count)
            return false;
        _closed.Add((_tabs[index], index));
        if (_closed.Count > ClosedKept)
            _closed.RemoveAt(0);
        _tabs.RemoveAt(index);
        if (index < _active || _active >= _tabs.Count)
            _active--;
        RaiseChanged();
        return true;
    }

    /// <summary>Closes every tab but <paramref name="keep"/> (locked tabs too: the user asked for it).</summary>
    public void CloseOthers(int keep)
    {
        if (keep < 0 || keep >= _tabs.Count || _tabs.Count == 1)
            return;
        PanelTab kept = _tabs[keep];
        for (int i = _tabs.Count - 1; i >= 0; i--)
            if (i != keep)
                _closed.Add((_tabs[i], i));
        while (_closed.Count > ClosedKept)
            _closed.RemoveAt(0);
        _tabs.Clear();
        _tabs.Add(kept);
        _active = 0;
        RaiseChanged();
    }

    /// <summary>The last closed tab, back where it was, and shown. Null: none to reopen.</summary>
    public PanelTab? Reopen()
    {
        if (_closed.Count == 0)
            return null;
        (PanelTab tab, int index) = _closed[^1];
        _closed.RemoveAt(_closed.Count - 1);
        Insert(tab, index);
        return tab;
    }

    /// <summary>Moves a tab (dragging it along the row); the active tab stays the same tab.</summary>
    public void Move(int from, int to)
    {
        if (from < 0 || from >= _tabs.Count || from == to)
            return;
        to = Math.Clamp(to, 0, _tabs.Count - 1);
        PanelTab activeTab = _tabs[_active];
        PanelTab moved = _tabs[from];
        _tabs.RemoveAt(from);
        _tabs.Insert(to, moved);
        _active = _tabs.IndexOf(activeTab);
        RaiseChanged();
    }

    /// <summary>Takes a tab out (moving it to the other side); the last one stays (false). Not kept for reopening.</summary>
    public bool Remove(int index)
    {
        if (_tabs.Count <= 1 || index < 0 || index >= _tabs.Count)
            return false;
        _tabs.RemoveAt(index);
        if (index < _active || _active >= _tabs.Count)
            _active--;
        RaiseChanged();
        return true;
    }

    /// <summary>The next tab (Ctrl+Tab), from the last to the first.</summary>
    public int Next => (_active + 1) % _tabs.Count;

    /// <summary>The previous tab (Ctrl+Shift+Tab), from the first to the last.</summary>
    public int Previous => (_active - 1 + _tabs.Count) % _tabs.Count;

    /// <summary>Exchanges all tabs with the other side (Ctrl+U swaps the panels with their tabs).</summary>
    public void SwapWith(TabList other)
    {
        var mine = new List<PanelTab>(_tabs);
        int myActive = _active;
        _tabs.Clear();
        _tabs.AddRange(other._tabs);
        _active = other._active;
        other._tabs.Clear();
        other._tabs.AddRange(mine);
        other._active = myActive;
        RaiseChanged();
        other.RaiseChanged();
    }
}
