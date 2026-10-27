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

    private void SetHotPath(int number)
    {
        string path = _active.IsThisPc ? ThisPcProvider.RootPath : _active.Path;
        Settings.HotPaths[number] = path;
        SaveSettings();
        QuickBarsChanged?.Invoke(this, EventArgs.Empty);
        _host.Notify($"Hot path {number}: {(_active.IsThisPc ? "This PC" : path)}  (Ctrl+{number} goes there)", isError: false);
    }

    // ---- phase 2 helpers -------------------------------------------------------------------

    private Task ReportProblemsAsync(OperationResult? result)
    {
        if (result != null && result.Failed > 0)
            _host.Notify(Loc.F("Items that could not be processed: {0}.", result.Failed));
        return Task.CompletedTask;
    }

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

    private Task RefreshAllAsync() => Task.WhenAll(Left.RefreshAsync(), Right.RefreshAsync());

    private async Task FilterAsync()
    {
        string? mask = await _host.Dialogs.FilterAsync(_active.Filter);
        if (mask != null)
            _active.Filter = mask;
    }

    private void OpenWith()
    {
        FileItemViewModel? item = _active.FocusedItem;
        if (item != null && !item.Entry.IsDirectory)
            ShellOperations.OpenWith(_host.WindowHandle, item.Entry.FullPath);
    }

    private async Task ChangeAttributesAsync()
    {
        var items = _active.GetOperationItems();
        if (items.Count == 0)
            return;
        AttributeChange? change = await _host.Dialogs.AttributesAsync(items.Select(i => i.Entry).ToList());
        if (change == null)
            return;
        var result = await NameAndAttributeOperations.ChangeAttributesAsync(items.Select(i => i.Entry.FullPath).ToList(), change, _callbacks);
        await _active.RefreshAsync();
        await ReportProblemsAsync(result);
    }

    private async Task ChangeCaseAsync()
    {
        var items = _active.GetOperationItems();
        if (items.Count == 0)
            return;
        var options = await _host.Dialogs.ChangeCaseAsync(items.Any(i => i.Entry.IsDirectory));
        if (options is not { } o)
            return;
        var result = await NameAndAttributeOperations.ChangeCaseAsync(items.Select(i => i.Entry.FullPath).ToList(),
            o.Mode, o.Part, o.Recursive, _callbacks);
        await _active.RefreshAsync();
        await ReportProblemsAsync(result);
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
            CaretMoveRequested?.Invoke(this, (pane, index));
    }

    private async Task ClipboardSetAsync(bool cut)
    {
        var items = _active.GetOperationItems();
        if (items.Count == 0)
            return;
        await FileClipboard.SetFilesAsync(items.Select(i => i.Entry.FullPath).ToList(), cut);
    }

    private async Task ClipboardPasteAsync()
    {
        if (!_active.IsLocalDisk)
            return;
        var (paths, move) = await FileClipboard.GetFilesAsync();
        if (paths.Count == 0)
            return;
        string target = _active.Path.EndsWith('\\') ? _active.Path : _active.Path + "\\";
        await TransferAsync(paths, target, move);
        if (move)
            Clipboard.Clear(); // cut + paste moves once, like Explorer
    }

    /// <summary>
    /// F7: creates "New Folder" in the panel and lets you name it in place, like Explorer (Esc keeps the
    /// name). Typing a path such as "a\b\c" creates the nested directories, as Salamander's F7 does.
    /// </summary>
    private async Task CreateDirectoryAsync()
    {
        PaneViewModel pane = _active;
        if (!pane.IsLocalDisk)
            return;
        string path = UniquePath(pane.Path, "New Folder", string.Empty);
        Directory.CreateDirectory(path);
        await CreatedItemRenameAsync(pane, path, allowSubdirectories: true);
    }

    /// <summary>Shift+F7: type a path straight into the address bar.</summary>
    private void ChangeDirectory() => _host.EditPath(_active);

    private void ShowProperties()
    {
        var items = _active.GetOperationItems();
        if (items.Count > 0)
            ShellOperations.ShowProperties(_host.WindowHandle, items[0].Entry.FullPath);
        else if (_active.FocusedItem?.Drive is { } drive)
            ShellOperations.ShowProperties(_host.WindowHandle, drive.Root);
    }

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
