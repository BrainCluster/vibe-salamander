using System.Formats.Tar;
using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Operations;

namespace VibeSalamander.Plugins.Archive;

/// <summary>Packing with .NET (ZIP, TAR, TAR.GZ) and unpacking with SharpCompress (and .NET for gzip).</summary>
internal sealed class Archiver : IArchiver
{
    private const int BufferSize = 1 << 20;

    public string DisplayName => "Archives (ZIP, 7-Zip, RAR, TAR)";

    public IReadOnlyList<ArchiveFormat> PackFormats { get; } = new[]
    {
        new ArchiveFormat("zip", "ZIP", ".zip", HasCompressionLevels: true),
        new ArchiveFormat("tar.gz", "TAR.GZ (gzip)", ".tar.gz", HasCompressionLevels: true),
        new ArchiveFormat("tar", "TAR (not compressed)", ".tar", HasCompressionLevels: false),
    };

    // longer endings first: "tar.gz" before "gz"
    public IReadOnlyList<string> UnpackExtensions { get; } = new[]
    {
        "tar.gz", "tar.bz2", "tar.xz", "tar.lz",
        "zip", "7z", "rar", "tar", "tgz", "tbz", "tbz2", "txz", "gz", "bz2", "xz", "lz",
        "jar", "cbz", "cbr", "cb7", "nupkg", "vsix", "apk", "epub", "xpi",
    };

    /// <summary>Formats read with random access (ArchiveFactory); everything else is read as a stream.</summary>
    private static readonly HashSet<string> RandomAccess = new(StringComparer.OrdinalIgnoreCase)
    {
        "zip", "7z", "rar", "jar", "cbz", "cbr", "cb7", "nupkg", "vsix", "apk", "epub", "xpi",
    };

    public bool CanUnpack(string archivePath) => KindOf(archivePath) != null;

    /// <summary>The matching extension of UnpackExtensions ("tar.gz"), or null.</summary>
    internal string? KindOf(string path)
    {
        string name = Path.GetFileName(path);
        foreach (string ext in UnpackExtensions)
            if (name.Length > ext.Length + 1 && name.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase))
                return ext;
        return null;
    }

    // ---- pack ---------------------------------------------------------------------------------

    private sealed record PackItem(string FullPath, string EntryName, bool IsDirectory, long Size, DateTime LastWrite);

    public Task<OperationResult> PackAsync(PackRequest request, IArchiveJob job) =>
        Task.Run(() => Pack(request, job));

    private static OperationResult Pack(PackRequest request, IArchiveJob job)
    {
        var result = new OperationResult();
        CancellationToken ct = job.Cancellation;
        string archivePath = Path.GetFullPath(request.ArchivePath);

        // 1. what goes in (names relative to the panel's directory)
        job.Report(new OperationProgress { Kind = OperationKind.Pack, Analyzing = true, CurrentSource = request.BaseDirectory });
        var items = new List<PackItem>();
        string baseDir = Path.GetFullPath(request.BaseDirectory);
        foreach (string source in request.Sources)
        {
            ct.ThrowIfCancellationRequested();
            Collect(Path.GetFullPath(source), baseDir, archivePath, items, job, result);
        }

        // 2. the archive exists: overwrite, keep both or skip
        if (File.Exists(archivePath))
        {
            var existing = new FileInfo(archivePath);
            ConflictChoice choice = Wait(job.ResolveConflictAsync(new ConflictInfo(
                request.Sources.Count == 1 ? request.Sources[0] : baseDir, items.Where(i => !i.IsDirectory).Sum(i => i.Size), DateTime.Now,
                archivePath, existing.Length, existing.LastWriteTime, IsDirectory: false, TargetAttributes: existing.Attributes)));
            switch (choice)
            {
                case ConflictChoice.Skip or ConflictChoice.SkipAll:
                    result.Skipped++;
                    return result;
                case ConflictChoice.Rename or ConflictChoice.RenameAll:
                    archivePath = FileOperationEngine.UniqueName(archivePath);
                    break;
                case ConflictChoice.Cancel:
                    result.Cancelled = true;
                    return result;
            }
        }

        // 3. write into a temporary file next to the archive; it replaces the archive when complete
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        string temp = archivePath + ".vibe-partial";
        var tracker = new ProgressTracker(OperationKind.Pack, items.Where(i => !i.IsDirectory).Sum(i => i.Size), items.Count(i => !i.IsDirectory));
        try
        {
            switch (request.FormatId)
            {
                case "zip":
                    PackZip(items, temp, request.Compression, job, tracker, result);
                    break;
                case "tar":
                case "tar.gz":
                    PackTar(items, temp, request.FormatId == "tar.gz", request.Compression, job, tracker, result);
                    break;
                default:
                    throw new NotSupportedException($"The archive format \"{request.FormatId}\" can't be created.");
            }
            ct.ThrowIfCancellationRequested();
            if (File.Exists(archivePath))
                File.SetAttributes(archivePath, FileAttributes.Normal);
            File.Move(temp, archivePath, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        tracker.ReportTo(job, force: true);
        result.CreatedTopLevelNames.Add(Path.GetFileName(archivePath));
        return result;
    }

    /// <summary>Adds a file, or a directory with its content (not following junctions).</summary>
    private static void Collect(string path, string baseDir, string archivePath, List<PackItem> items, IArchiveJob job, OperationResult result)
    {
        string entryName = EntryName(path, baseDir);
        if (File.Exists(path))
        {
            if (!string.Equals(path, archivePath, StringComparison.OrdinalIgnoreCase))
            {
                var info = new FileInfo(path);
                items.Add(new PackItem(path, entryName, false, info.Length, info.LastWriteTime));
            }
            return;
        }
        if (!Directory.Exists(path))
            return;
        var dir = new DirectoryInfo(path);
        items.Add(new PackItem(path, entryName, true, 0, dir.LastWriteTime));
        if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
            return; // a junction / symbolic link: packed as an empty directory, like Salamander
        IEnumerable<FileSystemInfo> children;
        try
        {
            children = dir.EnumerateFileSystemInfos().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorChoice choice = Wait(job.ResolveErrorAsync(new OperationError(path, ex.Message)));
            if (choice == ErrorChoice.Retry)
            {
                items.RemoveAt(items.Count - 1);
                Collect(path, baseDir, archivePath, items, job, result);
                return;
            }
            if (choice == ErrorChoice.Cancel)
                throw new OperationCanceledException();
            result.Skipped++;
            return;
        }
        job.Report(new OperationProgress { Kind = OperationKind.Pack, Analyzing = true, CurrentSource = path });
        foreach (FileSystemInfo child in children)
        {
            job.Cancellation.ThrowIfCancellationRequested();
            Collect(child.FullName, baseDir, archivePath, items, job, result);
        }
    }

    /// <summary>"sub/dir/file.txt": relative to the base directory, or just the name when outside it.</summary>
    internal static string EntryName(string path, string baseDir)
    {
        string relative = Path.GetRelativePath(baseDir, path);
        if (relative == ".." || relative.StartsWith("..\\", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            relative = Path.GetFileName(path.TrimEnd('\\'));
        return relative.Replace('\\', '/');
    }

    private static void PackZip(List<PackItem> items, string temp, ArchiveCompression compression, IArchiveJob job, ProgressTracker tracker, OperationResult result)
    {
        CompressionLevel level = Level(compression);
        using var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (PackItem item in items)
        {
            job.WaitWhilePaused();
            if (item.IsDirectory)
            {
                ZipArchiveEntry dirEntry = zip.CreateEntry(item.EntryName + "/");
                dirEntry.LastWriteTime = ZipTime(item.LastWrite);
                continue;
            }
            tracker.Start(item.FullPath);
            tracker.ReportTo(job, force: true);
            using FileStream? source = OpenSource(item, job, result);
            if (source == null)
                continue;
            ZipArchiveEntry entry = zip.CreateEntry(item.EntryName, level);
            entry.LastWriteTime = ZipTime(item.LastWrite);
            using (Stream target = entry.Open())
                Copy(source, target, job, tracker);
            tracker.ItemDone();
            result.Succeeded++;
        }
    }

    private static void PackTar(List<PackItem> items, string temp, bool gzip, ArchiveCompression compression, IArchiveJob job, ProgressTracker tracker, OperationResult result)
    {
        using var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        Stream output = gzip ? new GZipStream(file, Level(compression), leaveOpen: true) : file;
        try
        {
            using var tar = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true);
            foreach (PackItem item in items)
            {
                job.WaitWhilePaused();
                if (item.IsDirectory)
                {
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, item.EntryName + "/") { ModificationTime = TarTime(item.LastWrite) });
                    continue;
                }
                tracker.Start(item.FullPath);
                tracker.ReportTo(job, force: true);
                using FileStream? source = OpenSource(item, job, result);
                if (source == null)
                    continue;
                using var progress = new ProgressStream(source, job, tracker);
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, item.EntryName)
                {
                    ModificationTime = TarTime(item.LastWrite),
                    DataStream = progress,
                });
                tracker.ItemDone();
                result.Succeeded++;
            }
        }
        finally
        {
            if (gzip)
                output.Dispose();
        }
    }

    /// <summary>Opens a file to pack; asks on errors. Null = skipped.</summary>
    private static FileStream? OpenSource(PackItem item, IArchiveJob job, OperationResult result)
    {
        while (true)
        {
            try
            {
                return new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                switch (Wait(job.ResolveErrorAsync(new OperationError(item.FullPath, ex.Message))))
                {
                    case ErrorChoice.Retry:
                        continue;
                    case ErrorChoice.Cancel:
                        throw new OperationCanceledException();
                    default:
                        result.Skipped++;
                        return null;
                }
            }
        }
    }

    private static CompressionLevel Level(ArchiveCompression compression) => compression switch
    {
        ArchiveCompression.Store => CompressionLevel.NoCompression,
        ArchiveCompression.Fastest => CompressionLevel.Fastest,
        ArchiveCompression.Maximum => CompressionLevel.SmallestSize,
        _ => CompressionLevel.Optimal,
    };

    /// <summary>TarEntry.ModificationTime rejects times before 1970.</summary>
    private static DateTimeOffset TarTime(DateTime time)
    {
        var value = new DateTimeOffset(time);
        return value < DateTimeOffset.UnixEpoch ? DateTimeOffset.UnixEpoch : value;
    }

    /// <summary>ZIP stores times from 1980 to 2107.</summary>
    private static DateTimeOffset ZipTime(DateTime time)
    {
        var min = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
        var max = new DateTime(2107, 12, 31, 0, 0, 0, DateTimeKind.Local);
        return new DateTimeOffset(time < min ? min : time > max ? max : time);
    }

    // ---- unpack -------------------------------------------------------------------------------

    public Task<OperationResult> UnpackAsync(UnpackRequest request, IArchiveJob job) =>
        Task.Run(() => UnpackWithPassword(request, job));

    private OperationResult UnpackWithPassword(UnpackRequest request, IArchiveJob job)
    {
        var state = new UnpackState(request, job, KindOf(request.ArchivePath) ?? Path.GetExtension(request.ArchivePath).TrimStart('.'));
        Directory.CreateDirectory(state.TargetDirectory);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Unpack(state);
                return state.Result;
            }
            catch (PasswordNeededException)
            {
                // ask (again); files written before a wrong password was noticed are overwritten without asking
                string? password = Wait(job.AskPasswordAsync(request.ArchivePath, wrongPassword: state.Password != null));
                if (password == null)
                {
                    state.Result.Cancelled = true;
                    return state.Result;
                }
                if (attempt >= 10)
                    throw new InvalidOperationException("The password is wrong.");
                state.Password = password;
                state.ResetCounts();
            }
        }
    }

    private void Unpack(UnpackState s)
    {
        string kind = s.Kind;
        if (RandomAccess.Contains(kind))
            UnpackRandomAccess(s);
        else if (kind is "gz" or "bz2" or "xz" or "lz")
            UnpackSingleStream(s, kind);
        else
            UnpackStreamed(s);
    }

    /// <summary>ZIP, 7z, RAR: entries and sizes are known up front.</summary>
    private static void UnpackRandomAccess(UnpackState s)
    {
        using var file = new FileStream(s.Request.ArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        IArchive archive;
        try
        {
            archive = ArchiveFactory.OpenArchive(file, new ReaderOptions { Password = s.Password, LeaveStreamOpen = true });
        }
        catch (Exception ex) when (IsPasswordProblem(ex, s))
        {
            throw new PasswordNeededException();
        }
        using (archive)
        {
            List<IArchiveEntry> entries;
            try
            {
                entries = archive.Entries.ToList();
            }
            catch (Exception ex) when (IsPasswordProblem(ex, s))
            {
                throw new PasswordNeededException(); // 7-Zip with encrypted names
            }
            s.Encrypted = entries.Any(e => e.IsEncrypted);
            if (s.Encrypted && s.Password == null)
                throw new PasswordNeededException();
            var files = entries.Where(e => !e.IsDirectory && s.Matches(e.Key)).ToList();
            s.Tracker = new ProgressTracker(OperationKind.Unpack, files.Sum(e => Math.Max(0, e.Size)), files.Count);

            if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
            {
                // solid archives decompress in one pass
                using IReader reader = archive.ExtractAllEntries();
                while (NextEntry(reader, s))
                {
                    IEntry entry = reader.Entry;
                    if (entry.IsDirectory)
                        s.CreateDirectoryEntry(entry.Key);
                    else if (s.Matches(entry.Key))
                        s.Extract(entry.Key, entry.Size, entry.LastModifiedTime, () => reader.OpenEntryStream(), countBytes: true);
                }
            }
            else
            {
                foreach (IArchiveEntry entry in entries)
                {
                    s.Job.Cancellation.ThrowIfCancellationRequested();
                    if (entry.IsDirectory)
                        s.CreateDirectoryEntry(entry.Key);
                    else if (s.Matches(entry.Key))
                        s.Extract(entry.Key, entry.Size, entry.LastModifiedTime, entry.OpenEntryStream, countBytes: true, canRetry: true);
                }
            }
        }
    }

    /// <summary>
    /// TAR (also .tar.gz/.tgz/.tar.bz2/.tar.xz/.tar.lz) with .NET's TAR reader (pax, GNU and ustar
    /// headers): read front to back; progress by the position in the archive file.
    /// </summary>
    private static void UnpackStreamed(UnpackState s)
    {
        using var file = new FileStream(s.Request.ArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        s.Tracker = new ProgressTracker(OperationKind.Unpack, file.Length, 0);
        using Stream data = s.Kind switch
        {
            "tar.gz" or "tgz" => new GZipStream(file, CompressionMode.Decompress, leaveOpen: true),
            "tar.bz2" or "tbz" or "tbz2" => SharpCompress.Compressors.BZip2.BZip2Stream.Create(
                new KeepOpenStream(file), SharpCompress.Compressors.CompressionMode.Decompress, decompressConcatenated: true),
            "tar.xz" or "txz" => new SharpCompress.Compressors.Xz.XZStream(new KeepOpenStream(file)),
            "tar.lz" => SharpCompress.Compressors.LZMA.LZipStream.Create(
                new KeepOpenStream(file), SharpCompress.Compressors.CompressionMode.Decompress),
            _ => new KeepOpenStream(file),
        };
        using var reader = new TarReader(data, leaveOpen: true);
        while (true)
        {
            s.Job.Cancellation.ThrowIfCancellationRequested();
            TarEntry? entry;
            try
            {
                entry = reader.GetNextEntry(copyData: false);
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or EndOfStreamException)
            {
                throw new InvalidDataException("This is not a TAR archive, or it is damaged: " + ex.Message, ex);
            }
            if (entry == null)
                break;
            s.Tracker.SetBytes(SafePosition(file));
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    s.CreateDirectoryEntry(entry.Name);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                    if (s.Matches(entry.Name))
                    {
                        s.Tracker.ItemsTotal++;
                        TarEntry current = entry;
                        // the entry's data belongs to the reader (it skips to the next entry by itself): not disposed here
                        s.Extract(current.Name, current.Length, current.ModificationTime.LocalDateTime,
                            () => current.DataStream ?? Stream.Null, countBytes: false,
                            position: () => SafePosition(file), disposeInput: false);
                    }
                    break;
                case TarEntryType.SymbolicLink or TarEntryType.HardLink:
                    s.Result.Skipped++; // links aren't created (they could point anywhere)
                    break;
            }
        }
        s.Tracker.SetBytes(file.Length);
    }

    /// <summary>A single compressed file (notes.txt.gz → notes.txt).</summary>
    private static void UnpackSingleStream(UnpackState s, string kind)
    {
        string archiveName = Path.GetFileName(s.Request.ArchivePath);
        string name = archiveName[..^(kind.Length + 1)];
        if (name.Length == 0)
            name = "unpacked";
        if (!s.Matches(name))
            return;
        using var file = new FileStream(s.Request.ArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        s.Tracker = new ProgressTracker(OperationKind.Unpack, file.Length, 1);
        DateTime time = File.GetLastWriteTime(s.Request.ArchivePath);
        s.Extract(name, -1, time, () => kind switch
        {
            "gz" => new GZipStream(file, CompressionMode.Decompress, leaveOpen: true),
            "bz2" => SharpCompress.Compressors.BZip2.BZip2Stream.Create(file, SharpCompress.Compressors.CompressionMode.Decompress, decompressConcatenated: true),
            "xz" => new SharpCompress.Compressors.Xz.XZStream(file),
            _ => SharpCompress.Compressors.LZMA.LZipStream.Create(file, SharpCompress.Compressors.CompressionMode.Decompress),
        }, countBytes: false, position: () => SafePosition(file));
    }

    private static bool NextEntry(IReader reader, UnpackState s)
    {
        s.Job.Cancellation.ThrowIfCancellationRequested();
        try
        {
            return reader.MoveToNextEntry();
        }
        catch (Exception ex) when (IsPasswordProblem(ex, s))
        {
            throw new PasswordNeededException();
        }
    }

    private static long SafePosition(Stream stream)
    {
        try
        {
            return stream.Position;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or NotSupportedException)
        {
            return 0;
        }
    }

    /// <summary>A wrong or missing password shows up as one of these, depending on the format.</summary>
    internal static bool IsPasswordProblem(Exception ex, UnpackState s)
    {
        if (ex is TargetWriteException or OperationCanceledException)
            return false;
        if (ex is SharpCompress.Common.CryptographicException or System.Security.Cryptography.CryptographicException)
            return true;
        if (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
            return true;
        // AES with a wrong key just produces garbage: any data or CRC error in an encrypted archive
        return s.Encrypted;
    }

    // ---- helpers ------------------------------------------------------------------------------

    /// <summary>Copies with pause/cancel checks and progress.</summary>
    internal static void Copy(Stream source, Stream target, IArchiveJob job, ProgressTracker tracker, Func<long>? position = null)
    {
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                job.WaitWhilePaused();
                job.Cancellation.ThrowIfCancellationRequested();
                try
                {
                    target.Write(buffer, 0, read);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new TargetWriteException(ex.Message, ex);
                }
                if (position != null)
                    tracker.SetBytes(position());
                else
                    tracker.AddBytes(read);
                tracker.ReportTo(job);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Waits for an answer from the UI (we are on a worker thread).</summary>
    internal static T Wait<T>(Task<T> task) => task.GetAwaiter().GetResult();

    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a leftover partial file is better than a crash
        }
    }
}

/// <summary>The archive needs a (different) password.</summary>
internal sealed class PasswordNeededException : Exception
{
}

/// <summary>Writing the unpacked file failed (not a problem of the archive or its password).</summary>
internal sealed class TargetWriteException : Exception
{
    public TargetWriteException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>Reads a source file for the TAR writer, with progress and pause/cancel checks.</summary>
internal sealed class ProgressStream : Stream
{
    private readonly Stream _inner;
    private readonly IArchiveJob _job;
    private readonly ProgressTracker _tracker;

    public ProgressStream(Stream inner, IArchiveJob job, ProgressTracker tracker)
    {
        _inner = inner;
        _job = job;
        _tracker = tracker;
    }

    public override bool CanRead => true;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        _job.WaitWhilePaused();
        _job.Cancellation.ThrowIfCancellationRequested();
        int read = _inner.Read(buffer, offset, count);
        _tracker.AddBytes(read);
        _tracker.ReportTo(_job);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        _job.WaitWhilePaused();
        _job.Cancellation.ThrowIfCancellationRequested();
        int read = _inner.Read(buffer);
        _tracker.AddBytes(read);
        _tracker.ReportTo(_job);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Passes everything through but doesn't close the archive file (decompressors may dispose their input).</summary>
internal sealed class KeepOpenStream : Stream
{
    private readonly Stream _inner;

    public KeepOpenStream(Stream inner) => _inner = inner;

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => _inner.Read(buffer);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // the archive file stays open: its owner disposes it
    }
}
