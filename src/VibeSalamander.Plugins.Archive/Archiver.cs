// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Formats.Tar;
using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Operations;

namespace VibeSalamander.Plugins.Archive;

/// <summary>
/// Packing (own ZIP writer, .NET for TAR and TAR.GZ), unpacking with SharpCompress (and .NET for gzip and TAR),
/// and changing ZIP and TAR archives in place (Archiver.Edit.cs).
/// </summary>
internal sealed partial class Archiver : IArchiver
{
    private const int BufferSize = 1 << 20;

    public string DisplayName => "Archives (ZIP, 7-Zip, RAR, TAR)";

    public IReadOnlyList<ArchiveFormat> PackFormats { get; } = new[]
    {
        new ArchiveFormat("zip", "ZIP", ".zip", HasCompressionLevels: true, SupportsPassword: true, IsZip: true),
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

    /// <summary>Comic books and e-books open in their own apps on Enter; the rest is browsed like a directory.</summary>
    private static readonly HashSet<string> NotBrowsed = new(StringComparer.OrdinalIgnoreCase) { "cbz", "cbr", "cb7", "epub" };

    public bool CanBrowse(string archivePath) => KindOf(archivePath) is string kind && !NotBrowsed.Contains(kind);

    // ---- list (browsing in a panel) -----------------------------------------------------------

    public Task<IReadOnlyList<ArchiveItem>> ListAsync(string archivePath, CancellationToken cancellationToken) =>
        Task.Run(() => List(archivePath, cancellationToken), cancellationToken);

    private IReadOnlyList<ArchiveItem> List(string archivePath, CancellationToken ct)
    {
        string kind = KindOf(archivePath) ?? Path.GetExtension(archivePath).TrimStart('.');
        DateTime archiveTime = File.GetLastWriteTime(archivePath);
        var items = new List<ArchiveItem>();
        if (RandomAccess.Contains(kind))
        {
            using Stream file = OpenRead(archivePath);
            using IArchive archive = OpenRandomAccess(archivePath, file, new ReaderOptions { LeaveStreamOpen = true });
            foreach (IArchiveEntry entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (UnpackState.Normalize(entry.Key) is string key)
                    items.Add(new ArchiveItem(key, entry.IsDirectory, Math.Max(0, entry.Size), entry.LastModifiedTime ?? archiveTime));
            }
        }
        else if (kind is "gz" or "bz2" or "xz" or "lz")
        {
            string name = Path.GetFileName(WithoutPartNumber(archivePath))[..^(kind.Length + 1)];
            items.Add(new ArchiveItem(name.Length > 0 ? name : "unpacked", false, 0, archiveTime));
        }
        else
        {
            using Stream file = OpenRead(archivePath);
            using Stream data = OpenTarData(kind, file);
            using var reader = new TarReader(data, leaveOpen: true);
            while (reader.GetNextEntry(copyData: false) is TarEntry entry)
            {
                ct.ThrowIfCancellationRequested();
                if (UnpackState.Normalize(entry.Name) is not string key)
                    continue;
                if (entry.EntryType == TarEntryType.Directory)
                    items.Add(new ArchiveItem(key, true, 0, entry.ModificationTime.LocalDateTime));
                else if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile)
                    items.Add(new ArchiveItem(key, false, entry.Length, entry.ModificationTime.LocalDateTime));
            }
        }
        return items;
    }

    /// <summary>
    /// ZIP, 7z, RAR: a RAR in several volumes (name.part1.rar, name.part2.rar, ... or name.rar, name.r00,
    /// name.r01, ...) is opened with all its volumes, otherwise files that continue in the next volume are
    /// "incomplete".
    /// </summary>
    internal static IArchive OpenRandomAccess(string path, Stream file, ReaderOptions options)
    {
        if (path.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cbr", StringComparison.OrdinalIgnoreCase))
        {
            List<FileInfo> volumes = RarVolumes(path);
            if (volumes.Count > 1)
                return ArchiveFactory.OpenArchive(volumes, options);
        }
        return ArchiveFactory.OpenArchive(file, options);
    }

    private static readonly System.Text.RegularExpressions.Regex PartName =
        new(@"^(?<base>.*)\.part(?<n>\d+)\.(?<ext>rar|cbr)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>The volumes of a RAR archive in order, starting with the first (any volume may be given).</summary>
    internal static List<FileInfo> RarVolumes(string path)
    {
        string directory = Path.GetDirectoryName(path) ?? ".";
        string name = Path.GetFileName(path);
        System.Text.RegularExpressions.Match part = PartName.Match(name);
        if (part.Success)
        {
            // new naming: name.part1.rar, name.part2.rar, ... (also part01, part001)
            string baseName = part.Groups["base"].Value;
            var volumes = Directory.EnumerateFiles(directory, baseName + ".part*." + part.Groups["ext"].Value)
                .Select(f => (Path: f, Match: PartName.Match(Path.GetFileName(f))))
                .Where(v => v.Match.Success && string.Equals(v.Match.Groups["base"].Value, baseName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(v => long.Parse(v.Match.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture))
                .Select(v => new FileInfo(v.Path))
                .ToList();
            return volumes.Count > 0 ? volumes : new List<FileInfo> { new(path) };
        }
        // old naming: name.rar, then name.r00 ... name.r99, name.s00 ...
        var list = new List<FileInfo> { new(path) };
        string stem = Path.Combine(directory, Path.GetFileNameWithoutExtension(name));
        for (char letter = 'r'; letter <= 'z'; letter++)
        {
            for (int i = 0; i < 100; i++)
            {
                string next = $"{stem}.{letter}{i:00}";
                if (!File.Exists(next))
                    return list;
                list.Add(new FileInfo(next));
            }
        }
        return list;
    }

    /// <summary>The TAR data of a .tar / .tar.gz / .tar.bz2 / .tar.xz / .tar.lz file (the file itself stays open).</summary>
    internal static Stream OpenTarData(string kind, Stream file) => kind switch
    {
        "tar.gz" or "tgz" => new GZipStream(file, CompressionMode.Decompress, leaveOpen: true),
        "tar.bz2" or "tbz" or "tbz2" => SharpCompress.Compressors.BZip2.BZip2Stream.Create(
            new KeepOpenStream(file), SharpCompress.Compressors.CompressionMode.Decompress, decompressConcatenated: true),
        "tar.xz" or "txz" => new SharpCompress.Compressors.Xz.XZStream(new KeepOpenStream(file)),
        "tar.lz" => SharpCompress.Compressors.LZMA.LZipStream.Create(
            new KeepOpenStream(file), SharpCompress.Compressors.CompressionMode.Decompress),
        _ => new KeepOpenStream(file),
    };

    /// <summary>
    /// "backup.zip.001" → "backup.zip": the first part of an archive split into parts of equal size (7-Zip's
    /// "Split to volumes"); other paths stay as they are.
    /// </summary>
    internal static string WithoutPartNumber(string path) =>
        path.EndsWith(".001", StringComparison.Ordinal) && path.Length > 4 ? path[..^4] : path;

    /// <summary>Reads an archive file, or all the parts of a split one (".001", ".002", ...) as one stream.</summary>
    internal static Stream OpenRead(string path)
    {
        if (WithoutPartNumber(path) is var whole && whole != path)
        {
            var parts = new List<string>();
            for (int i = 1; i < 10000; i++)
            {
                string part = $"{whole}.{i:000}";
                if (!File.Exists(part))
                    break;
                parts.Add(part);
            }
            if (parts.Count > 1)
                return new PartsStream(parts);
        }
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
    }

    /// <summary>The matching extension of UnpackExtensions ("tar.gz"), or null.</summary>
    internal string? KindOf(string path)
    {
        string name = Path.GetFileName(WithoutPartNumber(path));
        foreach (string ext in UnpackExtensions)
            if (name.Length > ext.Length + 1 && name.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase))
                return ext;
        return null;
    }

    // ---- pack ---------------------------------------------------------------------------------

    internal sealed record PackItem(string FullPath, string EntryName, bool IsDirectory, long Size, DateTime LastWrite);

    public Task<OperationResult> PackAsync(PackRequest request, IArchiveJob job) =>
        Task.Run(() => Pack(request, job));

    /// <summary>Where the self-extractor is kept once compiled (the plugin's data directory).</summary>
    public string? DataDirectory { get; set; }

    private OperationResult Pack(PackRequest request, IArchiveJob job)
    {
        var result = new OperationResult();
        CancellationToken ct = job.Cancellation;
        string archivePath = Path.GetFullPath(request.ArchivePath);
        bool zip = request.FormatId == "zip";
        bool selfExtracting = zip && request.SelfExtracting && string.IsNullOrEmpty(request.Password);

        // 0. the archive exists and is to be updated (7-Zip's update modes): files go into it in place
        if (request.UpdateMode != PackUpdateMode.Replace && !selfExtracting && File.Exists(archivePath) && CanEdit(archivePath))
        {
            return Edit(archivePath, job, new ArchiveAddRequest
            {
                ArchivePath = archivePath,
                Sources = request.Sources,
                Compression = request.Compression,
                UpdateMode = request.UpdateMode,
                PathMode = request.PathMode,
                Password = request.Password,
            }, null);
        }

        // 1. what goes in (names relative to the panel's directory, or as the path mode says)
        job.Report(new OperationProgress { Kind = OperationKind.Pack, Analyzing = true, CurrentSource = request.BaseDirectory });
        var items = new List<PackItem>();
        string baseDir = Path.GetFullPath(request.BaseDirectory);
        foreach (string source in request.Sources)
        {
            ct.ThrowIfCancellationRequested();
            Collect(Path.GetFullPath(source), baseDir, archivePath, items, job, result);
        }
        items = ApplyPathMode(items, request.PathMode);

        // what is written in the end: the .exe of a self-extracting archive, else the archive (or its parts)
        string finalPath = selfExtracting ? Path.ChangeExtension(archivePath, ".exe") : archivePath;

        // 2. the archive exists: overwrite, keep both or skip
        if (File.Exists(finalPath) || (request.VolumeSize > 0 && File.Exists(finalPath + ".001")))
        {
            string existingPath = File.Exists(finalPath) ? finalPath : finalPath + ".001";
            var existing = new FileInfo(existingPath);
            ConflictChoice choice = Wait(job.ResolveConflictAsync(new ConflictInfo(
                request.Sources.Count == 1 ? request.Sources[0] : baseDir, items.Where(i => !i.IsDirectory).Sum(i => i.Size), DateTime.Now,
                existingPath, existing.Length, existing.LastWriteTime, IsDirectory: false, TargetAttributes: existing.Attributes)));
            switch (choice)
            {
                case ConflictChoice.Skip or ConflictChoice.SkipAll:
                    result.Skipped++;
                    return result;
                case ConflictChoice.Rename or ConflictChoice.RenameAll:
                    finalPath = FileOperationEngine.UniqueName(finalPath);
                    archivePath = selfExtracting ? Path.ChangeExtension(finalPath, Path.GetExtension(archivePath)) : finalPath;
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
                    PackZip(items, temp, request, job, tracker, result);
                    break;
                case "tar":
                case "tar.gz":
                    if (!string.IsNullOrEmpty(request.Password))
                        throw new NotSupportedException("TAR archives can't be encrypted: choose ZIP for a password.");
                    PackTar(items, temp, request.FormatId == "tar.gz", request.Compression, job, tracker, result);
                    break;
                default:
                    throw new NotSupportedException($"The archive format \"{request.FormatId}\" can't be created.");
            }
            ct.ThrowIfCancellationRequested();
            if (selfExtracting)
            {
                // the ZIP becomes the data of the .exe
                job.Report(new OperationProgress { Kind = OperationKind.Pack, Analyzing = true, CurrentSource = finalPath });
                byte[] stub = SelfExtractor.GetStub(DataDirectory ?? Path.Combine(Path.GetTempPath(), "VibeSalamander"));
                string name = Path.GetFileNameWithoutExtension(finalPath);
                SelfExtractor.Make(temp, finalPath, stub, new SfxOptions { Title = name, Target = name }, _ => { }, ct);
                TryDelete(temp);
            }
            else if (request.VolumeSize > 0 && new FileInfo(temp).Length > request.VolumeSize)
            {
                List<string> parts = Split(temp, archivePath, request.VolumeSize, ct);
                TryDelete(temp);
                finalPath = parts[0];
            }
            else
            {
                if (File.Exists(archivePath))
                    File.SetAttributes(archivePath, FileAttributes.Normal);
                File.Move(temp, archivePath, overwrite: true);
            }
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        tracker.ReportTo(job, force: true);
        result.CreatedTopLevelNames.Add(Path.GetFileName(finalPath));
        return result;
    }

    /// <summary>
    /// 7-Zip's "Split to volumes": "backup.zip.001", ".002", ... of <paramref name="size"/> bytes each (the last one
    /// smaller). Parts of an earlier split that are no longer needed are deleted. Returns the parts.
    /// </summary>
    internal static List<string> Split(string source, string archivePath, long size, CancellationToken ct)
    {
        var parts = new List<string>();
        byte[] buffer = new byte[1 << 20];
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
        {
            for (int index = 1; input.Position < input.Length; index++)
            {
                string part = $"{archivePath}.{index:000}";
                parts.Add(part);
                using var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                long left = Math.Min(size, input.Length - input.Position);
                while (left > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (read <= 0)
                        break;
                    output.Write(buffer, 0, read);
                    left -= read;
                }
            }
        }
        for (int index = parts.Count + 1; File.Exists($"{archivePath}.{index:000}"); index++)
            TryDelete($"{archivePath}.{index:000}");
        return parts;
    }

    /// <summary>
    /// 7-Zip's "Path mode": Full = the path without the drive ("Users/me/docs/a.txt"); None = file names only (no
    /// directories; the same name twice gets " (2)"). Relative leaves the names as they are.
    /// </summary>
    internal static List<PackItem> ApplyPathMode(List<PackItem> items, PackPathMode mode)
    {
        switch (mode)
        {
            case PackPathMode.Full:
                return items.Select(i =>
                {
                    string root = Path.GetPathRoot(i.FullPath) ?? string.Empty;
                    string name = i.FullPath[root.Length..].Trim('\').Replace('\', '/');
                    return i with { EntryName = name.Length > 0 ? name : i.EntryName };
                }).ToList();
            case PackPathMode.None:
                var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var files = new List<PackItem>();
                foreach (PackItem item in items.Where(i => !i.IsDirectory))
                {
                    string name = Path.GetFileName(item.FullPath);
                    if (!taken.Add(name))
                    {
                        name = UniqueEntryName(name, taken);
                        taken.Add(name);
                    }
                    files.Add(item with { EntryName = name });
                }
                return files;
            default:
                return items;
        }
    }

    /// <summary>Adds a file, or a directory with its content (not following junctions).</summary>
    internal static void Collect(string path, string baseDir, string archivePath, List<PackItem> items, IArchiveJob job, OperationResult result)
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

    private static void PackZip(List<PackItem> items, string temp, PackRequest request, IArchiveJob job,
        ProgressTracker tracker, OperationResult result)
    {
        string? password = string.IsNullOrEmpty(request.Password) ? null : request.Password;
        using var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        var zip = new ZipBuilder(file);
        foreach (PackItem item in items)
        {
            job.WaitWhilePaused();
            job.Cancellation.ThrowIfCancellationRequested();
            if (item.IsDirectory)
            {
                zip.AddDirectory(item.EntryName, item.LastWrite);
                continue;
            }
            tracker.Start(item.FullPath);
            tracker.ReportTo(job, force: true);
            using FileStream? source = OpenSource(item, job, result);
            if (source == null)
                continue;
            (DateTime, DateTime)? times = null;
            if (request.StoreNtfsTimes)
            {
                try
                {
                    times = (File.GetCreationTime(item.FullPath), File.GetLastAccessTime(item.FullPath));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // without them
                }
            }
            AddZipFile(zip, item.EntryName, source, item, request.Compression, password, job, tracker, request.Encryption, times);
            tracker.ItemDone();
            result.Succeeded++;
        }
        zip.Finish();
    }

    /// <summary>One file into a ZIP, with progress and pause / cancel checks.</summary>
    internal static void AddZipFile(ZipBuilder zip, string entryName, Stream source, PackItem item, ArchiveCompression compression,
        string? password, IArchiveJob job, ProgressTracker tracker, ZipEncryption encryption = ZipEncryption.Aes256,
        (DateTime Created, DateTime Accessed)? times = null) =>
        zip.AddFile(entryName, source, item.Size, item.LastWrite,
            compression == ArchiveCompression.Store ? null : Level(compression), password,
            progress: read =>
            {
                tracker.AddBytes(read);
                tracker.ReportTo(job);
            },
            checkpoint: () =>
            {
                job.WaitWhilePaused();
                job.Cancellation.ThrowIfCancellationRequested();
            },
            encryption: encryption,
            times: times);

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
    internal static FileStream? OpenSource(PackItem item, IArchiveJob job, OperationResult result)
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

    internal static CompressionLevel Level(ArchiveCompression compression) => compression switch
    {
        ArchiveCompression.Store => CompressionLevel.NoCompression,
        ArchiveCompression.Fastest => CompressionLevel.Fastest,
        ArchiveCompression.Maximum => CompressionLevel.SmallestSize,
        _ => CompressionLevel.Optimal,
    };

    /// <summary>TarEntry.ModificationTime rejects times before 1970.</summary>
    internal static DateTimeOffset TarTime(DateTime time)
    {
        var value = new DateTimeOffset(time);
        return value < DateTimeOffset.UnixEpoch ? DateTimeOffset.UnixEpoch : value;
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
        using Stream file = OpenRead(s.Request.ArchivePath);
        IArchive archive;
        try
        {
            archive = OpenRandomAccess(s.Request.ArchivePath, file, new ReaderOptions { Password = s.Password, LeaveStreamOpen = true });
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
            var files = entries.Where(e => !e.IsDirectory && s.Matches(e.Key)).ToList();
            // only the files to unpack count: one protected file doesn't need a password for the others
            s.Encrypted = files.Any(e => e.IsEncrypted);
            if (s.Encrypted && s.Password == null)
                throw new PasswordNeededException();
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
        using Stream file = OpenRead(s.Request.ArchivePath);
        s.Tracker = new ProgressTracker(OperationKind.Unpack, file.Length, 0);
        using Stream data = OpenTarData(s.Kind, file);
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
        string archiveName = Path.GetFileName(WithoutPartNumber(s.Request.ArchivePath));
        string name = archiveName[..^(kind.Length + 1)];
        if (name.Length == 0)
            name = "unpacked";
        if (!s.Matches(name))
            return;
        using Stream file = OpenRead(s.Request.ArchivePath);
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

    internal static long SafePosition(Stream stream)
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
