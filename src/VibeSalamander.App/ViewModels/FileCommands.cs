// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Windows.ApplicationModel.DataTransfer;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Interop;
using VibeSalamander.Core.Operations;
using VibeSalamander.Core.Settings;
using VibeSalamander.Services;

namespace VibeSalamander.ViewModels;

/// <summary>
/// The file commands of the active panel on a disk: copy and move (F5 / F6, paste, drag and drop), delete (F8),
/// rename in place (F2), new folder (F7) and new file (Shift+F4), attributes, case, the clipboard, properties.
/// The decisions are in Core (CopyTargetRules, DeletePlan, RenameRules, NewItemNames); copies and deletes run in
/// the transfer panel. Archives and servers have their own commands (they come first, see MainViewModel.ExecuteAsync).
/// </summary>
internal sealed class FileCommands
{
    private readonly MainViewModel _main;
    private readonly IUiHost _host;
    private readonly UiOperationCallbacks _callbacks;

    public FileCommands(MainViewModel main, IUiHost host, UiOperationCallbacks callbacks)
    {
        _main = main;
        _host = host;
        _callbacks = callbacks;
    }

    private PaneViewModel Active => _main.Active;
    private PaneViewModel Inactive => _main.Inactive;
    private AppSettings Settings => _main.Settings;

    // ---- copy and move ---------------------------------------------------------------------------------------

    /// <summary>F5 / F6: asks where to (unless Configuration says not to), then copies or moves in the transfer panel.</summary>
    public async Task CopyOrMoveAsync(bool move)
    {
        PaneViewModel active = Active;
        var items = active.GetOperationItems();
        if (items.Count == 0 || !active.IsLocalDisk)
            return;

        string what = items.Count == 1 ? $"\"{items[0].Entry.Name}\"" : Loc.F("{0} items", items.Count);
        // the other panel, also when it shows a ZIP or TAR (the files go into the archive)
        bool otherUsable = Inactive.IsLocalDisk || Inactive.IsRemote || ArchivePanelCommands.Editable(Inactive) != null;
        string defaultTarget = CopyTargetRules.DefaultTarget(Inactive.Path, otherUsable);

        string target;
        if (!CopyTargetRules.AskForTarget(Settings.AskCopyMoveTarget, otherUsable, active.Path, Inactive.Path))
        {
            target = defaultTarget;
        }
        else
        {
            var answer = await _host.Dialogs.CopyMoveTargetAsync(move, what, defaultTarget);
            _host.FocusPane(active);
            if (answer is not { } a)
                return;
            target = a.Target;
            if (a.DontAskAgain)
            {
                Settings.AskCopyMoveTarget = false;
                _main.SaveSettings();
            }
        }
        bool endsWithSeparator = target.TrimEnd().EndsWith('\\');
        string resolved = PathUtil.Resolve(active.Path, target);
        var sources = items.Select(i => i.Entry.FullPath).ToList();
        if (_main.RemoteProvider.FileSystemOf(resolved) is { } remote)
        {
            _main.ServerPanels.Upload(sources, remote, resolved, move);
            return;
        }
        if (!Directory.Exists(resolved) && _main.ArchiveProvider.Locate(resolved) is { } archive)
        {
            if (_main.ArchivePanels.EditableAt(resolved) is { } editable)
                _main.ArchivePanels.Add(sources, editable, move);
            else
                _host.Notify($"\"{Path.GetFileName(archive.ArchivePath)}\" is open read-only: copy files out with F5, or unpack it with Alt+F6 from its folder.", isError: false);
            return;
        }
        if (endsWithSeparator && !resolved.EndsWith('\\'))
            resolved += "\\"; // "target\" always means "into this directory"
        // Confirmations → "Do you want to create target path?"
        if (CopyTargetRules.IntoNewDirectory(resolved, items.Count, Directory.Exists(resolved)) && Settings.Confirm.CreateTargetPath &&
            !await _host.ConfirmAsync(move ? "Move" : "Copy", $"The target directory \"{resolved.TrimEnd('\\')}\" doesn't exist. Do you want to create it?", "Create"))
            return;
        await TransferAsync(sources, resolved, move);
    }

    /// <summary>
    /// Copies or moves in the background (F5/F6, paste, drag and drop): the operation is added to the transfer panel
    /// and this returns immediately, so the panels stay usable while it runs.
    /// </summary>
    public Task TransferAsync(IReadOnlyList<string> sources, string target, bool move)
    {
        if (sources.Count == 0)
            return Task.CompletedTask;
        if (_main.RemoteProvider.FileSystemOf(target) is { } remote)
        {
            _main.ServerPanels.Upload(sources, remote, target, move); // dropped or pasted onto an FTP / SFTP panel
            return Task.CompletedTask;
        }
        if (!Directory.Exists(target) && _main.ArchivePanels.EditableAt(target) is { } archive)
        {
            _main.ArchivePanels.Add(sources, archive, move); // dropped or pasted onto a ZIP / TAR panel
            return Task.CompletedTask;
        }
        // like Salamander, the marks are cleared once the operation is started; Ctrl+W brings them back
        Active.RememberSelection();
        Active.MarkAll(false);
        _main.Transfers.Enqueue(move ? OperationKind.Move : OperationKind.Copy, sources, target,
            (engine, ct) => move ? engine.MoveAsync(sources, target, ct) : engine.CopyAsync(sources, target, ct));
        return Task.CompletedTask;
    }

    /// <summary>Confirmations → Drag and Drop operations.</summary>
    public async Task<bool> ConfirmDropAsync(IReadOnlyList<string> paths, string target, bool move)
    {
        if (!Settings.Confirm.DragAndDrop)
            return true;
        string what = paths.Count == 1 ? $"\"{Path.GetFileName(paths[0].TrimEnd('\\'))}\"" : Loc.F("{0} items", paths.Count);
        return await _host.ConfirmAsync(move ? "Move" : "Copy",
            Loc.F(move ? "Do you want to move {0} to {1}?" : "Do you want to copy {0} to {1}?", what, target), move ? "Move" : "Copy");
    }

    // ---- delete ----------------------------------------------------------------------------------------------

    /// <summary>F8 / Del (Recycle Bin, or as Configuration → Recycle Bin says) and Shift+Del (permanently).</summary>
    public async Task DeleteAsync(bool permanent)
    {
        PaneViewModel active = Active;
        var items = active.GetOperationItems();
        if (items.Count == 0 || !active.IsLocalDisk)
            return;

        (List<FileItemViewModel> recycle, List<FileItemViewModel> remove) = DeletePlan.Split(items, permanent, Settings.Recycle.Mode, Settings.Recycle.Masks);

        // whole sentences for one file, one directory or several items (so they translate well)
        static string Ask(IReadOnlyList<FileItemViewModel> list, string file, string directory, string many) => list.Count == 1
            ? Loc.F(list[0].Entry.IsDirectory ? directory : file, list[0].Entry.Name)
            : Loc.F(many, list.Count);

        // Configuration → Confirmations
        if (recycle.Count > 0 && Settings.ConfirmRecycle &&
            !await _host.ConfirmAsync("Delete", Ask(recycle,
                "Do you want to move the file \"{0}\" to the Recycle Bin?",
                "Do you want to move the directory \"{0}\" to the Recycle Bin?",
                "Do you want to move the {0} selected items to the Recycle Bin?"), "Delete"))
            return;
        if (remove.Count > 0)
        {
            if (Settings.Confirm.PermanentDelete &&
                !await _host.ConfirmAsync("Delete", Ask(remove,
                    "Do you want to permanently delete the file \"{0}\"?\nThis cannot be undone.",
                    "Do you want to permanently delete the directory \"{0}\"?\nThis cannot be undone.",
                    "Do you want to permanently delete the {0} selected items?\nThis cannot be undone."), "Delete"))
                return;
            var hiddenFiles = remove.Where(i => !i.Entry.IsDirectory && (i.Entry.IsHidden || i.Entry.IsSystem)).ToList();
            if (hiddenFiles.Count > 0 && Settings.Confirm.HiddenSystemFileDelete &&
                !await _host.ConfirmAsync("Delete", Ask(hiddenFiles,
                    "The file \"{0}\" is a hidden or system file. Delete it anyway?",
                    "The file \"{0}\" is a hidden or system file. Delete it anyway?",
                    "{0} of the files are hidden or system files. Delete them anyway?"), "Delete"))
                return;
            var hiddenDirs = remove.Where(i => i.Entry.IsDirectory && (i.Entry.IsHidden || i.Entry.IsSystem)).ToList();
            if (hiddenDirs.Count > 0 && Settings.Confirm.HiddenSystemDirectoryDelete &&
                !await _host.ConfirmAsync("Delete", Ask(hiddenDirs,
                    "The directory \"{0}\" is a hidden or system directory. Delete it anyway?",
                    "The directory \"{0}\" is a hidden or system directory. Delete it anyway?",
                    "{0} of the directories are hidden or system directories. Delete them anyway?"), "Delete"))
                return;
            if (Settings.Confirm.NonEmptyDirectoryDelete)
            {
                var nonEmpty = remove.Where(i => i.Entry.IsDirectory && HasContent(i.Entry.FullPath)).ToList();
                if (nonEmpty.Count > 0 &&
                    !await _host.ConfirmAsync("Delete", nonEmpty.Count == 1
                        ? $"The directory \"{nonEmpty[0].Entry.Name}\" is not empty. Delete it with all its content?"
                        : $"{nonEmpty.Count} directories are not empty. Delete them with all their content?", "Delete All"))
                    return;
            }
        }

        int caret = active.CaretIndex;
        active.RememberSelection();
        if (remove.Count > 0)
        {
            // runs in the transfer panel; the panels refresh when it finishes
            active.MarkAll(false);
            var paths = remove.Select(i => i.Entry.FullPath).ToList();
            _main.Transfers.Enqueue(OperationKind.Delete, paths, null, (engine, ct) => engine.DeleteAsync(paths, ct));
        }
        if (recycle.Count > 0)
        {
            // the Recycle Bin is only reachable through the shell (it shows its own progress window)
            await ShellOperations.DeleteAsync(_host.WindowHandle, recycle.Select(i => i.Entry.FullPath).ToList(), permanent: false);
            await _main.RefreshAllAsync();
            _main.RequestCaretMove(active, Math.Min(caret, Math.Max(0, active.Items.Count - 1)));
        }
    }

    private static bool HasContent(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    // ---- names: rename, new folder, new file -----------------------------------------------------------------

    /// <summary>F2: rename in place (Salamander quick rename).</summary>
    public async Task RenameAsync()
    {
        PaneViewModel active = Active;
        FileItemViewModel? item = active.FocusedItem;
        if (item == null || item.Entry.IsUpDir || item.IsDrive || !active.IsLocalDisk)
            return;
        await RenameItemAsync(active, item, allowSubdirectories: false, selectWholeName: Settings.General.RenameSelectsExtension);
    }

    /// <summary>
    /// Edits the item's name in its row and renames it. Problems are reported with a notification, not a dialog.
    /// With <paramref name="allowSubdirectories"/> (a new folder) a typed "a\b" creates the path. Returns the item's
    /// path afterwards (unchanged when cancelled), or null when the rename failed.
    /// </summary>
    public async Task<string?> RenameItemAsync(PaneViewModel pane, FileItemViewModel item, bool allowSubdirectories, bool selectWholeName = false)
    {
        string oldPath = item.Entry.FullPath;
        string? typed = await _host.EditNameInPlaceAsync(pane, item, selectWholeName);
        RenameTarget rename = RenameRules.Resolve(pane.Path, item.Entry.Name, typed, item.Entry.IsDirectory, allowSubdirectories);
        if (rename.Unchanged)
            return oldPath;
        if (rename.Problem != null)
        {
            _host.Notify(rename.Problem);
            return null;
        }

        string target = rename.Path;
        try
        {
            if (item.Entry.IsDirectory)
            {
                if (rename.Nested)
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (rename.CaseOnly)
                {
                    string temp = oldPath + ".~rename";
                    Directory.Move(oldPath, temp);
                    Directory.Move(temp, target);
                }
                else
                {
                    Directory.Move(oldPath, target);
                }
            }
            else
            {
                File.Move(oldPath, target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _host.Notify(ex.Message);
            return null;
        }

        await pane.NavigateAsync(pane.Path, rename.FocusName, addToHistory: false);
        return target;
    }

    /// <summary>
    /// F7: creates "New Folder" in the panel and lets you name it in place, like Explorer (Esc keeps the name).
    /// Typing a path such as "a\b\c" creates the nested directories, as Salamander's F7 does.
    /// </summary>
    public async Task CreateDirectoryAsync()
    {
        PaneViewModel pane = Active;
        if (!pane.IsLocalDisk)
            return;
        string path = NewItemNames.UniquePath(pane.Path, "New Folder", string.Empty);
        Directory.CreateDirectory(path);
        await CreatedItemRenameAsync(pane, path, allowSubdirectories: true);
    }

    /// <summary>
    /// Shift+F4: creates "New Text Document.txt" in the panel and lets you name it in place (like Explorer), then
    /// opens it in the editor. Esc keeps the suggested name.
    /// </summary>
    public async Task EditNewFileAsync()
    {
        PaneViewModel pane = Active;
        if (!pane.IsLocalDisk)
            return;
        string path = NewItemNames.UniquePath(pane.Path, "New Text Document", ".txt");
        await File.WriteAllTextAsync(path, string.Empty);
        string? final = await CreatedItemRenameAsync(pane, path, allowSubdirectories: false);
        if (final != null && File.Exists(final))
            _main.EditWithoutRules(final);
    }

    /// <summary>Focuses a just created item and starts the in-place rename on it. Returns its final path.</summary>
    public async Task<string?> CreatedItemRenameAsync(PaneViewModel pane, string path, bool allowSubdirectories)
    {
        string name = Path.GetFileName(path);
        await pane.NavigateAsync(pane.Path, name, addToHistory: false);
        if (pane.FocusedItem is not { } focused || !string.Equals(focused.Entry.Name, name, StringComparison.OrdinalIgnoreCase))
            await pane.NavigateAsync(pane.Path, name, addToHistory: false); // an auto refresh (after creating it) replaced that load
        // like Explorer, a name that can't be used (exists already, invalid) puts you back into the edit
        for (int attempt = 0; attempt < 5; attempt++)
        {
            FileItemViewModel? item = pane.FocusedItem;
            if (item == null || !string.Equals(item.Entry.Name, name, StringComparison.OrdinalIgnoreCase))
                return path; // created, but not visible (e.g. filtered out)
            string? final = await RenameItemAsync(pane, item, allowSubdirectories,
                selectWholeName: !item.Entry.IsDirectory && Settings.General.EditNewSelectsExtension);
            if (final != null)
                return final;
            await pane.NavigateAsync(pane.Path, name, addToHistory: false);
        }
        return path;
    }

    /// <summary>Confirmations → "The directory doesn't exist. Do you want to create it?" Creates it when confirmed.</summary>
    public async Task<bool> CreateMissingDirectoryAsync(string path)
    {
        if (!Settings.Confirm.CreateMissingDirectory)
            return true; // navigating there shows the error
        if (!await _host.ConfirmAsync("Change Directory", $"The directory \"{path}\" doesn't exist. Do you want to create it?", "Create"))
            return false;
        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _host.Notify(ex.Message);
            return false;
        }
    }

    // ---- attributes, case ------------------------------------------------------------------------------------

    public async Task ChangeAttributesAsync()
    {
        PaneViewModel active = Active;
        var items = active.GetOperationItems();
        if (items.Count == 0)
            return;
        AttributeChange? change = await _host.Dialogs.AttributesAsync(items.Select(i => i.Entry).ToList());
        if (change == null)
            return;
        var result = await NameAndAttributeOperations.ChangeAttributesAsync(items.Select(i => i.Entry.FullPath).ToList(), change, _callbacks);
        await active.RefreshAsync();
        ReportProblems(result);
    }

    public async Task ChangeCaseAsync()
    {
        PaneViewModel active = Active;
        var items = active.GetOperationItems();
        if (items.Count == 0)
            return;
        var options = await _host.Dialogs.ChangeCaseAsync(items.Any(i => i.Entry.IsDirectory));
        if (options is not { } o)
            return;
        var result = await NameAndAttributeOperations.ChangeCaseAsync(items.Select(i => i.Entry.FullPath).ToList(),
            o.Mode, o.Part, o.Recursive, _callbacks);
        await active.RefreshAsync();
        ReportProblems(result);
    }

    private void ReportProblems(OperationResult? result)
    {
        if (result != null && result.Failed > 0)
            _host.Notify(Loc.F("Items that could not be processed: {0}.", result.Failed));
    }

    // ---- clipboard, properties, open with ---------------------------------------------------------------------

    public async Task ClipboardSetAsync(bool cut)
    {
        var items = Active.GetOperationItems();
        if (items.Count == 0)
            return;
        await FileClipboard.SetFilesAsync(items.Select(i => i.Entry.FullPath).ToList(), cut);
    }

    public async Task ClipboardPasteAsync()
    {
        PaneViewModel active = Active;
        if (!active.IsLocalDisk)
            return;
        var (paths, move) = await FileClipboard.GetFilesAsync();
        if (paths.Count == 0)
            return;
        string target = active.Path.EndsWith('\\') ? active.Path : active.Path + "\\";
        await TransferAsync(paths, target, move);
        if (move)
            Clipboard.Clear(); // cut + paste moves once, like Explorer
    }

    public void ShowProperties()
    {
        var items = Active.GetOperationItems();
        if (items.Count > 0)
            ShellOperations.ShowProperties(_host.WindowHandle, items[0].Entry.FullPath);
        else if (Active.FocusedItem?.Drive is { } drive)
            ShellOperations.ShowProperties(_host.WindowHandle, drive.Root);
    }

    public void OpenWith()
    {
        FileItemViewModel? item = Active.FocusedItem;
        if (item != null && !item.Entry.IsDirectory)
            ShellOperations.OpenWith(_host.WindowHandle, item.Entry.FullPath);
    }
}
