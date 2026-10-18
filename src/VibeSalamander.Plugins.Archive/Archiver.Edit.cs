// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Formats.Tar;
using System.IO.Compression;
using VibeSalamander.Core.Operations;

namespace VibeSalamander.Plugins.Archive;

/// <summary>
/// Changing archives in place (Salamander's "edit archive"): copying files into a ZIP or TAR and deleting
/// files from it. The archive is written anew next to the old one and replaces it only when everything
/// went well. ZIP entries that stay are copied as they are (not unpacked, still encrypted); TAR entries
/// are copied header and data. 7-Zip and RAR archives can't be written, they stay read-only.
/// </summary>
internal sealed partial class Archiver
{
    private static readonly HashSet<string> EditableKinds = new(StringComparer.OrdinalIgnoreCase) { "zip", "tar", "tar.gz", "tgz" };

    public bool CanEdit(string archivePath) => KindOf(archivePath) is string kind && EditableKinds.Contains(kind);

    public Task<OperationResult> AddAsync(ArchiveAddRequest request, IArchiveJob job) =>
        Task.Run(() => Edit(request.ArchivePath, job, request, null));

    public Task<OperationResult> DeleteAsync(ArchiveDeleteRequest request, IArchiveJob job) =>
        Task.Run(() => Edit(request.ArchivePath, job, null, request));

    /// <summary>What is in the archive now (normalized names).</summary>
    private sealed record Existing(string Key, bool IsDirectory, long Size, DateTime LastWrite);

    /// <summary>The plan: what goes, what is replaced and what comes in.</summary>
    private sealed class EditPlan
    {
        public required string ArchivePath { get; init; }
        public required IArchiveJob Job { get; init; }
        public List<string> Deleted { get; } = new();
        public HashSet<string> Replaced { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<PackItem> Added { get; } = new();
        public HashSet<string> ExistingDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The panel's directory: kept as an empty directory when everything in it goes.</summary>
        public string KeepDirectory { get; set; } = string.Empty;

        public ArchiveCompression Compression { get; set; } = ArchiveCompression.Normal;
        public string? Password { get; set; }

        public bool IsDeleted(string key)
        {
            foreach (string item in Deleted)
                if (key.Equals(item, StringComparison.OrdinalIgnoreCase) || key.StartsWith(item + "/", StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>A file (not a directory) that stays.</summary>
        public bool Keeps(string? key, bool isDirectory) =>
            key == null || (!IsDeleted(key) && (isDirectory || !Replaced.Contains(key)));
    }

    /// <summary>One change of an archive at a time (transfers run side by side).</summary>
    private static readonly Dictionary<string, object> EditLocks = new(StringComparer.OrdinalIgnoreCase);

    private OperationResult Edit(string archivePath, IArchiveJob job, ArchiveAddRequest? add, ArchiveDeleteRequest? delete)
    {
        archivePath = Path.GetFullPath(archivePath);
        object gate;
        lock (EditLocks)
        {
            if (!EditLocks.TryGetValue(archivePath, out gate!))
                EditLocks[archivePath] = gate = new object();
        }
        lock (gate)
            return EditLocked(archivePath, job, add, delete);
    }

    private OperationResult EditLocked(string archivePath, IArchiveJob job, ArchiveAddRequest? add, ArchiveDeleteRequest? delete)
    {
        string kind = KindOf(archivePath) ?? string.Empty;
        if (!EditableKinds.Contains(kind))
            throw new NotSupportedException($"\"{Path.GetFileName(archivePath)}\" can't be changed: only ZIP and TAR archives can.");
        if ((File.GetAttributes(archivePath) & FileAttributes.ReadOnly) != 0)
            throw new UnauthorizedAccessException($"\"{Path.GetFileName(archivePath)}\" is read-only.");
        bool zip = kind == "zip";
        var result = new OperationResult();
        var plan = new EditPlan { ArchivePath = archivePath, Job = job };
        job.Report(new OperationProgress { Kind = OperationKind.Pack, Analyzing = true, CurrentSource = archivePath });

        // 1. what is in the archive
        List<Existing> existing;
        List<ZipRecord>? records = null;
        byte[]? comment = null;
        if (zip)
        {
            using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            (records, comment) = Zip.ReadDirectory(input);
            existing = records.Select(r => UnpackState.Normalize(r.Name) is string key ? new Existing(key, r.IsDirectory, r.UncompressedSize, r.LastWrite) : null)
                              .Where(e => e != null).Select(e => e!).ToList();
        }
        else
        {
            existing = List(archivePath, job.Cancellation).Select(i => new Existing(i.Path, i.IsDirectory, i.Size, i.LastWrite)).ToList();
        }
        foreach (Existing e in existing.Where(e => e.IsDirectory))
            plan.ExistingDirectories.Add(e.Key);

        // 2. what goes
        if (delete != null)
        {
            plan.Deleted.AddRange(delete.Items.Select(UnpackState.Normalize).Where(k => k != null).Select(k => k!));
            plan.KeepDirectory = UnpackState.Normalize(delete.BasePath) ?? string.Empty;
        }

        // 3. what comes in, and what it replaces
        if (add != null)
        {
            plan.Compression = add.Compression;
            string target = UnpackState.Normalize(add.TargetPath) ?? string.Empty;
            var collected = new List<PackItem>();
            foreach (string source in add.Sources)
            {
                job.Cancellation.ThrowIfCancellationRequested();
                string full = Path.GetFullPath(source).TrimEnd('\\');
                Collect(full, Path.GetDirectoryName(full) ?? full, archivePath, collected, job, result);
            }
            if (!PlanAdditions(plan, collected, target, existing, result))
            {
                result.Cancelled = true;
                return result;
            }
            if (zip && plan.Added.Any(i => !i.IsDirectory) && records!.Any(r => r.IsEncrypted))
            {
                // new files in an encrypted ZIP get the archive's password
                plan.Password = AskEditPassword(archivePath, records!, job);
                if (plan.Password == null)
                {
                    result.Cancelled = true;
                    return result;
                }
            }
        }
        if (plan.Added.Count == 0 && plan.Deleted.Count == 0)
            return result;

        // 4. the new archive next to the old one; it replaces the old one when complete
        string temp = archivePath + ".vibe-partial";
        try
        {
            if (zip)
                WriteZip(plan, records!, comment!, result);
            else
                WriteTar(plan, kind, existing, result);
            job.Cancellation.ThrowIfCancellationRequested();
            File.Move(temp, archivePath, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        return result;
    }

    /// <summary>Names in the archive for the new items; asks about names that exist. False = cancelled.</summary>
    private static bool PlanAdditions(EditPlan plan, List<PackItem> collected, string target, List<Existing> existing, OperationResult result)
    {
        var files = new Dictionary<string, Existing>(StringComparer.OrdinalIgnoreCase);
        foreach (Existing e in existing.Where(e => !e.IsDirectory))
            files.TryAdd(e.Key, e);
        var taken = new HashSet<string>(existing.Select(e => e.Key), StringComparer.OrdinalIgnoreCase);
        ConflictChoice? all = null;
        // a renamed directory ("docs (2)") takes its content along
        var renamedDirectories = new List<(string From, string To)>();
        foreach (PackItem item in collected)
        {
            string name = target.Length > 0 ? target + "/" + item.EntryName : item.EntryName;
            foreach ((string from, string to) in renamedDirectories)
                if (name.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase))
                    name = to + name[from.Length..];
            if (item.IsDirectory)
            {
                if (files.ContainsKey(name))
                {
                    // a file of that name is in the way: the directory and its content get a free name
                    string free = UniqueEntryName(name, taken);
                    renamedDirectories.Add((name, free));
                    name = free;
                }
                taken.Add(name);
                plan.Added.Add(item with { EntryName = name });
                continue;
            }
            if (files.TryGetValue(name, out Existing? old) || plan.Added.Any(a => a.EntryName.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                ConflictChoice choice = all ?? Wait(plan.Job.ResolveConflictAsync(new ConflictInfo(
                    item.FullPath, item.Size, item.LastWrite,
                    plan.ArchivePath + "\\" + name.Replace('/', '\\'), old?.Size ?? 0, old?.LastWrite ?? DateTime.MinValue, IsDirectory: false)));
                switch (choice)
                {
                    case ConflictChoice.OverwriteAll:
                        all = ConflictChoice.Overwrite;
                        choice = ConflictChoice.Overwrite;
                        break;
                    case ConflictChoice.SkipAll:
                        all = ConflictChoice.Skip;
                        choice = ConflictChoice.Skip;
                        break;
                    case ConflictChoice.RenameAll:
                        all = ConflictChoice.Rename;
                        choice = ConflictChoice.Rename;
                        break;
                    case ConflictChoice.OverwriteAllOlder:
                        all = ConflictChoice.OverwriteAllOlder;
                        break;
                }
                if (choice == ConflictChoice.OverwriteAllOlder)
                    choice = old == null || old.LastWrite < item.LastWrite ? ConflictChoice.Overwrite : ConflictChoice.Skip;
                switch (choice)
                {
                    case ConflictChoice.Cancel:
                        return false;
                    case ConflictChoice.Skip:
                        result.Skipped++;
                        continue;
                    case ConflictChoice.Rename:
                        name = UniqueEntryName(name, taken);
                        break;
                    default:
                        plan.Replaced.Add(name);
                        plan.Added.RemoveAll(a => a.EntryName.Equals(name, StringComparison.OrdinalIgnoreCase));
                        break;
                }
            }
            taken.Add(name);
            plan.Added.Add(item with { EntryName = name });
        }
        return true;
    }

    /// <summary>"docs/a (2).txt": a name nothing in the archive has.</summary>
    internal static string UniqueEntryName(string name, ISet<string> taken)
    {
        int slash = name.LastIndexOf('/');
        string directory = slash < 0 ? string.Empty : name[..(slash + 1)];
        string file = name[(slash + 1)..];
        string stem = Path.GetFileNameWithoutExtension(file);
        string extension = Path.GetExtension(file);
        for (int i = 2; ; i++)
        {
            string candidate = $"{directory}{stem} ({i}){extension}";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    /// <summary>The password for new files in an encrypted ZIP, checked against an AES entry when there is one. Null = cancelled.</summary>
    private static string? AskEditPassword(string archivePath, List<ZipRecord> records, IArchiveJob job)
    {
        ZipRecord? aes = records.FirstOrDefault(r => r.IsEncrypted && r.Method == Zip.AesMethod);
        bool wrong = false;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            string? password = Wait(job.AskPasswordAsync(archivePath, wrong));
            if (password == null)
                return null;
            if (aes == null)
                return password; // older encryption: nothing to check it against
            using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            if (Zip.CheckPassword(input, aes, password) != false)
                return password;
            wrong = true;
        }
        throw new InvalidOperationException("The password is wrong.");
    }

    /// <summary>The changed ZIP: the entries that stay, copied as they are, then the new ones.</summary>
    private static void WriteZip(EditPlan plan, List<ZipRecord> records, byte[] comment, OperationResult result)
    {
        IArchiveJob job = plan.Job;
        var kept = records.Where(r => plan.Keeps(UnpackState.Normalize(r.Name), r.IsDirectory)).ToList();
        long total = kept.Sum(r => r.CompressedSize) + plan.Added.Where(i => !i.IsDirectory).Sum(i => i.Size);
        var tracker = new ProgressTracker(OperationKind.Pack, total, plan.Added.Count(i => !i.IsDirectory));
        result.Succeeded += records.Count(r => !r.IsDirectory && UnpackState.Normalize(r.Name) is string key && plan.IsDeleted(key));

        using var input = new FileStream(plan.ArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var output = new FileStream(plan.ArchivePath + ".vibe-partial", FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        var zip = new ZipBuilder(output);
        tracker.Start(plan.ArchivePath);
        foreach (ZipRecord record in kept)
        {
            job.WaitWhilePaused();
            job.Cancellation.ThrowIfCancellationRequested();
            zip.CopyRaw(input, record, read =>
            {
                tracker.AddBytes(read);
                tracker.ReportTo(job);
            });
        }
        if (KeepsDirectory(plan, kept.Select(r => UnpackState.Normalize(r.Name))))
            zip.AddDirectory(plan.KeepDirectory, DateTime.Now);
        foreach (PackItem item in plan.Added)
        {
            job.WaitWhilePaused();
            job.Cancellation.ThrowIfCancellationRequested();
            if (item.IsDirectory)
            {
                if (!plan.ExistingDirectories.Contains(item.EntryName))
                    zip.AddDirectory(item.EntryName, item.LastWrite);
                continue;
            }
            tracker.Start(item.FullPath);
            tracker.ReportTo(job, force: true);
            using FileStream? source = OpenSource(item, job, result);
            if (source == null)
                continue;
            AddZipFile(zip, item.EntryName, source, item, plan.Compression, plan.Password, job, tracker);
            tracker.ItemDone();
            result.Succeeded++;
        }
        zip.Finish(comment);
        tracker.ReportTo(job, force: true);
    }

    /// <summary>True when the panel's directory would vanish (nothing left in it and no entry of its own).</summary>
    private static bool KeepsDirectory(EditPlan plan, IEnumerable<string?> keptKeys)
    {
        string directory = plan.KeepDirectory;
        if (directory.Length == 0)
            return false;
        foreach (string? key in keptKeys)
            if (key != null && (key.Equals(directory, StringComparison.OrdinalIgnoreCase) || key.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase)))
                return false;
        return !plan.Added.Any(a => a.EntryName.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The changed TAR (.tar, .tar.gz): every entry that stays is written again, then the new ones. A
    /// compressed TAR is unpacked into a temporary .tar first: the TAR writer copies entries from a
    /// seekable archive only when it writes into a compressed (unseekable) one.
    /// </summary>
    private static void WriteTar(EditPlan plan, string kind, List<Existing> existing, OperationResult result)
    {
        IArchiveJob job = plan.Job;
        bool gzip = kind is "tar.gz" or "tgz";
        result.Succeeded += existing.Count(e => !e.IsDirectory && plan.IsDeleted(e.Key));
        long addedBytes = plan.Added.Where(i => !i.IsDirectory).Sum(i => i.Size);
        string? plainTar = gzip ? plan.ArchivePath + ".vibe-tar" : null;
        try
        {
            if (plainTar != null)
            {
                job.Report(new OperationProgress { Kind = OperationKind.Pack, Analyzing = true, CurrentSource = plan.ArchivePath });
                using var compressed = new FileStream(plan.ArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                using var gunzip = new GZipStream(compressed, CompressionMode.Decompress);
                using var plain = new FileStream(plainTar, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                byte[] buffer = new byte[1 << 20];
                int read;
                while ((read = gunzip.Read(buffer, 0, buffer.Length)) > 0)
                {
                    job.WaitWhilePaused();
                    job.Cancellation.ThrowIfCancellationRequested();
                    plain.Write(buffer, 0, read);
                }
            }

            using var input = new FileStream(plainTar ?? plan.ArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            var tracker = new ProgressTracker(OperationKind.Pack, input.Length + addedBytes, plan.Added.Count(i => !i.IsDirectory));
            using var output = new FileStream(plan.ArchivePath + ".vibe-partial", FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            Stream packed = gzip
                ? new GZipStream(output, Level(plan.Compression == ArchiveCompression.Store ? ArchiveCompression.Fastest : plan.Compression), leaveOpen: true)
                : output;
            var keptKeys = new List<string?>();
            try
            {
                using var reader = new TarReader(input, leaveOpen: true);
                using var writer = new TarWriter(packed, TarEntryFormat.Pax, leaveOpen: true);
                tracker.Start(plan.ArchivePath);
                while (reader.GetNextEntry(copyData: false) is TarEntry entry)
                {
                    job.WaitWhilePaused();
                    job.Cancellation.ThrowIfCancellationRequested();
                    string? key = UnpackState.Normalize(entry.Name);
                    if (!plan.Keeps(key, entry.EntryType == TarEntryType.Directory))
                        continue;
                    keptKeys.Add(key);
                    writer.WriteEntry(entry); // header and data as they are, in the entry's own format
                    tracker.SetBytes(SafePosition(input));
                    tracker.ReportTo(job);
                }
                tracker.SetBytes(input.Length);

                if (KeepsDirectory(plan, keptKeys))
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, plan.KeepDirectory + "/") { ModificationTime = DateTimeOffset.Now });
                foreach (PackItem item in plan.Added)
                {
                    job.WaitWhilePaused();
                    job.Cancellation.ThrowIfCancellationRequested();
                    if (item.IsDirectory)
                    {
                        if (!plan.ExistingDirectories.Contains(item.EntryName))
                            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, item.EntryName + "/") { ModificationTime = TarTime(item.LastWrite) });
                        continue;
                    }
                    tracker.Start(item.FullPath);
                    tracker.ReportTo(job, force: true);
                    using FileStream? source = OpenSource(item, job, result);
                    if (source == null)
                        continue;
                    using var progress = new ProgressStream(source, job, tracker);
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, item.EntryName)
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
                    packed.Dispose();
            }
            tracker.ReportTo(job, force: true);
        }
        finally
        {
            if (plainTar != null)
                TryDelete(plainTar);
        }
    }
}
