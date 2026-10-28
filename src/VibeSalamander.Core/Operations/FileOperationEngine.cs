// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VibeSalamander.Core.Localization;

namespace VibeSalamander.Core.Operations;

/// <summary>
/// Salamander-style copy / move / permanent delete. The operation is planned first (directory tree
/// walk, total size), then executed step by step with progress, conflict and error prompts.
/// Moves within one volume are plain renames. Files are copied with CopyFileEx (keeps attributes,
/// timestamps, alternate streams; partial targets are removed on cancel).
/// </summary>
public sealed class FileOperationEngine : IPausable
{
    private readonly IOperationCallbacks _callbacks;
    private readonly IProgress<OperationProgress>? _progress;

    private OperationKind _kind;
    private ConflictChoice? _conflictForAll;
    private ConflictChoice? _mergeForAll;
    private readonly List<string> _skippedDirectories = new();

    /// <summary>Failed + skipped items when a directory was started (a moved link is removed only when nothing in it went wrong).</summary>
    private readonly Dictionary<string, int> _problemsBefore = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Confirmations and options from the configuration (set before starting an operation).</summary>
    public EngineOptions Options { get; set; } = new();

    /// <summary>Speed limits the copies keep to (this transfer's own, and the one for all transfers).</summary>
    public IReadOnlyList<SpeedLimiter> Limiters { get; set; } = Array.Empty<SpeedLimiter>();
    private bool _skipAllErrors;
    private long _totalBytes, _doneBytes, _currentDone, _currentTotal;

    /// <summary>A file is being copied: its bytes so far count toward the total (once it's done they are in _doneBytes).</summary>
    private bool _inFile;
    private int _itemsTotal, _itemsDone;
    private string _currentSource = string.Empty, _currentTarget = string.Empty;
    private readonly Stopwatch _clock = new();
    private long _lastReportTicks;
    private CancellationToken _ct;
    private volatile bool _paused;

    public FileOperationEngine(IOperationCallbacks callbacks, IProgress<OperationProgress>? progress = null)
    {
        _callbacks = callbacks;
        _progress = progress;
    }

    // ---- public API --------------------------------------------------------------------------

    /// <summary>Pauses/resumes the running operation (between files and inside a file copy).</summary>
    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    /// <summary>Blocks while paused (the clock stops, so speed and time left stay correct).</summary>
    private void WaitWhilePaused()
    {
        if (!_paused)
            return;
        PauseClock();
        while (_paused && !_ct.IsCancellationRequested)
            Thread.Sleep(100);
        ResumeClock();
    }

    /// <summary>
    /// Copies <paramref name="sources"/> to <paramref name="target"/>. Target rules (Salamander):
    /// an existing directory (or a path ending with '\') receives the items; a non-existing path with a
    /// single source is the new name; a non-existing path with several sources is created as a directory.
    /// </summary>
    public Task<OperationResult> CopyAsync(IReadOnlyList<string> sources, string target, CancellationToken cancellationToken = default) =>
        Task.Run(() => TransferAsync(sources, target, move: false, cancellationToken), cancellationToken);

    public Task<OperationResult> MoveAsync(IReadOnlyList<string> sources, string target, CancellationToken cancellationToken = default) =>
        Task.Run(() => TransferAsync(sources, target, move: true, cancellationToken), cancellationToken);

    /// <summary>Permanently deletes files and directory trees (Recycle Bin deletes go through the shell).</summary>
    public Task<OperationResult> DeleteAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        Task.Run(() => DeletePermanentlyAsync(paths, cancellationToken), cancellationToken);

    // ---- plan --------------------------------------------------------------------------------

    private abstract record Step(string Source);
    private sealed record CreateDirStep(string Source, string Target) : Step(Source);
    /// <param name="IsLink">The source is a junction or symbolic link: a move removes the link itself (its folder stays).</param>
    private sealed record FinishDirStep(string Source, string Target, bool DeleteSource, bool IsLink = false) : Step(Source);
    private sealed record CopyFileStep(string Source, string Target, long Size, bool DeleteSource) : Step(Source);
    private sealed record RenameStep(string Source, string Target, bool IsDirectory) : Step(Source);
    private sealed record DeleteStep(string Source, bool IsDirectory) : Step(Source);

    private async Task<OperationResult> TransferAsync(IReadOnlyList<string> sources, string target, bool move, CancellationToken ct)
    {
        Start(move ? OperationKind.Move : OperationKind.Copy, ct);
        var result = new OperationResult();
        var steps = new List<Step>();
        try
        {
            target = Path.GetFullPath(target);
            // one item renamed to its own name in other capitals ("folder" → "FOLDER"): the "existing directory" is itself
            bool caseRename = sources.Count == 1 && !target.EndsWith('\\') &&
                              string.Equals(Path.GetFullPath(sources[0]).TrimEnd('\\'), target, StringComparison.OrdinalIgnoreCase);
            bool intoDirectory = !caseRename && (target.EndsWith('\\') || Directory.Exists(target) || sources.Count > 1);
            target = target.TrimEnd('\\');
            if (target.Length == 2 && target[1] == ':')
                target += "\\";
            if (intoDirectory && !Directory.Exists(target))
                Directory.CreateDirectory(target);

            foreach (string rawSource in sources)
            {
                string source = Path.GetFullPath(rawSource).TrimEnd('\\');
                bool isDir = Directory.Exists(source);
                if (!isDir && !File.Exists(source))
                {
                    if (!await HandleErrorAsync(source, "The file or directory no longer exists.", result))
                        return Cancelled(result);
                    continue;
                }

                string dest = intoDirectory ? Path.Combine(target, Path.GetFileName(source)) : target;
                if (string.Equals(source, dest, StringComparison.OrdinalIgnoreCase) && move &&
                    !string.Equals(Path.GetFileName(source), Path.GetFileName(dest), StringComparison.Ordinal))
                {
                    // the same item with its name in other capitals ("a.txt" → "A.txt"): a rename
                    result.CreatedTopLevelNames.Add(Path.GetFileName(dest));
                    steps.Add(new RenameStep(source, dest, isDir));
                    continue;
                }
                if (string.Equals(source, dest, StringComparison.OrdinalIgnoreCase))
                {
                    if (!await HandleErrorAsync(source, move ? "Cannot move an item onto itself." : "Cannot copy an item onto itself.", result))
                        return Cancelled(result);
                    continue;
                }
                if (isDir && dest.StartsWith(source + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    if (!await HandleErrorAsync(source, move ? "Cannot move a directory into itself." : "Cannot copy a directory into itself.", result))
                        return Cancelled(result);
                    continue;
                }

                result.CreatedTopLevelNames.Add(Path.GetFileName(dest));
                if (move && SameVolume(source, dest) && !Directory.Exists(dest) && !File.Exists(dest))
                {
                    steps.Add(new RenameStep(source, dest, isDir));
                }
                else if (isDir)
                {
                    if (!await PlanDirectoryAsync(source, dest, move, steps, result, new List<string>()))
                        return Cancelled(result);
                }
                else
                {
                    long size = new FileInfo(source).Length;
                    steps.Add(new CopyFileStep(source, dest, size, move));
                    _totalBytes += size;
                }
            }

            _itemsTotal = steps.Count(s => s is CopyFileStep or RenameStep or CreateDirStep);
            Report(force: true, analyzing: false);

            // a write-protected drive (a locked SD card): said once, instead of an error for every file
            if (steps.Any(s => s is CopyFileStep or CreateDirStep || (s is RenameStep && !SameVolume(s.Source, target))) &&
                FileSystem.DriveInformation.IsWriteProtected(target))
            {
                await HandleErrorAsync(target, L.F("The drive {0} is write-protected: nothing can be copied to it.", Path.GetPathRoot(target) ?? target), result);
                return Cancelled(result);
            }

            // before anything is copied: is there room, and can the drive hold the files at all?
            string? problem = Preflight(steps.OfType<CopyFileStep>().Select(s => (s.Source, s.Target, s.Size)), target, VolumeOf);
            if (problem != null && !await _callbacks.ConfirmAsync(move ? L.T("Move") : L.T("Copy"), problem))
                return Cancelled(result);

            foreach (Step step in steps)
            {
                WaitWhilePaused();
                ct.ThrowIfCancellationRequested();
                if (IsInSkippedDirectory(step))
                    continue; // the user skipped merging this directory
                bool proceed = step switch
                {
                    CopyFileStep s => await CopyFileAsync(s, result),
                    CreateDirStep s => await CreateDirectoryAsync(s, result),
                    FinishDirStep s => await FinishDirectoryAsync(s, result),
                    RenameStep s => await RenameAsync(s, result),
                    _ => true,
                };
                if (!proceed)
                    return Cancelled(result);
            }
        }
        catch (OperationCanceledException)
        {
            return Cancelled(result);
        }
        Report(force: true);
        return result;
    }

    /// <param name="deleteSources">A move: the source items go once copied (not inside a link: its folder is elsewhere).</param>
    /// <param name="chain">The real folders being copied above this one (to recognize a link that points back up).</param>
    private async Task<bool> PlanDirectoryAsync(string source, string dest, bool deleteSources, List<Step> steps, OperationResult result,
        List<string> chain)
    {
        _ct.ThrowIfCancellationRequested();
        // a junction or symbolic link: its content is copied, but a move only removes the link, never the files of the
        // folder it points to (that folder is somewhere else and isn't part of what is moved)
        bool isLink = IsLink(source, out string real);
        if (chain.Exists(ancestor => IsSameOrInside(ancestor, real)))
        {
            // it points at a folder being copied: copying it would never end ("Application Data" in a user profile)
            return await HandleErrorAsync(source, "The link points back to a folder that is being copied: it was left out.", result);
        }
        chain.Add(real);
        bool childrenDeleted = deleteSources && !isLink;
        steps.Add(new CreateDirStep(source, dest));
        _currentSource = source;
        Report(force: false, analyzing: true);

        IEnumerable<string> children;
        while (true)
        {
            try
            {
                children = Directory.EnumerateFileSystemEntries(source, "*", new EnumerationOptions
                {
                    AttributesToSkip = 0,
                    IgnoreInaccessible = false,
                    RecurseSubdirectories = false,
                }).ToList();
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ErrorChoice choice = await AskErrorAsync(source, Interop.FileLocks.Explain(ex, source));
                if (choice == ErrorChoice.Retry) continue;
                if (choice == ErrorChoice.Cancel) return false;
                result.Skipped++;
                children = Array.Empty<string>();
                break;
            }
        }

        foreach (string child in children)
        {
            string childDest = Path.Combine(dest, Path.GetFileName(child));
            bool isDirectory;
            long size = 0;
            try
            {
                isDirectory = (File.GetAttributes(child) & FileAttributes.Directory) != 0;
                if (!isDirectory)
                    size = new FileInfo(child).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (await AskErrorAsync(child, Interop.FileLocks.Explain(ex, child)) == ErrorChoice.Cancel)
                    return false;
                result.Skipped++;
                continue;
            }

            if (isDirectory)
            {
                if (!await PlanDirectoryAsync(child, childDest, childrenDeleted, steps, result, chain))
                    return false;
            }
            else
            {
                steps.Add(new CopyFileStep(child, childDest, size, childrenDeleted));
                _totalBytes += size;
            }
        }
        chain.RemoveAt(chain.Count - 1);
        steps.Add(new FinishDirStep(source, dest, deleteSources, isLink));
        return true;
    }

    /// <summary>True for a junction or symbolic link to a directory; <paramref name="real"/> is the folder it leads to.</summary>
    private static bool IsLink(string directory, out string real)
    {
        real = directory;
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                return false;
            FileSystemInfo? target = new DirectoryInfo(directory).ResolveLinkTarget(returnFinalTarget: true);
            if (target == null)
                return false; // another kind of reparse point (OneDrive placeholders, deduplicated files): an ordinary folder
            string resolved = target.FullName;
            if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal) && !resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                resolved = resolved[4..];
            real = Path.GetFullPath(resolved).TrimEnd('\\');
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // a reparse point that can't be read: like a link, never deleted through
        }
    }

    private static bool IsSameOrInside(string path, string folder) =>
        string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    // ---- steps -------------------------------------------------------------------------------

    private async Task<bool> CopyFileAsync(CopyFileStep step, OperationResult result)
    {
        string target = step.Target;
        _currentSource = step.Source;
        _currentTarget = target;
        _currentDone = 0;
        _currentTotal = step.Size;
        _inFile = true;
        Report(force: true);

        bool overwrite = false;
        if (File.Exists(target) || Directory.Exists(target))
        {
            ConflictChoice choice = await ResolveConflictAsync(step.Source, target, isDirectory: Directory.Exists(target));
            switch (choice)
            {
                case ConflictChoice.Cancel:
                    return false;
                case ConflictChoice.Skip:
                    result.Skipped++;
                    Done(step.Size);
                    return true;
                case ConflictChoice.Rename:
                    target = UniqueName(target);
                    _currentTarget = target;
                    break;
                default: // Overwrite
                    if (Directory.Exists(target))
                    {
                        if (!await HandleErrorAsync(target, "A directory with this name already exists.", result))
                            return false;
                        Done(step.Size);
                        return true;
                    }
                    overwrite = true;
                    break;
            }
        }

        bool copied = false;
        while (true)
        {
            try
            {
                if (!copied)
                {
                    if (overwrite)
                        PrepareOverwrite(target);
                    CopyOneFile(step.Source, target, overwrite);
                    copied = true; // a Retry after the source couldn't be deleted (a move) doesn't copy again
                    if (Options.ClearReadOnlyFromCd && IsOnCdRom(step.Source))
                        ClearReadOnly(target);
                }
                if (step.DeleteSource)
                {
                    ClearReadOnly(step.Source);
                    File.Delete(step.Source);
                }
                result.Succeeded++;
                break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                ErrorChoice choice = await AskErrorAsync(step.Source, Interop.FileLocks.Explain(ex, step.Source, target));
                if (choice == ErrorChoice.Retry) continue;
                if (choice == ErrorChoice.Cancel) return false;
                result.Failed++;
                break;
            }
        }
        Done(step.Size);
        return true;
    }

    private async Task<bool> CreateDirectoryAsync(CreateDirStep step, OperationResult result)
    {
        _currentSource = step.Source;
        _currentTarget = step.Target;
        _currentDone = _currentTotal = 0;
        Report(force: false);
        _problemsBefore[step.Source] = result.Failed + result.Skipped;
        if (Directory.Exists(step.Target))
        {
            _itemsDone++;
            if (!Options.ConfirmDirectoryMerge)
                return true; // merge into the existing directory without asking
            ConflictChoice choice = await AskMergeAsync(step.Source, step.Target);
            if (choice == ConflictChoice.Cancel)
                return false;
            if (choice == ConflictChoice.Skip)
            {
                _skippedDirectories.Add(step.Target.TrimEnd('\\') + "\\");
                result.Skipped++;
            }
            return true;
        }
        while (true)
        {
            try
            {
                if (File.Exists(step.Target))
                    throw new IOException("A file with the same name as the directory already exists.");
                Directory.CreateDirectory(step.Target);
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ErrorChoice choice = await AskErrorAsync(step.Target, Interop.FileLocks.Explain(ex, step.Target));
                if (choice == ErrorChoice.Retry) continue;
                if (choice == ErrorChoice.Cancel) return false;
                result.Failed++;
                // its content has nowhere to go: one error for the directory, not one for every file in it
                // (and a move keeps the source)
                _skippedDirectories.Add(step.Target.TrimEnd('\\') + "\\");
                break;
            }
        }
        _itemsDone++;
        return true;
    }

    private Task<bool> FinishDirectoryAsync(FinishDirStep step, OperationResult result)
    {
        try
        {
            if (Directory.Exists(step.Target))
            {
                var info = new DirectoryInfo(step.Source);
                Directory.SetLastWriteTime(step.Target, info.LastWriteTime);
                FileAttributes attr = info.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.ReadOnly);
                if (attr != 0)
                    File.SetAttributes(step.Target, File.GetAttributes(step.Target) | attr);
            }
            if (step.DeleteSource && step.IsLink)
            {
                // a moved link: everything it showed was copied, so the link goes (the folder it points to stays);
                // after a problem inside it the link stays too
                bool problems = _problemsBefore.TryGetValue(step.Source, out int before) && result.Failed + result.Skipped > before;
                if (!problems)
                    Directory.Delete(step.Source); // removes only the link
            }
            else if (step.DeleteSource && Directory.Exists(step.Source) && !Directory.EnumerateFileSystemEntries(step.Source).Any())
            {
                ClearReadOnly(step.Source);
                Directory.Delete(step.Source);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // timestamps / removal of an emptied source directory are best effort
        }
        return Task.FromResult(true);
    }

    private async Task<bool> RenameAsync(RenameStep step, OperationResult result)
    {
        _currentSource = step.Source;
        _currentTarget = step.Target;
        Report(force: false);
        while (true)
        {
            try
            {
                if (step.IsDirectory)
                    Directory.Move(step.Source, step.Target);
                else
                    File.Move(step.Source, step.Target);
                result.Succeeded++;
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ErrorChoice choice = await AskErrorAsync(step.Source, Interop.FileLocks.Explain(ex, step.Source));
                if (choice == ErrorChoice.Retry) continue;
                if (choice == ErrorChoice.Cancel) return false;
                result.Failed++;
                break;
            }
        }
        _itemsDone++;
        return true;
    }

    // ---- permanent delete --------------------------------------------------------------------

    private async Task<OperationResult> DeletePermanentlyAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        Start(OperationKind.Delete, ct);
        var result = new OperationResult();
        var steps = new List<DeleteStep>();
        try
        {
            foreach (string path in paths)
            {
                string full = Path.GetFullPath(path).TrimEnd('\\');
                if (Directory.Exists(full))
                    PlanDelete(full, steps);
                else if (File.Exists(full))
                    steps.Add(new DeleteStep(full, false));
            }
            _itemsTotal = steps.Count;
            Report(force: true);

            foreach (DeleteStep step in steps)
            {
                WaitWhilePaused();
                ct.ThrowIfCancellationRequested();
                _currentSource = step.Source;
                Report(force: false);
                while (true)
                {
                    try
                    {
                        ClearReadOnly(step.Source);
                        if (step.IsDirectory)
                            Directory.Delete(step.Source, recursive: false);
                        else
                            File.Delete(step.Source);
                        result.Succeeded++;
                        break;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        ErrorChoice choice = await AskErrorAsync(step.Source, Interop.FileLocks.Explain(ex, step.Source));
                        if (choice == ErrorChoice.Retry) continue;
                        if (choice == ErrorChoice.Cancel) return Cancelled(result);
                        result.Failed++;
                        break;
                    }
                }
                _itemsDone++;
            }
        }
        catch (OperationCanceledException)
        {
            return Cancelled(result);
        }
        Report(force: true);
        return result;
    }

    private void PlanDelete(string directory, List<DeleteStep> steps)
    {
        _ct.ThrowIfCancellationRequested();
        FileAttributes attr;
        try
        {
            attr = File.GetAttributes(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            attr = FileAttributes.ReparsePoint; // unreadable: not gone into; its delete step reports the problem
        }
        if ((attr & FileAttributes.ReparsePoint) == 0) // junctions/symlinks: delete the link, not the target's content
        {
            try
            {
                foreach (string child in Directory.EnumerateFileSystemEntries(directory, "*", new EnumerationOptions { AttributesToSkip = 0 }))
                {
                    if ((File.GetAttributes(child) & FileAttributes.Directory) != 0)
                        PlanDelete(child, steps);
                    else
                        steps.Add(new DeleteStep(child, false));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the directory delete step will report the error
            }
        }
        steps.Add(new DeleteStep(directory, true));
    }

    // ---- prompts -----------------------------------------------------------------------------

    private bool IsInSkippedDirectory(Step step)
    {
        if (_skippedDirectories.Count == 0)
            return false;
        string target = step switch
        {
            CopyFileStep s => s.Target,
            CreateDirStep s => s.Target,
            FinishDirStep s => s.Target,
            RenameStep s => s.Target,
            _ => string.Empty,
        };
        string withSlash = target.TrimEnd('\\') + "\\";
        foreach (string skipped in _skippedDirectories)
            if (withSlash.StartsWith(skipped, StringComparison.OrdinalIgnoreCase))
                return !(step is CreateDirStep && string.Equals(withSlash, skipped, StringComparison.OrdinalIgnoreCase));
        return false;
    }

    /// <summary>"The directory already exists: merge?" (Overwrite = merge, Skip = leave it out).</summary>
    private async Task<ConflictChoice> AskMergeAsync(string source, string target)
    {
        if (_mergeForAll is ConflictChoice all)
            return all == ConflictChoice.SkipAll ? ConflictChoice.Skip : ConflictChoice.Overwrite;
        var info = new ConflictInfo(source, 0, Directory.GetLastWriteTime(source), target, 0, Directory.GetLastWriteTime(target),
            IsDirectory: true, TargetAttributes: File.GetAttributes(target), IsMerge: true);
        PauseClock();
        ConflictChoice choice = await _callbacks.ResolveConflictAsync(info).ConfigureAwait(false);
        ResumeClock();
        if (choice is ConflictChoice.OverwriteAll or ConflictChoice.SkipAll)
        {
            _mergeForAll = choice;
            return choice == ConflictChoice.SkipAll ? ConflictChoice.Skip : ConflictChoice.Overwrite;
        }
        return choice is ConflictChoice.Skip or ConflictChoice.Cancel ? choice : ConflictChoice.Overwrite;
    }

    private async Task<ConflictChoice> ResolveConflictAsync(string source, string target, bool isDirectory)
    {
        var srcInfo = new FileInfo(source);
        var dstInfo = new FileInfo(target);
        if (_conflictForAll is ConflictChoice all)
        {
            return all switch
            {
                ConflictChoice.OverwriteAll => ConflictChoice.Overwrite,
                ConflictChoice.SkipAll => ConflictChoice.Skip,
                ConflictChoice.RenameAll => ConflictChoice.Rename,
                ConflictChoice.OverwriteAllOlder => dstInfo.Exists && dstInfo.LastWriteTimeUtc < srcInfo.LastWriteTimeUtc
                    ? ConflictChoice.Overwrite : ConflictChoice.Skip,
                _ => all,
            };
        }

        FileAttributes targetAttributes = 0;
        try
        {
            targetAttributes = File.GetAttributes(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        // Configuration → Confirmations: overwrite without asking, except hidden/system files when that still asks
        bool hiddenOrSystem = (targetAttributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
        if (!isDirectory && !Options.ConfirmFileOverwrite && !(hiddenOrSystem && Options.ConfirmHiddenSystemOverwrite))
            return ConflictChoice.Overwrite;

        var info = new ConflictInfo(
            source, srcInfo.Exists ? srcInfo.Length : 0, srcInfo.Exists ? srcInfo.LastWriteTime : DateTime.MinValue,
            target, dstInfo.Exists ? dstInfo.Length : 0, dstInfo.Exists ? dstInfo.LastWriteTime : DateTime.MinValue,
            isDirectory, targetAttributes);
        PauseClock();
        ConflictChoice choice = await _callbacks.ResolveConflictAsync(info).ConfigureAwait(false);
        ResumeClock();
        if (choice is ConflictChoice.OverwriteAll or ConflictChoice.SkipAll or ConflictChoice.RenameAll or ConflictChoice.OverwriteAllOlder)
        {
            _conflictForAll = choice;
            return await ResolveConflictAsync(source, target, isDirectory).ConfigureAwait(false);
        }
        return choice;
    }

    private async Task<ErrorChoice> AskErrorAsync(string path, string message)
    {
        if (_skipAllErrors)
            return ErrorChoice.Skip;
        PauseClock();
        ErrorChoice choice = await _callbacks.ResolveErrorAsync(new OperationError(path, message)).ConfigureAwait(false);
        ResumeClock();
        if (choice == ErrorChoice.SkipAll)
        {
            _skipAllErrors = true;
            return ErrorChoice.Skip;
        }
        return choice;
    }

    /// <summary>Asks about an error that cannot be retried; returns false when the user cancels.</summary>
    private async Task<bool> HandleErrorAsync(string path, string message, OperationResult result)
    {
        ErrorChoice choice = await AskErrorAsync(path, message);
        if (choice == ErrorChoice.Cancel)
            return false;
        result.Skipped++;
        return true;
    }

    // ---- helpers -----------------------------------------------------------------------------

    private void Start(OperationKind kind, CancellationToken ct)
    {
        _kind = kind;
        _ct = ct;
        _conflictForAll = null;
        _mergeForAll = null;
        _skippedDirectories.Clear();
        _problemsBefore.Clear();
        _skipAllErrors = false;
        _totalBytes = _doneBytes = _currentDone = _currentTotal = 0;
        _inFile = false;
        _itemsTotal = _itemsDone = 0;
        _clock.Restart();
        Report(force: true, analyzing: true);
    }

    private static OperationResult Cancelled(OperationResult result)
    {
        result.Cancelled = true;
        return result;
    }

    private void Done(long bytes)
    {
        _doneBytes += bytes;
        _currentDone = _currentTotal; // the file's own bar shows it full
        _inFile = false; // ...but its bytes are counted once, in _doneBytes
        _itemsDone++;
        Report(force: false);
    }

    private void PauseClock() => _clock.Stop();
    private void ResumeClock() => _clock.Start();

    private void Report(bool force, bool analyzing = false)
    {
        if (_progress == null)
            return;
        long now = Environment.TickCount64;
        if (!force && now - _lastReportTicks < 100)
            return;
        _lastReportTicks = now;

        long done = _doneBytes + (_inFile ? _currentDone : 0);
        double seconds = _clock.Elapsed.TotalSeconds;
        double speed = seconds > 0.5 ? done / seconds : 0;
        TimeSpan? remaining = speed > 0 && _totalBytes > done ? TimeSpan.FromSeconds((_totalBytes - done) / speed) : null;
        _progress.Report(new OperationProgress
        {
            Kind = _kind,
            Analyzing = analyzing,
            CurrentSource = _currentSource,
            CurrentTarget = _currentTarget,
            CurrentBytesDone = _currentDone,
            CurrentBytesTotal = _currentTotal,
            TotalBytesDone = done,
            TotalBytes = _totalBytes,
            ItemsDone = _itemsDone,
            ItemsTotal = _itemsTotal,
            BytesPerSecond = speed,
            Remaining = remaining,
        });
    }

    /// <summary>The free space and file system of the drive a path is on, or null when it can't be told.</summary>
    private static (long Free, string Format)? VolumeOf(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
                return null;
            var drive = new DriveInfo(root);
            return drive.IsReady ? (drive.AvailableFreeSpace, drive.DriveFormat) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null; // a network share without a drive letter, a drive that went away
        }
    }

    /// <summary>The largest file FAT32 can hold: 4 GB less one byte.</summary>
    internal const long Fat32Limit = 4L * 1024 * 1024 * 1024 - 1;

    /// <summary>
    /// What would go wrong before a copy starts: not enough free space on the target drive (files that are replaced
    /// count as freed), or files larger than FAT32 can hold. Null when all is fine; else the question to ask.
    /// </summary>
    internal static string? Preflight(IEnumerable<(string Source, string Target, long Size)> files, string target,
        Func<string, (long Free, string Format)?> volume)
    {
        var list = files.ToList();
        if (list.Count == 0 || volume(target) is not { } drive)
            return null;
        long needed = 0;
        foreach (var (_, to, size) in list)
        {
            long replaced = 0;
            try
            {
                if (File.Exists(to))
                    replaced = new FileInfo(to).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            needed += Math.Max(0, size - replaced);
        }
        var problems = new List<string>();
        string root = Path.GetPathRoot(target) ?? target;
        if (needed > drive.Free)
            problems.Add(L.F("{0} are needed on {1}, but only {2} are free.", Formatting.Formats.ShortSize(needed), root,
                Formatting.Formats.ShortSize(drive.Free)));
        if (drive.Format.Equals("FAT32", StringComparison.OrdinalIgnoreCase) || drive.Format.Equals("FAT", StringComparison.OrdinalIgnoreCase))
        {
            var big = list.Where(f => f.Size > Fat32Limit).ToList();
            if (big.Count > 0)
                problems.Add(L.F("{0} files are larger than 4 GB, which the FAT32 drive {1} can't hold: {2}", big.Count, root,
                    string.Join(", ", big.Take(3).Select(f => Path.GetFileName(f.Source))) + (big.Count > 3 ? ", ..." : string.Empty)));
        }
        return problems.Count == 0 ? null : string.Join("\n\n", problems) + "\n\n" + L.T("Copy anyway? (What doesn't fit fails, the rest is copied.)");
    }

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

    private string? _cdRoot;
    private bool _cdRootIsCd;

    /// <summary>True when the file is on a CD/DVD drive (cached per drive root).</summary>
    private bool IsOnCdRom(string path)
    {
        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
            return false;
        if (!string.Equals(root, _cdRoot, StringComparison.OrdinalIgnoreCase))
        {
            _cdRoot = root;
            try
            {
                _cdRootIsCd = root.Length <= 3 && new DriveInfo(root).DriveType == DriveType.CDRom;
            }
            catch (ArgumentException)
            {
                _cdRootIsCd = false;
            }
        }
        return _cdRootIsCd;
    }

    /// <summary>
    /// Before overwriting: Windows refuses to copy over a read-only, hidden or system file. The copy then gets the
    /// source's attributes.
    /// </summary>
    private static void PrepareOverwrite(string path)
    {
        try
        {
            FileAttributes attr = File.GetAttributes(path);
            const FileAttributes blocking = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System;
            if ((attr & blocking) != 0)
                File.SetAttributes(path, attr & ~blocking);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the copy reports the real problem
        }
    }

    private static void ClearReadOnly(string path)
    {
        try
        {
            FileAttributes attr = File.GetAttributes(path);
            if ((attr & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the following operation reports the real problem
        }
    }

    /// <summary>"name (2).ext", "name (3).ext", … – first name that does not exist.</summary>
    public static string UniqueName(string path)
    {
        string dir = Path.GetDirectoryName(path) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }

    // ---- CopyFileEx --------------------------------------------------------------------------

    private const uint COPY_FILE_FAIL_IF_EXISTS = 0x1;
    private const uint COPY_FILE_ALLOW_DECRYPTED_DESTINATION = 0x8;
    private const uint PROGRESS_CONTINUE = 0;
    private const uint PROGRESS_CANCEL = 1;
    private const int ERROR_REQUEST_ABORTED = 1235;

    private delegate uint CopyProgressRoutine(long totalFileSize, long totalBytesTransferred, long streamSize,
        long streamBytesTransferred, uint streamNumber, uint callbackReason, IntPtr sourceFile, IntPtr destinationFile, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CopyFileExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopyFileEx(string existingFileName, string newFileName, CopyProgressRoutine? progressRoutine,
        IntPtr data, ref int cancel, uint copyFlags);

    private void CopyOneFile(string source, string target, bool overwrite)
    {
        long counted = 0;
        CopyProgressRoutine routine = (total, transferred, _, _, _, _, _, _, _) =>
        {
            _currentTotal = total;
            _currentDone = transferred;
            Report(force: false);
            if (transferred > counted)
            {
                foreach (SpeedLimiter limiter in Limiters)
                    limiter.Take(transferred - counted, _ct);
                counted = transferred;
            }
            WaitWhilePaused();
            return _ct.IsCancellationRequested ? PROGRESS_CANCEL : PROGRESS_CONTINUE;
        };
        int cancel = 0;
        uint flags = COPY_FILE_ALLOW_DECRYPTED_DESTINATION | (overwrite ? 0 : COPY_FILE_FAIL_IF_EXISTS);
        bool ok = CopyFileEx(LongPath(source), LongPath(target), routine, IntPtr.Zero, ref cancel, flags);
        int error = Marshal.GetLastWin32Error();
        GC.KeepAlive(routine);
        if (!ok)
        {
            if (error == ERROR_REQUEST_ABORTED && _ct.IsCancellationRequested)
                throw new OperationCanceledException(_ct);
            throw new Win32Exception(error);
        }
        // Configuration → General: the copy is read again and compared with the original
        if (Options.VerifyCopies && !Tools.DirectoryComparer.ContentEqual(LongPath(source), LongPath(target), _ct))
            throw new IOException(L.T("The copy differs from the original (checked after copying)."));
    }

    /// <summary>Adds the \\?\ prefix for paths beyond MAX_PATH.</summary>
    private static string LongPath(string path)
    {
        if (path.Length < 248 || path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path;
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }
}
