// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

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
/// Panels showing a plugin file system (FTP, SFTP): F5 / F6 copy and move to the other panel (download),
/// files from a disk panel, drag and drop or Ctrl+V go up (upload), F8 deletes, F7 makes a directory,
/// F2 renames, Enter / F3 / F4 open a temporary copy. Everything that takes time runs in the transfer panel.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Plugin file systems (FTP, SFTP) in the panels.</summary>
    public RemoteFileSystemProvider RemoteProvider { get; }

    /// <summary>
    /// A panel leaves a server: "Disconnect from ...?" Yes closes the connection (unless the other panel
    /// is on the same server, then nothing is asked); No keeps the panel where it is.
    /// </summary>
    private async Task<bool> ConfirmLeaveServerAsync(PaneViewModel pane, string serverPath)
    {
        PaneViewModel other = pane == Left ? Right : Left;
        if (other.IsRemote && RemoteFileSystemProvider.SameServer(other.Path, serverPath))
            return true; // still in use there
        IPluginFileSystem? fs = RemoteProvider.FileSystemOf(serverPath);
        if (fs == null || !SafeIsConnected(fs, serverPath))
            return true; // nothing to disconnect (already disconnected)
        string server = RemoteFileSystemProvider.ServerOf(serverPath).Replace("%40", "@");
        if (!await _host.ConfirmAsync("Disconnect", Loc.F("Do you want to disconnect from {0}?", server), "Disconnect"))
        {
            _host.FocusPane(pane);
            return false;
        }
        try
        {
            fs.Disconnect(serverPath);
        }
        catch (Exception ex)
        {
            _host.Notify(ex.Message);
        }
        return true;
    }

    private static bool SafeIsConnected(IPluginFileSystem fs, string path)
    {
        try
        {
            return fs.IsConnected(path);
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>F12 in a panel on a server: disconnects from it; the panels on it go to This PC.</summary>
    private async Task DisconnectServerAsync(PaneViewModel p, IPluginFileSystem fs)
    {
        string server = p.Path;
        try
        {
            fs.Disconnect(server);
        }
        catch (Exception ex)
        {
            _host.Notify(ex.Message);
        }
        foreach (PaneViewModel pane in new[] { Left, Right })
            if (pane.IsRemote && RemoteFileSystemProvider.SameServer(pane.Path, server))
                await pane.NavigateAsync(ThisPcProvider.RootPath);
    }

    /// <summary>Handles a command in a panel showing a server; true when it was handled here.</summary>
    private async Task<bool> RemoteCommandAsync(PaneViewModel p, CommandId command)
    {
        if (RemoteProvider.FileSystemOf(p.Path) is not { } fs)
            return false;
        switch (command)
        {
            case CommandId.Copy:
            case CommandId.Unpack:
                await DownloadFromRemoteAsync(p, fs, move: false);
                return true;
            case CommandId.Move:
                await DownloadFromRemoteAsync(p, fs, move: true);
                return true;
            case CommandId.Delete:
            case CommandId.DeletePermanently:
                await DeleteRemoteAsync(p, fs);
                return true;
            case CommandId.CreateDirectory:
                await CreateRemoteDirectoryAsync(p, fs);
                return true;
            case CommandId.DisconnectNetworkDrive:
                await DisconnectServerAsync(p, fs);
                return true;
            case CommandId.QuickRename:
                await RenameRemoteAsync(p, fs);
                return true;
            case CommandId.View:
                if (p.FocusedItem is { Entry.IsDirectory: false, Entry.IsUpDir: false } viewed)
                    await OpenFromRemoteAsync(p, viewed, Settings.Viewers);
                return true;
            case CommandId.Edit:
                if (p.FocusedItem is { Entry.IsDirectory: false, Entry.IsUpDir: false } edited)
                    await OpenFromRemoteAsync(p, edited, Settings.Editors);
                return true;
            case CommandId.ClipboardPaste:
                var (paths, cut) = await FileClipboard.GetFilesAsync();
                if (paths.Count > 0)
                {
                    UploadToRemote(paths, fs, p.Path, cut);
                    if (cut)
                        Windows.ApplicationModel.DataTransfer.Clipboard.Clear();
                }
                return true;
            case CommandId.EditNewFile:
            case CommandId.ChangeAttributes:
            case CommandId.ChangeCase:
            case CommandId.ClipboardCut:
            case CommandId.ClipboardCopy:
            case CommandId.Pack:
            case CommandId.NewMenu:
            case CommandId.CommandShell:
            case CommandId.OpenActiveFolder:
                _host.Notify(Loc.F("On {0} files can be copied (F5, F6), deleted (F8), renamed (F2) and opened; new directories with F7.",
                    fs.DisplayName), isError: false);
                return true;
            default:
                return false;
        }
    }

    /// <summary>F5 / F6 on a server: downloads the selected items into a directory (the other panel by default).</summary>
    private async Task DownloadFromRemoteAsync(PaneViewModel p, IPluginFileSystem fs, bool move)
    {
        var items = p.GetOperationItems().Where(i => !i.Entry.IsUpDir).ToList();
        if (items.Count == 0)
            return;
        if (Inactive.IsRemote && RemoteProvider.FileSystemOf(Inactive.Path) is { } other)
        {
            // server to server: through a temporary folder here
            await CopyBetweenServersAsync(p, fs, items, other, Inactive.Path, move);
            return;
        }
        string what = items.Count == 1 ? $"\"{items[0].Entry.Name}\"" : Loc.F("{0} items", items.Count);
        string defaultTarget = Inactive.IsLocalDisk ? Inactive.Path : string.Empty;
        if (defaultTarget.Length > 0 && !defaultTarget.EndsWith('\\'))
            defaultTarget += "\\";
        string target;
        if (!Settings.AskCopyMoveTarget && Inactive.IsLocalDisk)
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
                SaveSettings();
            }
        }
        string directory = PathUtil.Resolve(Inactive.IsLocalDisk ? Inactive.Path : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), target);
        if (RemoteProvider.FileSystemOf(directory) is { } targetFs)
        {
            await CopyBetweenServersAsync(p, fs, items, targetFs, directory, move);
            return;
        }
        if (ArchiveProvider.CanHandle(directory) || !Path.IsPathRooted(directory))
        {
            _host.Notify("Files can only be downloaded into a directory on disk.");
            return;
        }
        if (!Directory.Exists(directory) && Settings.Confirm.CreateTargetPath &&
            !await _host.ConfirmAsync(move ? "Move" : "Copy", $"The target directory \"{directory.TrimEnd('\\')}\" doesn't exist. Do you want to create it?", "Create"))
            return;

        var remote = items.Select(i => RemoteFileSystemProvider.ItemOf(i.Entry)).ToList();
        p.RememberSelection();
        p.MarkAll(false);
        Transfers.EnqueueJob(move ? OperationKind.Move : OperationKind.Copy, remote.Select(r => r.Path).ToList(), directory, async job =>
        {
            Directory.CreateDirectory(directory);
            OperationResult result = await Task.Run(() => fs.DownloadAsync(remote, directory, job));
            // moving: deleted on the server only when everything came down
            if (move && !result.Cancelled && result.Failed == 0 && result.Skipped == 0)
            {
                OperationResult deleted = await Task.Run(() => fs.DeleteAsync(remote, job));
                result.Failed += deleted.Failed;
            }
            return result;
        });
    }

    /// <summary>From one server to another (or another folder of the same one): down into a temporary folder, then up.</summary>
    private Task CopyBetweenServersAsync(PaneViewModel p, IPluginFileSystem fs, List<FileItemViewModel> items,
        IPluginFileSystem targetFs, string targetDirectory, bool move)
    {
        var remote = items.Select(i => RemoteFileSystemProvider.ItemOf(i.Entry)).ToList();
        string temp = Path.Combine(Path.GetTempPath(), "VibeSalamander", "Transfer", Guid.NewGuid().ToString("N")[..8]);
        p.RememberSelection();
        p.MarkAll(false);
        Transfers.EnqueueJob(move ? OperationKind.Move : OperationKind.Copy, remote.Select(r => r.Path).ToList(), targetDirectory, async job =>
        {
            try
            {
                Directory.CreateDirectory(temp);
                OperationResult down = await Task.Run(() => fs.DownloadAsync(remote, temp, job));
                if (down.Cancelled || down.Failed > 0)
                    return down;
                var local = Directory.EnumerateFileSystemEntries(temp).ToList();
                OperationResult up = await Task.Run(() => targetFs.UploadAsync(local, targetDirectory, job));
                if (move && !up.Cancelled && up.Failed == 0 && up.Skipped == 0 && down.Skipped == 0)
                {
                    OperationResult deleted = await Task.Run(() => fs.DeleteAsync(remote, job));
                    up.Failed += deleted.Failed;
                }
                return up;
            }
            finally
            {
                try
                {
                    Directory.Delete(temp, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // best effort: the temporary folder is emptied with the others at exit
                }
            }
        });
        return Task.CompletedTask;
    }

    /// <summary>Copy / move from a disk panel (or dropped, or pasted) onto a server.</summary>
    private void UploadToRemote(IReadOnlyList<string> sources, IPluginFileSystem fs, string targetDirectory, bool move)
    {
        if (sources.Count == 0)
            return;
        targetDirectory = targetDirectory.TrimEnd('\\');
        _active.RememberSelection();
        _active.MarkAll(false);
        Transfers.EnqueueJob(move ? OperationKind.Move : OperationKind.Copy, sources, targetDirectory, async job =>
        {
            OperationResult result = await Task.Run(() => fs.UploadAsync(sources, targetDirectory, job));
            if (move && !result.Cancelled && result.Failed == 0 && result.Skipped == 0)
            {
                var engine = new FileOperationEngine(new JobCallbacks(job));
                OperationResult deleted = await engine.DeleteAsync(sources, job.Cancellation);
                result.Failed += deleted.Failed;
            }
            return result;
        });
    }

    /// <summary>F8 on a server: deletes the selected files and directories (there is no Recycle Bin there).</summary>
    private async Task DeleteRemoteAsync(PaneViewModel p, IPluginFileSystem fs)
    {
        var items = p.GetOperationItems().Where(i => !i.Entry.IsUpDir).ToList();
        if (items.Count == 0)
            return;
        if (Settings.Confirm.PermanentDelete && !await _host.ConfirmAsync("Delete", items.Count == 1
                ? Loc.F(items[0].Entry.IsDirectory
                    ? "Do you want to permanently delete the directory \"{0}\"?\nThis cannot be undone."
                    : "Do you want to permanently delete the file \"{0}\"?\nThis cannot be undone.", items[0].Entry.Name)
                : Loc.F("Do you want to permanently delete the {0} selected items?\nThis cannot be undone.", items.Count), "Delete"))
            return;
        var remote = items.Select(i => RemoteFileSystemProvider.ItemOf(i.Entry)).ToList();
        p.RememberSelection();
        p.MarkAll(false);
        Transfers.EnqueueJob(OperationKind.Delete, remote.Select(r => r.Path).ToList(), null, job =>
            Task.Run(() => fs.DeleteAsync(remote, job)));
    }

    /// <summary>F7 on a server: asks for the name and creates the directory (a path "a/b" creates both).</summary>
    private async Task CreateRemoteDirectoryAsync(PaneViewModel p, IPluginFileSystem fs)
    {
        string? name = await _host.Dialogs.InputAsync("Create Directory", Loc.T("Name of the new directory:"));
        _host.FocusPane(p);
        if (name == null)
            return;
        string directory = p.Path;
        string first = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;
        try
        {
            foreach (string part in name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                directory = fs.Combine(directory, part);
                await Task.Run(() => fs.CreateDirectoryAsync(directory, CancellationToken.None));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _host.Notify(ex.Message);
        }
        await p.NavigateAsync(p.Path, first, addToHistory: false);
    }

    /// <summary>F2 on a server: the name is edited in its row, then renamed on the server.</summary>
    private async Task RenameRemoteAsync(PaneViewModel p, IPluginFileSystem fs)
    {
        FileItemViewModel? item = p.FocusedItem;
        if (item == null || item.Entry.IsUpDir)
            return;
        string? newName = await _host.EditNameInPlaceAsync(p, item);
        if (string.IsNullOrWhiteSpace(newName) || newName == item.Entry.Name)
            return;
        if (newName.IndexOfAny(new[] { '/', '\\' }) >= 0)
        {
            _host.Notify(Loc.T("The name can't contain '/' or '\\'."));
            return;
        }
        string from = RemoteFileSystemProvider.ItemOf(item.Entry).Path;
        string to = fs.Combine(p.Path, newName);
        try
        {
            await Task.Run(() => fs.RenameAsync(from, to, CancellationToken.None));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _host.Notify(ex.Message);
        }
        await p.NavigateAsync(p.Path, newName, addToHistory: false);
    }

    /// <summary>Enter / F3 / F4 on a file on a server: downloads it into a temporary folder and opens it.</summary>
    private Task OpenFromRemoteAsync(PaneViewModel p, FileItemViewModel item, IEnumerable<ProgramRule> rules)
    {
        if (RemoteProvider.FileSystemOf(p.Path) is not { } fs || item.Entry.IsDirectory)
            return Task.CompletedTask;
        RemoteItem remote = RemoteFileSystemProvider.ItemOf(item.Entry);
        string directory = Path.Combine(ArchiveTempRoot, Guid.NewGuid().ToString("N")[..8]);
        string file = Path.Combine(directory, item.Entry.Name);
        bool editor = ReferenceEquals(rules, Settings.Editors);
        Transfers.EnqueueJob(OperationKind.Copy, new[] { remote.Path }, directory, async job =>
        {
            Directory.CreateDirectory(directory);
            OperationResult result = await Task.Run(() => fs.DownloadAsync(new[] { remote }, directory, job));
            // back on the UI thread: open it with its program (Configuration → Viewers / Editors, else the default app)
            if (!result.Cancelled && File.Exists(file))
            {
                var entry = new FileEntry(item.Entry.Name, file, false, item.Entry.Size, item.Entry.LastWrite, FileAttributes.Normal);
                RunFoundFile(entry, rules, () =>
                {
                    if (editor)
                        ShellOperations.Edit(file);
                    else
                        ShellOperations.Open(file, directory);
                });
            }
            return result;
        });
        return Task.CompletedTask;
    }
}
