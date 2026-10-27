// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Operations;
using VibeSalamander.Core.Settings;
using VibeSalamander.Plugins;
using VibeSalamander.Services;

namespace VibeSalamander.ViewModels;

/// <summary>
/// Pack (Alt+F5) and Unpack (Alt+F6) on a disk, through the archiver plugins: the dialogs, the remembered choices,
/// and the jobs in the transfer panel. The decisions are in Core's ArchiveRules. (Browsing and changing archives
/// shown as folders is MainViewModel's ArchiveBrowsing part.)
/// </summary>
internal sealed class ArchiveCommands
{
    private readonly MainViewModel _main;
    private readonly IUiHost _host;

    public ArchiveCommands(MainViewModel main, IUiHost host)
    {
        _main = main;
        _host = host;
    }

    private PaneViewModel Active => _main.Active;
    private PaneViewModel Inactive => _main.Inactive;
    private AppSettings Settings => _main.Settings;
    private PluginManager Plugins => _main.Plugins;

    /// <summary>Alt+F5: packs the marked items (or the focused one) into an archive, in the transfer panel.</summary>
    public async Task PackAsync()
    {
        PaneViewModel active = Active;
        var items = active.GetOperationItems();
        if (items.Count == 0 || !active.IsLocalDisk)
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
        string baseName = ArchiveRules.PackBaseName(items.Count, items[0].Entry.Name, items[0].Entry.IsDirectory, active.Path);
        string directory = Inactive.IsLocalDisk ? Inactive.Path : active.Path;
        string what = items.Count == 1 ? $"\"{items[0].Entry.Name}\"" : Loc.F("{0} items", items.Count);

        var answer = await _host.Dialogs.PackAsync(what, Path.Combine(directory, baseName + format.Extension),
            formats.Select(f => f.Format).ToList(), format.Id, o.PackCompression, o.PackDeleteSources,
            new Views.Dialogs.PackAdvanced(o.PackAdvancedOpen, o.PackUpdateMode, o.PackPathMode, o.PackVolumes, o.PackEncryption,
                o.PackSelfExtracting, o.PackNtfsTimes, o.PackFormatOptions));
        _host.FocusPane(active);
        if (answer == null)
            return;
        (IArchiver archiver, ArchiveFormat chosen) = formats.First(f => f.Format.Id == answer.FormatId);
        string archivePath = ArchiveRules.PackArchivePath(active.Path, answer.ArchivePath, baseName, chosen.Extension,
            Directory.Exists, path => PluginManager.HasArchiveExtension(path, chosen));

        // the choices are remembered for the next time
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
        _main.SaveSettings();

        Dictionary<string, string> formatOptions = ArchiveRules.FormatOptions(chosen.Id,
            (chosen.Options ?? Array.Empty<PackOption>()).Select(option => (option.Id, option.Default)), o.PackFormatOptions);
        var sources = items.Select(i => i.Entry.FullPath).ToList();
        string baseDirectory = active.Path;
        var compression = Enum.TryParse(answer.Compression, out ArchiveCompression c) ? c : ArchiveCompression.Normal;
        bool deleteSources = answer.DeleteSources;
        string? password = chosen.SupportsPassword ? answer.Password : null;
        active.RememberSelection();
        active.MarkAll(false);
        _main.Transfers.EnqueueJob(OperationKind.Pack, sources, archivePath, async job =>
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
            if (ArchiveRules.MayDeleteSources(deleteSources, result, sources, archivePath))
            {
                var engine = new FileOperationEngine(new JobCallbacks(job));
                OperationResult deleted = await engine.DeleteAsync(sources, job.Cancellation);
                result.Failed += deleted.Failed;
            }
            return result;
        });
    }

    /// <summary>Alt+F6: unpacks the marked archives (or the focused one), each in its own transfer row.</summary>
    public async Task UnpackAsync()
    {
        PaneViewModel active = Active;
        var items = active.GetOperationItems();
        if (items.Count == 0 || !active.IsLocalDisk)
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
        string defaultTarget = ArchiveRules.UnpackDefaultTarget(active.Path, Inactive.Path, Inactive.IsLocalDisk);
        string what = archives.Count == 1 ? $"\"{archives[0].Item.Entry.Name}\"" : Loc.F("{0} archives", archives.Count);
        string example = archives.Count == 1 ? Plugins.ArchiveBaseName(archives[0].Item.Entry.Name) + "\\" : "name\\";
        var answer = await _host.Dialogs.UnpackAsync(what, defaultTarget, o.UnpackMask, o.UnpackToSubfolder, example);
        _host.FocusPane(active);
        if (answer == null)
            return;
        o.UnpackMask = answer.Mask;
        o.UnpackToSubfolder = answer.ToSubfolder;
        _main.SaveSettings();

        string target = PathUtil.Resolve(active.Path, answer.Target);
        if (!Directory.Exists(target) && Settings.Confirm.CreateTargetPath &&
            !await _host.ConfirmAsync("Unpack", $"The target directory \"{target.TrimEnd('\\')}\" doesn't exist. Do you want to create it?", "Create"))
            return;
        if (archives.Count > 1)
        {
            active.RememberSelection();
            active.MarkAll(false);
        }
        foreach (var (item, archiver) in archives)
        {
            string archivePath = item.Entry.FullPath;
            string directory = answer.ToSubfolder ? Path.Combine(target, Plugins.ArchiveBaseName(item.Entry.Name)) : target;
            string mask = answer.Mask;
            _main.Transfers.EnqueueJob(OperationKind.Unpack, new[] { archivePath }, directory, job =>
                Task.Run(() => archiver!.UnpackAsync(new UnpackRequest { ArchivePath = archivePath, TargetDirectory = directory, Mask = mask }, job)));
        }
    }
}

/// <summary>Asks a plugin job's questions for the copy engine (deleting after "move to archive", server transfers).</summary>
internal sealed class JobCallbacks : IOperationCallbacks
{
    private readonly IArchiveJob _job;

    public JobCallbacks(IArchiveJob job) => _job = job;

    public Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict) => _job.ResolveConflictAsync(conflict);

    public Task<ErrorChoice> ResolveErrorAsync(OperationError error) => _job.ResolveErrorAsync(error);
}
