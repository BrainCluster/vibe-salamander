// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.Operations;

namespace VibeSalamander.Plugins.Ftp;

/// <summary>
/// One copy or delete in the transfer panel, on its own connection: plans first (what, how many bytes),
/// then works through the files with progress, pause and cancel, and asks about existing files and
/// errors like local copies do.
/// </summary>
internal sealed class Transfer
{
    private readonly IRemoteClient _client;
    private readonly IArchiveJob _job;
    private readonly OperationResult _result;
    private readonly CancellationToken _ct;
    private ConflictChoice? _conflictForAll;
    private bool _skipAllErrors;

    public Transfer(IRemoteClient client, IArchiveJob job, OperationResult result)
    {
        _client = client;
        _job = job;
        _result = result;
        _ct = job.Cancellation;
    }

    private static T Wait<T>(Task<T> task) => task.GetAwaiter().GetResult();

    // ---- download -----------------------------------------------------------------------------------

    private sealed record DownloadItem(RemoteEntry Entry, string LocalPath);

    public async Task DownloadAsync(RemotePath server, IReadOnlyList<RemoteItem> items, string targetDirectory)
    {
        _job.Report(new OperationProgress { Kind = OperationKind.Copy, Analyzing = true, CurrentSource = server.ServerKey });
        var plan = new List<DownloadItem>();
        foreach (RemoteItem item in items)
        {
            _ct.ThrowIfCancellationRequested();
            RemotePath path = RemotePath.Parse(item.Path) ?? server.WithPath(item.Path);
            var entry = new RemoteEntry(item.Name, path.Path, item.IsDirectory, item.Size, item.LastWrite);
            string local = Path.Combine(targetDirectory, LocalName(item.Name));
            if (item.IsDirectory)
                await PlanDirectoryAsync(entry, local, plan);
            else
                plan.Add(new DownloadItem(entry, local));
            _result.CreatedTopLevelNames.Add(LocalName(item.Name));
        }

        var tracker = new ProgressTracker(OperationKind.Copy, plan.Where(p => !p.Entry.IsDirectory).Sum(p => p.Entry.Size),
            plan.Count(p => !p.Entry.IsDirectory));
        foreach (DownloadItem item in plan)
        {
            _job.WaitWhilePaused();
            _ct.ThrowIfCancellationRequested();
            if (item.Entry.IsDirectory)
            {
                Directory.CreateDirectory(item.LocalPath);
                continue;
            }
            tracker.Start(item.Entry.Path);
            tracker.ReportTo(_job, force: true);
            string? local = ResolveLocalConflict(item);
            if (local == null)
            {
                tracker.AddBytes(item.Entry.Size);
                tracker.ItemDone();
                continue;
            }
            await WithRetryAsync(item.Entry.Path, async () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                string partial = local + ".vibe-partial";
                long before = tracker.BytesDone;
                try
                {
                    await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                    await using (var counted = new JobStream(file, _job, tracker))
                        await _client.DownloadAsync(item.Entry.Path, counted, _ct);
                    File.Move(partial, local, overwrite: true);
                    if (item.Entry.LastWrite > DateTime.MinValue)
                        File.SetLastWriteTime(local, item.Entry.LastWrite);
                }
                catch
                {
                    TryDelete(partial);
                    tracker.SetBytes(before); // a retry counts again
                    throw;
                }
            }, done: () =>
            {
                tracker.ItemDone();
                _result.Succeeded++;
            });
        }
        tracker.ReportTo(_job, force: true);
    }

    /// <summary>A directory's content, depth first (directories before their files).</summary>
    private async Task PlanDirectoryAsync(RemoteEntry directory, string local, List<DownloadItem> plan)
    {
        plan.Add(new DownloadItem(directory, local));
        _job.Report(new OperationProgress { Kind = OperationKind.Copy, Analyzing = true, CurrentSource = directory.Path });
        List<RemoteEntry>? children = null;
        await WithRetryAsync(directory.Path, async () => children = await _client.ListAsync(directory.Path, _ct), done: () => { });
        foreach (RemoteEntry child in children ?? new List<RemoteEntry>())
        {
            _ct.ThrowIfCancellationRequested();
            string childLocal = Path.Combine(local, LocalName(child.Name));
            if (child.IsDirectory && !child.IsLink)
                await PlanDirectoryAsync(child, childLocal, plan);
            else if (!child.IsDirectory)
                plan.Add(new DownloadItem(child, childLocal));
            // links to directories aren't followed (they could loop)
        }
    }

    /// <summary>Characters Windows doesn't allow in names become '_'.</summary>
    internal static string LocalName(string name)
    {
        char[] chars = name.ToCharArray();
        foreach (char bad in Path.GetInvalidFileNameChars())
            for (int i = 0; i < chars.Length; i++)
                if (chars[i] == bad)
                    chars[i] = '_';
        string result = new string(chars).TrimEnd(' ', '.');
        return result.Length == 0 ? "_" : result;
    }

    /// <summary>Where the file goes, or null to skip it (the local file exists: asks).</summary>
    private string? ResolveLocalConflict(DownloadItem item)
    {
        if (!File.Exists(item.LocalPath))
            return item.LocalPath;
        var existing = new FileInfo(item.LocalPath);
        ConflictChoice choice = Decide(new ConflictInfo(item.Entry.Path, item.Entry.Size, item.Entry.LastWrite,
            item.LocalPath, existing.Length, existing.LastWriteTime, IsDirectory: false, TargetAttributes: existing.Attributes),
            existing.LastWriteTime < item.Entry.LastWrite);
        switch (choice)
        {
            case ConflictChoice.Skip:
                _result.Skipped++;
                return null;
            case ConflictChoice.Rename:
                return FileOperationEngine.UniqueName(item.LocalPath);
            default:
                if ((existing.Attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(item.LocalPath, existing.Attributes & ~FileAttributes.ReadOnly);
                return item.LocalPath;
        }
    }

    // ---- upload -------------------------------------------------------------------------------------

    private sealed record UploadItem(string LocalPath, string RemotePath, bool IsDirectory, long Size, DateTime LastWrite);

    public async Task UploadAsync(IReadOnlyList<string> sources, RemotePath target)
    {
        _job.Report(new OperationProgress { Kind = OperationKind.Copy, Analyzing = true, CurrentSource = target.ToString() });
        var plan = new List<UploadItem>();
        foreach (string source in sources)
        {
            _ct.ThrowIfCancellationRequested();
            string full = Path.GetFullPath(source).TrimEnd('\\');
            Collect(full, RemoteClients.Join(target.Path, Path.GetFileName(full)), plan);
            _result.CreatedTopLevelNames.Add(Path.GetFileName(full));
        }

        var tracker = new ProgressTracker(OperationKind.Copy, plan.Where(p => !p.IsDirectory).Sum(p => p.Size), plan.Count(p => !p.IsDirectory));
        // what is there already, one listing per directory
        var listings = new Dictionary<string, Dictionary<string, RemoteEntry>>(StringComparer.Ordinal);
        async Task<Dictionary<string, RemoteEntry>> ListingOf(string directory)
        {
            if (listings.TryGetValue(directory, out var known))
                return known;
            Dictionary<string, RemoteEntry> names;
            try
            {
                names = (await _client.ListAsync(directory, _ct)).GroupBy(e => e.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                names = new Dictionary<string, RemoteEntry>(StringComparer.Ordinal); // a new directory
            }
            listings[directory] = names;
            return names;
        }

        foreach (UploadItem item in plan)
        {
            _job.WaitWhilePaused();
            _ct.ThrowIfCancellationRequested();
            string parent = ParentOf(item.RemotePath);
            string name = item.RemotePath[(item.RemotePath.LastIndexOf('/') + 1)..];
            Dictionary<string, RemoteEntry> there = await ListingOf(parent);
            if (item.IsDirectory)
            {
                if (!there.TryGetValue(name, out RemoteEntry? existingDir) || !existingDir.IsDirectory)
                {
                    await WithRetryAsync(item.RemotePath, () => _client.CreateDirectoryAsync(item.RemotePath, _ct), done: () => { });
                    there[name] = new RemoteEntry(name, item.RemotePath, true, 0, DateTime.Now);
                    listings[item.RemotePath] = new Dictionary<string, RemoteEntry>(StringComparer.Ordinal);
                }
                continue;
            }
            tracker.Start(item.LocalPath);
            tracker.ReportTo(_job, force: true);
            string remotePath = item.RemotePath;
            if (there.TryGetValue(name, out RemoteEntry? existing))
            {
                ConflictChoice choice = Decide(new ConflictInfo(item.LocalPath, item.Size, item.LastWrite,
                    remotePath, existing.Size, existing.LastWrite, IsDirectory: false), existing.LastWrite < item.LastWrite);
                if (choice == ConflictChoice.Skip)
                {
                    _result.Skipped++;
                    tracker.AddBytes(item.Size);
                    tracker.ItemDone();
                    continue;
                }
                if (choice == ConflictChoice.Rename)
                    remotePath = FreeName(parent, name, there);
            }
            await WithRetryAsync(item.LocalPath, async () =>
            {
                long before = tracker.BytesDone;
                try
                {
                    await using var file = new FileStream(item.LocalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                    await using var counted = new JobStream(file, _job, tracker);
                    await _client.UploadAsync(counted, remotePath, _ct);
                }
                catch
                {
                    tracker.SetBytes(before);
                    throw;
                }
                await _client.SetLastWriteAsync(remotePath, item.LastWrite, _ct);
            }, done: () =>
            {
                string uploaded = remotePath[(remotePath.LastIndexOf('/') + 1)..];
                there[uploaded] = new RemoteEntry(uploaded, remotePath, false, item.Size, item.LastWrite);
                tracker.ItemDone();
                _result.Succeeded++;
            });
        }
        tracker.ReportTo(_job, force: true);
    }

    private static string ParentOf(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }

    /// <summary>"name (2).ext" in a remote directory.</summary>
    private static string FreeName(string directory, string name, Dictionary<string, RemoteEntry> there)
    {
        string stem = Path.GetFileNameWithoutExtension(name), extension = Path.GetExtension(name);
        for (int i = 2; ; i++)
        {
            string candidate = $"{stem} ({i}){extension}";
            if (!there.ContainsKey(candidate))
                return RemoteClients.Join(directory, candidate);
        }
    }

    /// <summary>A file, or a directory with its content (junctions and links are copied as empty directories).</summary>
    private void Collect(string local, string remote, List<UploadItem> plan)
    {
        if (File.Exists(local))
        {
            var info = new FileInfo(local);
            plan.Add(new UploadItem(local, remote, false, info.Length, info.LastWriteTime));
            return;
        }
        if (!Directory.Exists(local))
            return;
        var dir = new DirectoryInfo(local);
        plan.Add(new UploadItem(local, remote, true, 0, dir.LastWriteTime));
        if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
            return;
        _job.Report(new OperationProgress { Kind = OperationKind.Copy, Analyzing = true, CurrentSource = local });
        IEnumerable<FileSystemInfo> children;
        try
        {
            children = dir.EnumerateFileSystemInfos().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!AskAboutError(local, ex.Message))
                return;
            Collect(local, remote, plan); // retry
            return;
        }
        foreach (FileSystemInfo child in children)
        {
            _ct.ThrowIfCancellationRequested();
            Collect(child.FullName, RemoteClients.Join(remote, child.Name), plan);
        }
    }

    // ---- delete -------------------------------------------------------------------------------------

    public async Task DeleteAsync(IReadOnlyList<RemoteItem> items)
    {
        foreach (RemoteItem item in items)
        {
            _job.WaitWhilePaused();
            _ct.ThrowIfCancellationRequested();
            RemotePath path = RemotePath.Parse(item.Path) ?? throw new ArgumentException(item.Path);
            if (item.IsDirectory && !item.IsLink)
                await DeleteDirectoryAsync(path.Path);
            else
                await WithRetryAsync(path.Path, () => _client.DeleteFileAsync(path.Path, _ct), done: () => Deleted(path.Path));
        }
    }

    private void Deleted(string path)
    {
        _result.Succeeded++;
        _job.Report(new OperationProgress { Kind = OperationKind.Delete, CurrentSource = path, ItemsDone = _result.Succeeded });
    }

    /// <summary>Content first (SFTP removes only empty directories), then the directory itself.</summary>
    private async Task DeleteDirectoryAsync(string path)
    {
        _job.Report(new OperationProgress { Kind = OperationKind.Delete, CurrentSource = path, ItemsDone = _result.Succeeded });
        List<RemoteEntry>? children = null;
        await WithRetryAsync(path, async () => children = await _client.ListAsync(path, _ct), done: () => { });
        foreach (RemoteEntry child in children ?? new List<RemoteEntry>())
        {
            _job.WaitWhilePaused();
            _ct.ThrowIfCancellationRequested();
            if (child.IsDirectory && !child.IsLink)
                await DeleteDirectoryAsync(child.Path);
            else
                await WithRetryAsync(child.Path, () => _client.DeleteFileAsync(child.Path, _ct), done: () => Deleted(child.Path));
        }
        await WithRetryAsync(path, () => _client.DeleteDirectoryAsync(path, _ct), done: () => Deleted(path));
    }

    // ---- questions ----------------------------------------------------------------------------------

    /// <summary>Overwrite, skip or keep both (with the "for all" answers remembered).</summary>
    private ConflictChoice Decide(ConflictInfo conflict, bool targetIsOlder)
    {
        ConflictChoice choice = _conflictForAll ?? Wait(_job.ResolveConflictAsync(conflict));
        switch (choice)
        {
            case ConflictChoice.OverwriteAll:
                _conflictForAll = ConflictChoice.Overwrite;
                return ConflictChoice.Overwrite;
            case ConflictChoice.SkipAll:
                _conflictForAll = ConflictChoice.Skip;
                return ConflictChoice.Skip;
            case ConflictChoice.RenameAll:
                _conflictForAll = ConflictChoice.Rename;
                return ConflictChoice.Rename;
            case ConflictChoice.OverwriteAllOlder:
                _conflictForAll = ConflictChoice.OverwriteAllOlder;
                return targetIsOlder ? ConflictChoice.Overwrite : ConflictChoice.Skip;
            case ConflictChoice.Cancel:
                throw new OperationCanceledException();
            default:
                return choice;
        }
    }

    /// <summary>Runs an operation; on an error asks retry / skip / cancel. <paramref name="done"/> runs when it worked.</summary>
    private async Task WithRetryAsync(string path, Func<Task> work, Action done)
    {
        while (true)
        {
            try
            {
                await work();
                done();
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (_ct.IsCancellationRequested)
                    throw new OperationCanceledException();
                if (!AskAboutError(path, ex.Message))
                    return;
            }
        }
    }

    /// <summary>True = try again; false = skip it. Cancel throws.</summary>
    private bool AskAboutError(string path, string message)
    {
        if (_skipAllErrors)
        {
            _result.Failed++;
            return false;
        }
        switch (Wait(_job.ResolveErrorAsync(new OperationError(path, message))))
        {
            case ErrorChoice.Retry:
                return true;
            case ErrorChoice.SkipAll:
                _skipAllErrors = true;
                _result.Failed++;
                return false;
            case ErrorChoice.Cancel:
                throw new OperationCanceledException();
            default:
                _result.Failed++;
                return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a leftover partial file
        }
    }
}

/// <summary>Counts the bytes going through for the progress, and holds them while the transfer is paused.</summary>
internal sealed class JobStream : Stream
{
    private readonly Stream _inner;
    private readonly IArchiveJob _job;
    private readonly ProgressTracker _tracker;

    public JobStream(Stream inner, IArchiveJob job, ProgressTracker tracker)
    {
        _inner = inner;
        _job = job;
        _tracker = tracker;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    private void Count(int bytes)
    {
        _job.WaitWhilePaused();
        _job.Cancellation.ThrowIfCancellationRequested();
        _tracker.AddBytes(bytes);
        _tracker.ReportTo(_job);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = _inner.Read(buffer, offset, count);
        Count(read);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        int read = _inner.Read(buffer);
        Count(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await _inner.ReadAsync(buffer, cancellationToken);
        Count(read);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
        Count(count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _inner.Write(buffer);
        Count(buffer.Length);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _inner.WriteAsync(buffer, cancellationToken);
        Count(buffer.Length);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        // the inner stream belongs to the caller
    }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
