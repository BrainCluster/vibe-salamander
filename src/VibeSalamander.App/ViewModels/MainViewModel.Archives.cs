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

// MainViewModel: packing and unpacking (Alt+F5 / Alt+F6) through the archiver plugins.
public sealed partial class MainViewModel
{
    // ---- archives (Alt+F5 / Alt+F6, the archive plugin) -----------------------------------------

    /// <summary>Alt+F5: packs the marked items (or the focused one) into an archive, in the transfer panel.</summary>
    private async Task PackAsync()
    {
        var items = _active.GetOperationItems();
        if (items.Count == 0 || !_active.IsLocalDisk)
            return;
        IReadOnlyList<(IArchiver Archiver, ArchiveFormat Format)> formats = Plugins.PackFormats;
        if (formats.Count == 0)
        {
            _host.Notify("No plugin can pack: enable the Archives plugin in Plugins → Plugins Manager.");
            return;
        }
        PluginOptions o = Settings.Plugins;
        ArchiveFormat format = formats.FirstOrDefault(f => string.Equals(f.Format.Id, o.PackFormat, StringComparison.OrdinalIgnoreCase)).Format
                               ?? formats[0].Format;

        // "photos.zip" for one item, the directory's name for several; into the other panel like F5
        string baseName = items.Count == 1
            ? (items[0].Entry.IsDirectory ? items[0].Entry.Name : Path.GetFileNameWithoutExtension(items[0].Entry.Name))
            : Path.GetFileName(_active.Path.TrimEnd('\\'));
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = _active.Path.Length >= 1 ? _active.Path[..1] : "Archive"; // a drive root: "C"
        string directory = Inactive.IsLocalDisk ? Inactive.Path : _active.Path;
        string what = items.Count == 1 ? $"\"{items[0].Entry.Name}\"" : Loc.F("{0} items", items.Count);

        var answer = await _host.Dialogs.PackAsync(what, Path.Combine(directory, baseName + format.Extension),
            formats.Select(f => f.Format).ToList(), format.Id, o.PackCompression, o.PackDeleteSources,
            new Views.Dialogs.PackAdvanced(o.PackAdvancedOpen, o.PackUpdateMode, o.PackPathMode, o.PackVolumes, o.PackEncryption,
                o.PackSelfExtracting, o.PackNtfsTimes, o.PackFormatOptions));
        _host.FocusPane(_active);
        if (answer == null)
            return;
        (IArchiver archiver, ArchiveFormat chosen) = formats.First(f => f.Format.Id == answer.FormatId);
        string archivePath = PathUtil.Resolve(_active.Path, answer.ArchivePath);
        if (answer.ArchivePath.TrimEnd().EndsWith('\\') || Directory.Exists(archivePath))
            archivePath = Path.Combine(archivePath, baseName + chosen.Extension); // only a directory was given
        else if (!PluginManager.HasArchiveExtension(archivePath, chosen))
            archivePath += chosen.Extension;
        o.PackFormat = chosen.Id;
        o.PackCompression = answer.Compression;
        o.PackDeleteSources = answer.DeleteSources;
        Views.Dialogs.PackAdvanced advanced = answer.Advanced ?? new Views.Dialogs.PackAdvanced(false, "Replace", "Relative", string.Empty, "Aes256", false, false);
        o.PackAdvancedOpen = advanced.Open;
        o.PackUpdateMode = advanced.UpdateMode;
        o.PackPathMode = advanced.PathMode;
        o.PackVolumes = advanced.Volumes;
        o.PackEncryption = advanced.Encryption;
        o.PackSelfExtracting = advanced.SelfExtracting;
        o.PackNtfsTimes = advanced.NtfsTimes;
        if (advanced.FormatOptions != null)
            o.PackFormatOptions = new Dictionary<string, string>(advanced.FormatOptions);
        SaveSettings();
        // the chosen format's own settings, without the "7z." in front
        string prefix = chosen.Id + ".";
        var formatOptions = (chosen.Options ?? Array.Empty<PackOption>()).ToDictionary(
            option => option.Id,
            option => o.PackFormatOptions.TryGetValue(prefix + option.Id, out string? value) ? value : option.Default);

        var sources = items.Select(i => i.Entry.FullPath).ToList();
        string baseDirectory = _active.Path;
        var compression = Enum.TryParse(answer.Compression, out ArchiveCompression c) ? c : ArchiveCompression.Normal;
        bool deleteSources = answer.DeleteSources;
        string? password = chosen.SupportsPassword ? answer.Password : null;
        _active.RememberSelection();
        _active.MarkAll(false);
        Transfers.EnqueueJob(OperationKind.Pack, sources, archivePath, async job =>
        {
            // on a worker thread: a plugin that blocks on the job's questions must not block the UI thread
            OperationResult result = await Task.Run(() => archiver.PackAsync(new PackRequest
            {
                Sources = sources,
                BaseDirectory = baseDirectory,
                ArchivePath = archivePath,
                FormatId = chosen.Id,
                Compression = compression,
                Password = password,
                Encryption = Enum.TryParse(advanced.Encryption, out ZipEncryption chosenEncryption) ? chosenEncryption : ZipEncryption.Aes256,
                PathMode = Enum.TryParse(advanced.PathMode, out PackPathMode chosenPathMode) ? chosenPathMode : PackPathMode.Relative,
                UpdateMode = Enum.TryParse(advanced.UpdateMode, out PackUpdateMode chosenUpdateMode) ? chosenUpdateMode : PackUpdateMode.Replace,
                VolumeSize = chosen.IsZip && advanced.SelfExtracting ? 0 : Math.Max(0, Views.Dialogs.PackAdvanced.ParseVolumeSize(advanced.Volumes)),
                SelfExtracting = chosen.IsZip && advanced.SelfExtracting,
                StoreNtfsTimes = chosen.IsZip && advanced.NtfsTimes,
                Options = formatOptions,
            }, job));
            // move to archive: only when everything went in (and never when the archive is inside a packed directory)
            if (deleteSources && !result.Cancelled && result.Failed == 0 && result.Skipped == 0 &&
                !sources.Any(src => archivePath.StartsWith(src.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
            {
                var engine = new FileOperationEngine(new JobCallbacks(job));
                OperationResult deleted = await engine.DeleteAsync(sources, job.Cancellation);
                result.Failed += deleted.Failed;
            }
            return result;
        });
    }

    /// <summary>Asks the plugin job's questions for the copy engine (deleting after "move to archive").</summary>
    private sealed class JobCallbacks : IOperationCallbacks
    {
        private readonly IArchiveJob _job;

        public JobCallbacks(IArchiveJob job) => _job = job;

        public Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict) => _job.ResolveConflictAsync(conflict);

        public Task<ErrorChoice> ResolveErrorAsync(OperationError error) => _job.ResolveErrorAsync(error);
    }

    /// <summary>Alt+F6: unpacks the marked archives (or the focused one), each in its own transfer row.</summary>
    private async Task UnpackAsync()
    {
        var items = _active.GetOperationItems();
        if (items.Count == 0 || !_active.IsLocalDisk)
            return;
        var archives = items.Where(i => !i.Entry.IsDirectory)
                            .Select(i => (Item: i, Archiver: Plugins.FindUnpacker(i.Entry.FullPath)))
                            .Where(x => x.Archiver != null)
                            .ToList();
        if (archives.Count == 0)
        {
            _host.Notify(items.Count == 1
                ? $"\"{items[0].Entry.Name}\" is not an archive Vibe Salamander can unpack."
                : "None of the selected files is an archive Vibe Salamander can unpack.");
            return;
        }
        PluginOptions o = Settings.Plugins;
        string defaultTarget = Inactive.IsLocalDisk ? Inactive.Path : _active.Path;
        if (!defaultTarget.EndsWith('\\'))
            defaultTarget += "\\";
        string what = archives.Count == 1 ? $"\"{archives[0].Item.Entry.Name}\"" : Loc.F("{0} archives", archives.Count);
        string example = archives.Count == 1 ? Plugins.ArchiveBaseName(archives[0].Item.Entry.Name) + "\\" : "name\\";
        var answer = await _host.Dialogs.UnpackAsync(what, defaultTarget, o.UnpackMask, o.UnpackToSubfolder, example);
        _host.FocusPane(_active);
        if (answer == null)
            return;
        o.UnpackMask = answer.Mask;
        o.UnpackToSubfolder = answer.ToSubfolder;
        SaveSettings();

        string target = PathUtil.Resolve(_active.Path, answer.Target);
        if (!Directory.Exists(target) && Settings.Confirm.CreateTargetPath &&
            !await _host.ConfirmAsync("Unpack", $"The target directory \"{target.TrimEnd('\\')}\" doesn't exist. Do you want to create it?", "Create"))
            return;
        if (archives.Count > 1)
        {
            _active.RememberSelection();
            _active.MarkAll(false);
        }
        foreach (var (item, archiver) in archives)
        {
            string archivePath = item.Entry.FullPath;
            string directory = answer.ToSubfolder ? Path.Combine(target, Plugins.ArchiveBaseName(item.Entry.Name)) : target;
            string mask = answer.Mask;
            Transfers.EnqueueJob(OperationKind.Unpack, new[] { archivePath }, directory, job =>
                Task.Run(() => archiver!.UnpackAsync(new UnpackRequest { ArchivePath = archivePath, TargetDirectory = directory, Mask = mask }, job)));
        }
    }

    private async Task DeleteAsync(bool permanent)
    {
        var items = _active.GetOperationItems();
        if (items.Count == 0 || !_active.IsLocalDisk)
            return;

        // Configuration → Recycle Bin: what F8 does with each item (Shift+Del is always permanent)
        var recycle = new List<FileItemViewModel>();
        var remove = new List<FileItemViewModel>();
        if (permanent || Settings.Recycle.Mode == DeleteMode.DeletePermanently)
        {
            remove.AddRange(items);
        }
        else if (Settings.Recycle.Mode == DeleteMode.RecycleMatching)
        {
            var mask = new WildcardMask(Settings.Recycle.Masks);
            foreach (FileItemViewModel item in items)
                (item.Entry.IsDirectory || mask.IsMatch(item.Entry.Name) ? recycle : remove).Add(item);
        }
        else
        {
            recycle.AddRange(items);
        }

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

        int caret = _active.CaretIndex;
        _active.RememberSelection();
        if (remove.Count > 0)
        {
            // runs in the transfer panel; the panels refresh when it finishes
            _active.MarkAll(false);
            var paths = remove.Select(i => i.Entry.FullPath).ToList();
            Transfers.Enqueue(OperationKind.Delete, paths, null, (engine, ct) => engine.DeleteAsync(paths, ct));
        }
        if (recycle.Count > 0)
        {
            // the Recycle Bin is only reachable through the shell (it shows its own progress window)
            await ShellOperations.DeleteAsync(_host.WindowHandle, recycle.Select(i => i.Entry.FullPath).ToList(), permanent: false);
            await RefreshAllAsync();
            CaretMoveRequested?.Invoke(this, (_active, Math.Min(caret, Math.Max(0, _active.Items.Count - 1))));
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

    /// <summary>Confirmations → Drag and Drop operations.</summary>
    public async Task<bool> ConfirmDropAsync(IReadOnlyList<string> paths, string target, bool move)
    {
        if (!Settings.Confirm.DragAndDrop)
            return true;
        string what = paths.Count == 1 ? $"\"{Path.GetFileName(paths[0].TrimEnd('\\'))}\"" : Loc.F("{0} items", paths.Count);
        return await _host.ConfirmAsync(move ? "Move" : "Copy",
            Loc.F(move ? "Do you want to move {0} to {1}?" : "Do you want to copy {0} to {1}?", what, target), move ? "Move" : "Copy");
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

    /// <summary>F2: rename in place (Salamander quick rename).</summary>
    private async Task RenameAsync()
    {
        FileItemViewModel? item = _active.FocusedItem;
        if (item == null || item.Entry.IsUpDir || item.IsDrive || !_active.IsLocalDisk)
            return;
        await RenameItemAsync(_active, item, allowSubdirectories: false, selectWholeName: Settings.General.RenameSelectsExtension);
    }

    /// <summary>
    /// Edits the item's name in its row and renames it. Problems are reported with a notification, not a
    /// dialog. With <paramref name="allowSubdirectories"/> (a new folder) a typed "a\b" creates the path.
    /// Returns the item's path afterwards (unchanged when cancelled), or null when the rename failed.
    /// </summary>
    private async Task<string?> RenameItemAsync(PaneViewModel pane, FileItemViewModel item, bool allowSubdirectories, bool selectWholeName = false)
    {
        string oldPath = item.Entry.FullPath;
        string? newName = await _host.EditNameInPlaceAsync(pane, item, selectWholeName);
        newName = newName?.Trim().TrimEnd('.', ' ');
        if (string.IsNullOrEmpty(newName) || newName == item.Entry.Name)
            return oldPath;

        bool nested = allowSubdirectories && item.Entry.IsDirectory && newName.IndexOfAny(new[] { '\\', '/' }) >= 0;
        string target;
        if (nested)
        {
            target = PathUtil.Resolve(pane.Path, newName);
        }
        else if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            _host.Notify($"\"{newName}\" contains characters that are not allowed in file names (\\ / : * ? \" < > |).");
            return null;
        }
        else
        {
            target = Path.Combine(pane.Path, newName);
        }

        try
        {
            bool caseOnly = string.Equals(target, oldPath, StringComparison.OrdinalIgnoreCase);
            if (item.Entry.IsDirectory)
            {
                if (nested)
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (caseOnly)
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

        // focus the renamed item (for a nested path: the first new directory in this panel)
        string relative = Path.GetRelativePath(pane.Path, target);
        string focus = relative.Split(Path.DirectorySeparatorChar)[0];
        await pane.NavigateAsync(pane.Path, focus, addToHistory: false);
        return target;
    }
}
