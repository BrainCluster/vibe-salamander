// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Windows.ApplicationModel.DataTransfer;
using VibeSalamander.Core.Commands;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Interop;
using VibeSalamander.Core.Operations;
using VibeSalamander.Core.Settings;
using VibeSalamander.Core.Tools;
using VibeSalamander.Plugins;
using VibeSalamander.Services;

namespace VibeSalamander.ViewModels;

/// <summary>
/// A panel that shows an archive's content: F5 / Alt+F6 copy the selected items out, F3 / F4 open a temporary copy.
/// In ZIP and TAR archives F6 moves out, F8 deletes and Ctrl+V pastes in; other changes say what can be done instead
/// (Core's PanelCommandRules decides). Files also go into an archive from other panels (F5, drag and drop, paste).
/// Everything that takes time runs in the transfer panel.
/// </summary>
internal sealed class ArchivePanelCommands
{
    private readonly MainViewModel _main;
    private readonly IUiHost _host;

    public ArchivePanelCommands(MainViewModel main, IUiHost host)
    {
        _main = main;
        _host = host;
    }

    private AppSettings Settings => _main.Settings;
    private PaneViewModel Inactive => _main.Inactive;

    /// <summary>Folder for files opened from archives and servers (emptied when the app closes).</summary>
    public static string TempRoot => Path.Combine(Path.GetTempPath(), "VibeSalamander", "FromArchives");

    /// <summary>Runs the command when it works differently in an archive; true when it was handled here.</summary>
    public async Task<bool> TryHandleAsync(PaneViewModel p, CommandId command)
    {
        ArchiveLocation? editable = Editable(p);
        switch (PanelCommandRules.InArchive(command, editable != null))
        {
            case ArchivePanelAction.CopyOut:
                await CopyOutAsync(p, move: false);
                return true;
            case ArchivePanelAction.MoveOut:
                await CopyOutAsync(p, move: true);
                return true;
            case ArchivePanelAction.Delete:
                await DeleteAsync(p, editable!);
                return true;
            case ArchivePanelAction.PasteIn:
                var (paths, cut) = await FileClipboard.GetFilesAsync();
                if (paths.Count > 0)
                {
                    Add(paths, editable!, cut);
                    if (cut)
                        Clipboard.Clear();
                }
                return true;
            case ArchivePanelAction.OpenCopy:
                if (p.FocusedItem is { Entry.IsDirectory: false, Entry.IsUpDir: false } item)
                    await OpenCopyAsync(p, item);
                return true;
            case ArchivePanelAction.ExplainEditable:
            case ArchivePanelAction.ExplainReadOnly:
                string name = p.Archive is { } location ? Path.GetFileName(location.ArchivePath) : "The archive";
                if (editable != null)
                    _host.Notify(Loc.F("In \"{0}\" files can be copied in and out (F5, F6) and deleted (F8).", name), isError: false);
                else
                    _host.Notify($"\"{name}\" is open read-only: copy files out with F5, or unpack it with Alt+F6 from its folder.", isError: false);
                return true;
            default:
                return false;
        }
    }

    // ---- which archives can be changed ------------------------------------------------------------------------

    /// <summary>Where the panel is in an archive that can be changed (ZIP, TAR), or null.</summary>
    public static ArchiveLocation? Editable(PaneViewModel p) =>
        p.Archive is { } location && SafeCanEdit(location.Archiver, location.ArchivePath) ? location : null;

    /// <summary>The archive (that can be changed) a target path points into, or null.</summary>
    public ArchiveLocation? EditableAt(string path) =>
        _main.ArchiveProvider.Locate(path) is { } location && SafeCanEdit(location.Archiver, location.ArchivePath) ? location : null;

    private static bool SafeCanEdit(IArchiver archiver, string path)
    {
        try
        {
            return archiver.CanEdit(path);
        }
        catch (Exception)
        {
            return false; // a broken plugin: its archives stay read-only
        }
    }

    // ---- in and out ---------------------------------------------------------------------------------------------

    /// <summary>
    /// F5 / F6 / drag and drop / paste into a panel showing a ZIP or TAR: the files go into the archive (in the
    /// transfer panel). Moving deletes the originals only when everything went in.
    /// </summary>
    public void Add(IReadOnlyList<string> sources, ArchiveLocation location, bool move)
    {
        if (sources.Count == 0)
            return;
        _main.Active.RememberSelection();
        _main.Active.MarkAll(false);
        var compression = Enum.TryParse(Settings.Plugins.PackCompression, out ArchiveCompression c) ? c : ArchiveCompression.Normal;
        string display = ArchiveFileSystemProvider.Combine(location.ArchivePath, location.InnerPath);
        _main.Transfers.EnqueueJob(move ? OperationKind.Move : OperationKind.Copy, sources, display, async job =>
        {
            OperationResult result = await Task.Run(() => location.Archiver.AddAsync(new ArchiveAddRequest
            {
                ArchivePath = location.ArchivePath,
                Sources = sources,
                TargetPath = location.InnerPath,
                Compression = compression,
            }, job));
            // never delete a directory the archive itself is in
            if (move && PanelCommandRules.Completed(result) && !PanelCommandRules.IsInsideAny(location.ArchivePath, sources))
            {
                var engine = new FileOperationEngine(new JobCallbacks(job));
                OperationResult deleted = await engine.DeleteAsync(sources, job.Cancellation);
                result.Failed += deleted.Failed;
            }
            return result;
        });
    }

    /// <summary>F8 in a ZIP or TAR: deletes the selected files and directories from the archive (in the transfer panel).</summary>
    private async Task DeleteAsync(PaneViewModel p, ArchiveLocation location)
    {
        var items = p.GetOperationItems().Where(i => !i.Entry.IsUpDir).ToList();
        if (items.Count == 0)
            return;
        if (Settings.Confirm.PermanentDelete && !await _host.ConfirmAsync("Delete", items.Count == 1
                ? Loc.F("Do you want to delete \"{0}\" from the archive?\nThis cannot be undone.", items[0].Entry.Name)
                : Loc.F("Do you want to delete the {0} selected items from the archive?\nThis cannot be undone.", items.Count), "Delete"))
            return;
        var inner = items.Select(i => location.ItemPath(i.Entry.Name)).ToList();
        p.RememberSelection();
        p.MarkAll(false);
        _main.Transfers.EnqueueJob(OperationKind.Delete, items.Select(i => i.Entry.FullPath).ToList(), null, job =>
            Task.Run(() => location.Archiver.DeleteAsync(new ArchiveDeleteRequest
            {
                ArchivePath = location.ArchivePath,
                Items = inner,
                BasePath = location.InnerPath,
            }, job)));
    }

    /// <summary>
    /// F5 in an archive: unpacks the selected files and directories into a directory (the other panel by default).
    /// F6 (ZIP, TAR) then deletes them from the archive, when everything came out.
    /// </summary>
    private async Task CopyOutAsync(PaneViewModel p, bool move)
    {
        if (p.Archive is not { } location)
            return;
        var items = p.GetOperationItems().Where(i => !i.Entry.IsUpDir).ToList();
        if (items.Count == 0)
            return;
        string what = items.Count == 1 ? $"\"{items[0].Entry.Name}\"" : Loc.F("{0} items", items.Count);
        string defaultTarget = CopyTargetRules.DefaultTarget(Inactive.Path, Inactive.IsLocalDisk);
        string target;
        if (!PanelCommandRules.AskForTargetOnDisk(Settings.AskCopyMoveTarget, Inactive.IsLocalDisk))
        {
            target = defaultTarget;
        }
        else
        {
            var answer = await _host.Dialogs.CopyMoveTargetAsync(move, what, defaultTarget);
            _host.FocusPane(p);
            if (answer is not { } a)
                return;
            target = a.Target;
            if (a.DontAskAgain)
            {
                Settings.AskCopyMoveTarget = false;
                _main.SaveSettings();
            }
        }
        string directory = PathUtil.Resolve(Inactive.IsLocalDisk ? Inactive.Path : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), target);
        if (_main.ArchiveProvider.CanHandle(directory) || !Path.IsPathRooted(directory))
        {
            _host.Notify("Files can only be copied out of an archive into a directory on disk.");
            return;
        }
        if (!Directory.Exists(directory) && Settings.Confirm.CreateTargetPath &&
            !await _host.ConfirmAsync(move ? "Move" : "Copy", $"The target directory \"{directory.TrimEnd('\\')}\" doesn't exist. Do you want to create it?", "Create"))
            return;

        var inner = items.Select(i => location.ItemPath(i.Entry.Name)).ToList();
        p.RememberSelection();
        p.MarkAll(false);
        _main.Transfers.EnqueueJob(move ? OperationKind.Move : OperationKind.Copy, items.Select(i => i.Entry.FullPath).ToList(), directory, async job =>
        {
            OperationResult result = await Task.Run(() => location.Archiver.UnpackAsync(new UnpackRequest
            {
                ArchivePath = location.ArchivePath,
                TargetDirectory = directory,
                Items = inner,
                BasePath = location.InnerPath,
            }, job));
            if (move && PanelCommandRules.Completed(result))
            {
                OperationResult deleted = await Task.Run(() => location.Archiver.DeleteAsync(new ArchiveDeleteRequest
                {
                    ArchivePath = location.ArchivePath,
                    Items = inner,
                    BasePath = location.InnerPath,
                }, job));
                result.Failed += deleted.Failed;
            }
            return result;
        });
    }

    /// <summary>Enter / F3 on a file in an archive: unpacks it into a temporary folder and opens it.</summary>
    public Task OpenCopyAsync(PaneViewModel p, FileItemViewModel item)
    {
        if (p.Archive is not { } location || item.Entry.IsDirectory)
            return Task.CompletedTask;
        string directory = Path.Combine(TempRoot, Guid.NewGuid().ToString("N")[..8]);
        string file = Path.Combine(directory, item.Entry.Name);
        string inner = location.ItemPath(item.Entry.Name);
        _main.Transfers.EnqueueJob(OperationKind.Unpack, new[] { item.Entry.FullPath }, directory, async job =>
        {
            OperationResult result = await Task.Run(() => location.Archiver.UnpackAsync(new UnpackRequest
            {
                ArchivePath = location.ArchivePath,
                TargetDirectory = directory,
                Items = new[] { inner },
                BasePath = location.InnerPath,
            }, job));
            // back on the UI thread: open it with its program (Configuration → Viewers, else the default app)
            if (!result.Cancelled && File.Exists(file))
            {
                FileItemViewModel opened = new(new FileEntry(item.Entry.Name, file, false, item.Entry.Size, item.Entry.LastWrite, FileAttributes.ReadOnly), p.Columns);
                PaneViewModel other = p == _main.Left ? _main.Right : _main.Left;
                // the rule's $(FullName) / $(Path) must be the temporary copy, not the path inside the archive
                var context = new UserMenuContext
                {
                    Path = directory,
                    OtherPath = other.IsLocalDisk ? other.Path : string.Empty,
                    FullName = file,
                    Selected = new[] { file },
                };
                if (!_main.RunProgramRule(Settings.Viewers, opened, context))
                    ShellOperations.Open(file, directory);
            }
            return result;
        });
        return Task.CompletedTask;
    }

    /// <summary>At exit: removes the temporary copies of files opened from archives and servers (best effort).</summary>
    public static void CleanTemp()
    {
        try
        {
            if (Directory.Exists(TempRoot))
                Directory.Delete(TempRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a file is still open in its program: it stays until the next time
        }
    }
}
