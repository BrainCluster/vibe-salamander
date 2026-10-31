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

// MainViewModel: smaller commands: hot paths, attributes, case, clipboard, create directory, command line.
public sealed partial class MainViewModel
{
    // ---- phase 3 helpers -------------------------------------------------------------------

    /// <summary>Alt+F10 (selection) / Ctrl+Shift+F10 (all directories): sizes go into the Size column, the total into a notification.</summary>
    private async Task CalculateOccupiedSpaceAsync(PaneViewModel pane, IReadOnlyList<FileItemViewModel> items)
    {
        if (!pane.IsLocalDisk || items.Count == 0)
            return;
        OccupiedSpace total = await pane.CalculateSizesAsync(items);
        _host.Notify(Loc.F("Occupied space: {0} ({1} bytes) in {2} files and {3} directories",
                     Formats.ShortSize(total.Bytes), Formats.ExactSize(total.Bytes), total.Files.ToString("N0"), total.Directories.ToString("N0")),
                     isError: false);
    }

    private async Task GoToHotPathAsync(int number)
    {
        string path = Settings.HotPaths[number];
        if (string.IsNullOrEmpty(path))
        {
            _host.Notify($"Hot path {number} isn't set. Ctrl+Shift+{number} makes the current folder hot path {number}.", isError: false);
            return;
        }
        await _active.NavigateAsync(path);
    }

    /// <summary>Ctrl+D (Total Commander) / Shift+F9: the hot paths as a menu; the last item makes this folder one.</summary>
    private void ShowHotPaths(PaneViewModel pane)
    {
        var entries = new List<MenuEntry> { MenuEntry.Header("Hot paths") };
        for (int number = 1; number <= 9; number++)
        {
            string path = Settings.HotPaths[number];
            if (string.IsNullOrEmpty(path))
                continue;
            string name = Settings.HotPathNames.Count > number && Settings.HotPathNames[number].Length > 0 ? Settings.HotPathNames[number] : path;
            string target = path;
            entries.Add(new MenuEntry(name == path ? path : $"{name}  —  {path}", () => _ = pane.NavigateAsync(target), "\uE728", $"Ctrl+{number}"));
        }
        if (entries.Count == 1)
            entries.Add(new MenuEntry("(none yet)", IsEnabled: false));
        int free = Enumerable.Range(1, 9).FirstOrDefault(n => string.IsNullOrEmpty(Settings.HotPaths[n]));
        if (free > 0 && !pane.IsThisPc && pane.IsLocalDisk)
        {
            entries.Add(MenuEntry.Separator);
            entries.Add(new MenuEntry(Loc.F("Add this folder as hot path {0}", free), () => SetHotPath(free), "\uE710", $"Ctrl+Shift+{free}"));
        }
        entries.Add(MenuEntry.Separator);
        entries.Add(new MenuEntry(Loc.T("Configure..."), () => _host.ShowConfigurationPage("HotPaths"), "\uE713"));
        _host.ShowMenu(pane, entries, atCaret: false);
    }

    /// <summary>Ctrl+B: every file of the folder and its subfolders in one list (names with their subfolders); again: back.</summary>
    private async Task FlatViewAsync(PaneViewModel pane)
    {
        if (FoundListProvider.IsFoundList(pane.Path))
        {
            await pane.GoUpAsync(); // back to the folder the list was made of
            return;
        }
        if (!pane.IsLocalDisk || pane.IsThisPc)
            return;
        string root = pane.Path;
        const int Limit = 200_000;
        var (files, more) = await Task.Run(() =>
        {
            var list = new List<FileEntry>();
            var options = new Core.Search.WalkOptions { IncludeHidden = Settings.ShowHidden };
            foreach (Core.Search.FileWalker.Found found in Core.Search.FileWalker.Walk(new[] { root }, options, null, CancellationToken.None))
            {
                if (found.Entry.IsDirectory)
                    continue;
                if (list.Count >= Limit)
                    return (list, true);
                list.Add(found.Entry);
            }
            return (list, false);
        });
        string title = Loc.F("All files in {0} ({1})", Path.GetFileName(root.TrimEnd('\\')) is { Length: > 0 } n ? n : root, files.Count.ToString("N0"));
        string path = FoundListProvider.Publish(title, root, files);
        await pane.NavigateAsync(path);
        if (more)
            _host.Notify(Loc.F("Only the first {0} files are listed.", Limit.ToString("N0")), isError: false);
    }

    /// <summary>The multi-rename tool on the marked items (or the one under the caret).</summary>
    private void OpenMultiRename(PaneViewModel pane)
    {
        var items = pane.GetOperationItems().Where(i => !i.Entry.IsUpDir && !i.IsDrive)
            .Select(i => new RenameSource(i.Entry.FullPath, i.Entry.IsDirectory, i.Entry.LastWrite)).ToList();
        if (items.Count == 0)
            return;
        _host.ShowMultiRename(pane, items);
    }

    private void SetHotPath(int number)
    {
        string path = _active.IsThisPc ? ThisPcProvider.RootPath : _active.Path;
        Settings.HotPaths[number] = path;
        SaveSettings();
        QuickBarsChanged?.Invoke(this, EventArgs.Empty);
        _host.Notify($"Hot path {number}: {(_active.IsThisPc ? "This PC" : path)}  (Ctrl+{number} goes there)", isError: false);
    }

    // ---- phase 2 helpers -------------------------------------------------------------------

    /// <summary>Help → Error Log: the panel goes to the log folder, on the newest log (Enter opens it).</summary>
    private async Task ShowErrorLogAsync(PaneViewModel pane)
    {
        if (Core.Diagnostics.ErrorLog.NewestFile() is not { } newest)
        {
            _host.Notify("No errors have been logged.", isError: false);
            return;
        }
        await pane.NavigateAsync(Core.Diagnostics.ErrorLog.Folder, Path.GetFileName(newest));
    }

    public Task ShowErrorAsync(string message)
    {
        _host.Notify(message);
        return Task.CompletedTask;
    }

    internal Task RefreshAllAsync() => Task.WhenAll(Left.RefreshAsync(), Right.RefreshAsync());

    private async Task FilterAsync()
    {
        string? mask = await _host.Dialogs.FilterAsync(_active.Filter);
        if (mask != null)
            _active.Filter = mask;
    }

    private async Task GoToShortcutTargetAsync()
    {
        FileItemViewModel? item = _active.FocusedItem;
        if (item == null || item.Entry.IsDirectory)
            return;
        string? target = ShellLink.GetTarget(item.Entry.FullPath);
        if (string.IsNullOrEmpty(target))
        {
            _host.Notify("The focused item is not a shortcut, or its target cannot be resolved.", isError: false);
            return;
        }
        if (Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) && !uri.IsFile)
        {
            ShellOperations.Open(target); // Internet shortcut
            return;
        }
        string? directory = Path.GetDirectoryName(target.TrimEnd('\\'));
        if (directory == null)
            await _active.NavigateAsync(target); // a drive root
        else
            await _active.NavigateAsync(directory, Path.GetFileName(target.TrimEnd('\\')));
    }

    private async Task MarkByMaskAsync(bool mark)
    {
        var answer = await _host.Dialogs.MaskAsync(mark ? "Select" : "Unselect", _lastSelectMask, _lastSelectDirectories);
        if (answer is not { } a)
            return;
        _lastSelectMask = a.Mask;
        _lastSelectDirectories = a.IncludeDirectories;
        _active.MarkByMask(a.Mask, a.IncludeDirectories, mark);
    }

    private void MoveToMarked(PaneViewModel pane, int direction)
    {
        int index = pane.FindMarked(direction);
        if (index >= 0)
            RequestCaretMove(pane, index);
    }

    /// <summary>Shift+F7: type a path straight into the address bar.</summary>
    private void ChangeDirectory() => _host.EditPath(_active);

    /// <summary>Executes the command line text in the active panel's directory.</summary>
    public async Task RunCommandLineAsync(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return;
        if (Settings.History.CommandLineHistory)
            RememberRecent(Settings.CommandLineHistory, text);
        // "cd <path>" navigates, like Salamander's command line
        if (text.StartsWith("cd ", StringComparison.OrdinalIgnoreCase) || (text.Length == 2 && text[1] == ':'))
        {
            string arg = text.Length == 2 ? text : text[3..];
            if (_active.IsLocalDisk || System.IO.Path.IsPathRooted(arg))
                await _active.NavigateAsync(PathUtil.Resolve(_active.IsLocalDisk ? _active.Path : @"C:\", arg));
            return;
        }
        if (_active.IsLocalDisk)
            ShellOperations.RunCommandLine(_active.Path, text, Settings.General.CloseShellAfterCommand);
    }
}
