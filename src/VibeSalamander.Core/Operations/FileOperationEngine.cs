using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VibeSalamander.Core.Operations;

/// <summary>
/// Salamander-style copy / move / permanent delete. The operation is planned first (directory tree
/// walk, total size), then executed step by step with progress, conflict and error prompts.
/// Moves within one volume are plain renames. Files are copied with CopyFileEx (keeps attributes,
/// timestamps, alternate streams; partial targets are removed on cancel).
/// </summary>
public sealed class FileOperationEngine
{
    private readonly IOperationCallbacks _callbacks;
    private readonly IProgress<OperationProgress>? _progress;

    private OperationKind _kind;
    private ConflictChoice? _conflictForAll;
    private bool _skipAllErrors;
    private long _totalBytes, _doneBytes, _currentDone, _currentTotal;
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
    private sealed record FinishDirStep(string Source, string Target, bool DeleteSource) : Step(Source);
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
            bool intoDirectory = target.EndsWith('\\') || Directory.Exists(target) || sources.Count > 1;
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
                if (string.Equals(source, dest, StringComparison.OrdinalIgnoreCase))
                {
                    if (!await HandleErrorAsync(source, $"Cannot {(move ? "move" : "copy")} an item onto itself.", result))
                        return Cancelled(result);
                    continue;
                }
                if (isDir && dest.StartsWith(source + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    if (!await HandleErrorAsync(source, $"Cannot {(move ? "move" : "copy")} a directory into itself.", result))
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
                    if (!await PlanDirectoryAsync(source, dest, move, steps, result))
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

            foreach (Step step in steps)
            {
                WaitWhilePaused();
                ct.ThrowIfCancellationRequested();
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

    private async Task<bool> PlanDirectoryAsync(string source, string dest, bool move, List<Step> steps, OperationResult result)
    {
        _ct.ThrowIfCancellationRequested();
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
                ErrorChoice choice = await AskErrorAsync(source, ex.Message);
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
                if (await AskErrorAsync(child, ex.Message) == ErrorChoice.Cancel)
                    return false;
                result.Skipped++;
                continue;
            }

            if (isDirectory)
            {
                if (!await PlanDirectoryAsync(child, childDest, move, steps, result))
                    return false;
            }
            else
            {
                steps.Add(new CopyFileStep(child, childDest, size, move));
                _totalBytes += size;
            }
        }
        steps.Add(new FinishDirStep(source, dest, move));
        return true;
    }

    // ---- steps -------------------------------------------------------------------------------

    private async Task<bool> CopyFileAsync(CopyFileStep step, OperationResult result)
    {
        string target = step.Target;
        _currentSource = step.Source;
        _currentTarget = target;
        _currentDone = 0;
        _currentTotal = step.Size;
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

        while (true)
        {
            try
            {
                if (overwrite)
                    ClearReadOnly(target);
                CopyOneFile(step.Source, target, overwrite);
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
                ErrorChoice choice = await AskErrorAsync(step.Source, ex.Message);
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
        if (Directory.Exists(step.Target))
        {
            _itemsDone++; // merge into the existing directory (Salamander does this without asking)
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
                ErrorChoice choice = await AskErrorAsync(step.Target, ex.Message);
                if (choice == ErrorChoice.Retry) continue;
                if (choice == ErrorChoice.Cancel) return false;
                result.Failed++;
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
            if (step.DeleteSource && Directory.Exists(step.Source) && !Directory.EnumerateFileSystemEntries(step.Source).Any())
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
                ErrorChoice choice = await AskErrorAsync(step.Source, ex.Message);
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
                        ErrorChoice choice = await AskErrorAsync(step.Source, ex.Message);
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
        var attr = File.GetAttributes(directory);
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

        var info = new ConflictInfo(
            source, srcInfo.Exists ? srcInfo.Length : 0, srcInfo.Exists ? srcInfo.LastWriteTime : DateTime.MinValue,
            target, dstInfo.Exists ? dstInfo.Length : 0, dstInfo.Exists ? dstInfo.LastWriteTime : DateTime.MinValue,
            isDirectory);
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
        _skipAllErrors = false;
        _totalBytes = _doneBytes = _currentDone = _currentTotal = 0;
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
        _currentDone = _currentTotal;
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

        long done = _doneBytes + _currentDone;
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

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

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
        CopyProgressRoutine routine = (total, transferred, _, _, _, _, _, _, _) =>
        {
            _currentTotal = total;
            _currentDone = transferred;
            Report(force: false);
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
    }

    /// <summary>Adds the \\?\ prefix for paths beyond MAX_PATH.</summary>
    private static string LongPath(string path)
    {
        if (path.Length < 248 || path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path;
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }
}
