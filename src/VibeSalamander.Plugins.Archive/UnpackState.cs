using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Operations;

namespace VibeSalamander.Plugins.Archive;

/// <summary>One unpack: the target, the answers given so far ("Overwrite All", ...) and the counts.</summary>
internal sealed class UnpackState
{
    private readonly WildcardMask _mask;
    private readonly string _targetPrefix;
    private readonly HashSet<string> _written = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _topLevel = new(StringComparer.OrdinalIgnoreCase);
    private ConflictChoice? _conflictForAll; // OverwriteAll, OverwriteAllOlder, SkipAll or RenameAll
    private bool _skipAllErrors;

    public UnpackState(UnpackRequest request, IArchiveJob job, string kind)
    {
        Request = request;
        Job = job;
        Kind = kind;
        TargetDirectory = Path.GetFullPath(request.TargetDirectory);
        _targetPrefix = TargetDirectory.TrimEnd('\\') + "\\";
        _mask = new WildcardMask(string.IsNullOrWhiteSpace(request.Mask) ? "*.*" : request.Mask);
        Tracker = new ProgressTracker(OperationKind.Unpack, 0, 0);
        _base = Normalize(request.BasePath) ?? string.Empty;
        _items = request.Items?.Select(Normalize).Where(i => i != null).Select(i => i!).ToList();
    }

    private readonly string _base;
    private readonly List<string>? _items;

    /// <summary>"./docs\a.txt/" → "docs/a.txt"; null when nothing is left.</summary>
    public static string? Normalize(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return null;
        var parts = key.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".");
        string joined = string.Join('/', parts);
        return joined.Length == 0 ? null : joined;
    }

    /// <summary>Part of the selection (UnpackRequest.Items), and inside the base directory.</summary>
    private bool Selected(string key)
    {
        if (_base.Length > 0 && !key.StartsWith(_base + "/", StringComparison.OrdinalIgnoreCase))
            return false;
        if (_items == null)
            return true;
        foreach (string item in _items)
            if (key.Equals(item, StringComparison.OrdinalIgnoreCase) || key.StartsWith(item + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public UnpackRequest Request { get; }
    public IArchiveJob Job { get; }
    public string Kind { get; }
    public string TargetDirectory { get; }
    public string? Password { get; set; }

    /// <summary>The archive has encrypted entries (a later data error means a wrong password).</summary>
    public bool Encrypted { get; set; }

    public ProgressTracker Tracker { get; set; }
    public OperationResult Result { get; private set; } = new();

    /// <summary>After a wrong password: count again (files already written are overwritten silently).</summary>
    public void ResetCounts()
    {
        Result = new OperationResult();
        _topLevel.Clear();
    }

    public bool Matches(string? key)
    {
        string? normalized = Normalize(key);
        if (normalized == null || !Selected(normalized))
            return false;
        if (_mask.MatchesEverything)
            return true;
        string name = Path.GetFileName((key ?? string.Empty).Replace('/', '\\').TrimEnd('\\'));
        return name.Length > 0 && _mask.IsMatch(name);
    }

    /// <summary>
    /// "dir/sub/file.txt" → full target path, or null when the name is unusable or would end up outside
    /// the target directory ("../../evil.exe", "C:\Windows\...").
    /// </summary>
    public string? TargetPathOf(string? key)
    {
        key = Normalize(key);
        if (key == null)
            return null;
        if (_base.Length > 0)
        {
            // "docs/images/a.png" with base "docs" → "images/a.png"
            if (!key.StartsWith(_base + "/", StringComparison.OrdinalIgnoreCase))
                return null;
            key = key[(_base.Length + 1)..];
        }
        var parts = new List<string>();
        foreach (string raw in key.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string part = raw.Trim();
            if (part is "." or "")
                continue;
            if (part == "..")
                return null;
            // names Windows can't store (':' in names from Unix archives, trailing dots and spaces)
            char[] invalid = Path.GetInvalidFileNameChars();
            var chars = part.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();
            part = new string(chars).TrimEnd('.', ' ');
            if (part.Length == 0)
                part = "_";
            if (IsDeviceName(part))
                part = "_" + part; // "CON", "nul.txt": names Windows reserves for devices
            parts.Add(part);
        }
        if (parts.Count == 0)
            return null;
        string full = Path.GetFullPath(Path.Combine(TargetDirectory, Path.Combine(parts.ToArray())));
        return full.StartsWith(_targetPrefix, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static bool IsDeviceName(string name)
    {
        int dot = name.IndexOf('.');
        return DeviceNames.Contains(dot < 0 ? name : name[..dot]);
    }

    private void RememberTopLevel(string fullPath)
    {
        string relative = fullPath[_targetPrefix.Length..];
        int slash = relative.IndexOf('\\');
        string top = slash < 0 ? relative : relative[..slash];
        if (_topLevel.Add(top))
            Result.CreatedTopLevelNames.Add(top);
    }

    /// <summary>A directory entry: created when everything is unpacked (with a mask, directories come with their files).</summary>
    public void CreateDirectoryEntry(string? key)
    {
        if (!_mask.MatchesEverything)
            return;
        string? normalized = Normalize(key);
        if (normalized == null || !Selected(normalized))
            return;
        string? path = TargetPathOf(key);
        if (path == null)
            return;
        try
        {
            Directory.CreateDirectory(path);
            RememberTopLevel(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the files inside report the problem
        }
    }

    /// <summary>Unpacks one file: asks about an existing file, writes it, sets its time.</summary>
    /// <param name="size">-1 when unknown.</param>
    /// <param name="open">Opens the entry's data (called at most once unless canRetry).</param>
    /// <param name="countBytes">Progress by the written bytes (false: by <paramref name="position"/>).</param>
    /// <param name="canRetry">The entry can be opened again after a write error.</param>
    /// <param name="disposeInput">False when the stream belongs to a reader (TAR).</param>
    public void Extract(string? key, long size, DateTime? time, Func<Stream> open, bool countBytes,
        Func<long>? position = null, bool canRetry = false, bool disposeInput = true)
    {
        Job.Cancellation.ThrowIfCancellationRequested();
        Job.WaitWhilePaused();
        string? target = TargetPathOf(key);
        if (target == null)
        {
            Result.Skipped++; // unsafe or empty name
            return;
        }
        Tracker.Start(target);
        Tracker.ReportTo(Job, force: true);

        // an existing file (not one this unpack wrote itself, e.g. before a wrong password was noticed)
        if (Directory.Exists(target))
        {
            Fail(target, "A directory with this name already exists.", countBytes, size);
            return;
        }
        if (File.Exists(target) && !_written.Contains(target))
        {
            string? resolved = ResolveConflict(key!, target, size, time);
            if (resolved == null)
            {
                Result.Skipped++;
                if (countBytes && size > 0)
                    Tracker.AddBytes(size);
                Tracker.ItemDone();
                return;
            }
            target = resolved;
        }

        while (true)
        {
            long bytesBefore = Tracker.BytesDone;
            try
            {
                using (FileStream output = CreateOutput(target))
                {
                    Stream input = open();
                    try
                    {
                        Archiver.Copy(input, output, Job, Tracker, countBytes ? null : position);
                    }
                    finally
                    {
                        if (disposeInput)
                            input.Dispose();
                    }
                }
                if (time is DateTime t && t.Year > 1601)
                {
                    try
                    {
                        File.SetLastWriteTime(target, t);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        // keep the file with the current time
                    }
                }
                _written.Add(target);
                RememberTopLevel(target);
                Result.Succeeded++;
                Tracker.ItemDone();
                return;
            }
            catch (OperationCanceledException)
            {
                Archiver.TryDelete(target);
                throw;
            }
            catch (PasswordNeededException)
            {
                Archiver.TryDelete(target);
                throw;
            }
            catch (TargetWriteException ex)
            {
                // the target can't be written (disk full, access denied, file in use)
                Archiver.TryDelete(target);
                ErrorChoice choice = _skipAllErrors ? ErrorChoice.Skip : Archiver.Wait(Job.ResolveErrorAsync(new OperationError(target, ex.Message)));
                if (choice == ErrorChoice.Cancel)
                    throw new OperationCanceledException();
                if (choice == ErrorChoice.Retry && canRetry)
                {
                    Tracker.SetBytes(bytesBefore);
                    continue;
                }
                if (choice == ErrorChoice.SkipAll)
                    _skipAllErrors = true;
                Result.Failed++;
                Tracker.ItemDone();
                return;
            }
            catch (Exception ex)
            {
                // the archive's data is broken (or the password is wrong)
                Archiver.TryDelete(target);
                if (Archiver.IsPasswordProblem(ex, this))
                    throw new PasswordNeededException();
                ErrorChoice choice = _skipAllErrors ? ErrorChoice.Skip : Archiver.Wait(Job.ResolveErrorAsync(new OperationError(key ?? target, "The archive is damaged: " + ex.Message)));
                if (choice == ErrorChoice.Cancel)
                    throw new OperationCanceledException();
                if (choice == ErrorChoice.SkipAll)
                    _skipAllErrors = true;
                Result.Failed++;
                Tracker.ItemDone();
                return;
            }
        }
    }

    /// <summary>Creates the file to write; failures are write errors (not problems of the archive).</summary>
    private static FileStream CreateOutput(string target)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
                File.SetAttributes(target, FileAttributes.Normal); // overwriting a read-only file
            return new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TargetWriteException(ex.Message, ex);
        }
    }

    private void Fail(string target, string message, bool countBytes, long size)
    {
        ErrorChoice choice = _skipAllErrors ? ErrorChoice.Skip : Archiver.Wait(Job.ResolveErrorAsync(new OperationError(target, message)));
        if (choice == ErrorChoice.Cancel)
            throw new OperationCanceledException();
        if (choice == ErrorChoice.SkipAll)
            _skipAllErrors = true;
        Result.Failed++;
        if (countBytes && size > 0)
            Tracker.AddBytes(size);
        Tracker.ItemDone();
    }

    /// <summary>Returns the path to write (the same, or a free name for "keep both"), or null to skip.</summary>
    private string? ResolveConflict(string key, string target, long size, DateTime? time)
    {
        var existing = new FileInfo(target);
        DateTime sourceTime = time ?? DateTime.Now;
        ConflictChoice choice = _conflictForAll ?? Archiver.Wait(Job.ResolveConflictAsync(new ConflictInfo(
            Request.ArchivePath + "\\" + key.Replace('/', '\\'), Math.Max(0, size), sourceTime,
            target, existing.Length, existing.LastWriteTime, IsDirectory: false, TargetAttributes: existing.Attributes)));
        switch (choice)
        {
            case ConflictChoice.OverwriteAll:
            case ConflictChoice.SkipAll:
            case ConflictChoice.RenameAll:
            case ConflictChoice.OverwriteAllOlder:
                _conflictForAll = choice;
                break;
            case ConflictChoice.Cancel:
                throw new OperationCanceledException();
        }
        return choice switch
        {
            ConflictChoice.Overwrite or ConflictChoice.OverwriteAll => target,
            ConflictChoice.OverwriteAllOlder => existing.LastWriteTime < sourceTime ? target : null,
            ConflictChoice.Rename or ConflictChoice.RenameAll => FileOperationEngine.UniqueName(target),
            _ => null,
        };
    }
}
