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
}
