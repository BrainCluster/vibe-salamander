// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Operations;

namespace VibeSalamander.Plugins.SevenZip;

/// <summary>
/// Archives through 7z.exe: packs 7z with 7-Zip's own settings (method, dictionary, word size, solid blocks,
/// threads, encrypted names, SFX, volumes, update modes) and unpacks, browses, tests and changes 7z, plus the
/// formats only 7-Zip reads. Progress comes from 7-Zip's percentage; pausing suspends 7-Zip.
/// </summary>
internal sealed class SevenZipArchiver : IArchiver
{
    public string DisplayName => "7-Zip";

    /// <summary>7z and what the Archives plugin can't read (disk images like ISO and VHD stay with Mount).</summary>
    private static readonly string[] Extensions =
    {
        "7z", "cab", "wim", "swm", "esd", "msi", "msp", "chm", "arj", "lzh", "lha", "cpio", "rpm", "deb", "xar", "pkg",
        "dmg", "squashfs", "lzma", "zst", "z",
    };

    private static readonly string[] Levels = { "Store", "Fastest", "Normal", "Maximum" };

    public static bool Available => SevenZipTool.Exe != null;

    public IReadOnlyList<ArchiveFormat> PackFormats { get; } = Available
        ? new[]
        {
            new ArchiveFormat("7z", "7z (7-Zip)", ".7z", HasCompressionLevels: true, SupportsPassword: true, Options: new[]
            {
                new PackOption("method", "Compression method", PluginFieldKind.Choice, new[] { "LZMA2", "LZMA", "PPMd", "BZip2" }, "LZMA2"),
                new PackOption("dictionary", "Dictionary size", PluginFieldKind.Choice,
                    new[] { "Auto", "64 KB", "256 KB", "1 MB", "4 MB", "16 MB", "32 MB", "64 MB", "128 MB", "256 MB", "512 MB", "1024 MB", "1536 MB" }, "Auto"),
                new PackOption("word", "Word size", PluginFieldKind.Choice,
                    new[] { "Auto", "8", "16", "24", "32", "48", "64", "96", "128", "192", "256", "273" }, "Auto"),
                new PackOption("solid", "Solid block size", PluginFieldKind.Choice,
                    new[] { "Auto", "Non-solid", "1 MB", "16 MB", "128 MB", "1 GB", "4 GB", "16 GB", "64 GB", "Solid" }, "Auto"),
                new PackOption("threads", "CPU threads", PluginFieldKind.Choice,
                    new[] { "Auto" }.Concat(Enumerable.Range(1, Math.Max(1, Environment.ProcessorCount)).Select(n => n.ToString(CultureInfo.InvariantCulture))).ToArray(), "Auto"),
                new PackOption("encryptNames", "Encrypt file names", PluginFieldKind.Check, Array.Empty<string>(), "false", NeedsPassword: true),
                new PackOption("sfx", "Create a self-extracting archive (.exe)", PluginFieldKind.Check, Array.Empty<string>(), "false"),
                new PackOption("times", "Store creation and last access times", PluginFieldKind.Check, Array.Empty<string>(), "false"),
            }),
        }
        : Array.Empty<ArchiveFormat>();

    public IReadOnlyList<string> UnpackExtensions => Available ? Extensions : Array.Empty<string>();

    /// <summary>"backup.7z.001" → "backup.7z" (7-Zip reads the parts itself).</summary>
    internal static string WithoutPartNumber(string path) =>
        path.EndsWith(".001", StringComparison.Ordinal) && path.Length > 4 ? path[..^4] : path;

    internal static string? KindOf(string path)
    {
        string name = Path.GetFileName(WithoutPartNumber(path));
        foreach (string extension in Extensions)
            if (name.Length > extension.Length + 1 && name.EndsWith("." + extension, StringComparison.OrdinalIgnoreCase))
                return extension;
        return null;
    }

    public bool CanUnpack(string archivePath) => Available && KindOf(archivePath) != null;

    public bool CanBrowse(string archivePath) => CanUnpack(archivePath);

    public bool CanEdit(string archivePath) =>
        Available && WithoutPartNumber(archivePath) == archivePath && KindOf(archivePath) == "7z";

    // ---- listing ---------------------------------------------------------------------------------

    public Task<IReadOnlyList<ArchiveItem>> ListAsync(string archivePath, CancellationToken cancellationToken) => Task.Run(() =>
    {
        DateTime archiveTime = File.GetLastWriteTime(archivePath);
        List<SevenZipItem> items;
        try
        {
            items = SevenZipTool.List(archivePath, null, cancellationToken).Items;
        }
        catch (PasswordNeededException)
        {
            throw new InvalidDataException("The names in this archive are encrypted: unpack it (Alt+F6) to type its password.");
        }
        return (IReadOnlyList<ArchiveItem>)items.Select(i => new ArchiveItem(i.Path, i.IsDirectory, i.Size, i.Modified ?? archiveTime)).ToList();
    }, cancellationToken);

    /// <summary>The listing; asks for the password when the names are encrypted. Null = cancelled.</summary>
    private static (Dictionary<string, string> Archive, List<SevenZipItem> Items, string? Password)? ListWithPassword(string archivePath, IArchiveJob job)
    {
        string? password = null;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var (archive, items) = SevenZipTool.List(archivePath, password, job.Cancellation);
                return (archive, items, password);
            }
            catch (PasswordNeededException)
            {
                password = Wait(job.AskPasswordAsync(archivePath, wrongPassword: attempt > 0));
                if (password == null)
                    return null;
            }
        }
    }

    private static T Wait<T>(Task<T> task) => task.GetAwaiter().GetResult();

    // ---- packing ---------------------------------------------------------------------------------

    private static string Option(IReadOnlyDictionary<string, string> options, string id, string fallback) =>
        options.TryGetValue(id, out string? value) && !string.IsNullOrEmpty(value) ? value : fallback;

    /// <summary>"16 MB" → "16m", "64 KB" → "64k", "1 GB" → "1g".</summary>
    internal static string SizeSwitch(string text)
    {
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return text;
        string unit = parts[1].ToUpperInvariant() switch { "KB" => "k", "MB" => "m", "GB" => "g", _ => "b" };
        return parts[0] + unit;
    }

    /// <summary>The 7-Zip switches of the compression settings.</summary>
    internal static List<string> CompressionSwitches(ArchiveCompression compression, IReadOnlyDictionary<string, string> options, string? password)
    {
        var args = new List<string>();
        int level = compression switch { ArchiveCompression.Store => 0, ArchiveCompression.Fastest => 1, ArchiveCompression.Maximum => 9, _ => 5 };
        args.Add("-mx=" + level.ToString(CultureInfo.InvariantCulture));
        if (level > 0)
        {
            string method = Option(options, "method", "LZMA2");
            args.Add("-m0=" + method);
            string dictionary = Option(options, "dictionary", "Auto");
            if (dictionary != "Auto" && method != "BZip2")
                args.Add((method == "PPMd" ? "-mmem=" : "-md=") + SizeSwitch(dictionary));
            string word = Option(options, "word", "Auto");
            if (word != "Auto" && int.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out int w))
            {
                if (method == "PPMd")
                    args.Add("-mo=" + Math.Clamp(w, 2, 32).ToString(CultureInfo.InvariantCulture));
                else if (method is "LZMA" or "LZMA2")
                    args.Add("-mfb=" + Math.Clamp(w, 5, 273).ToString(CultureInfo.InvariantCulture));
            }
        }
        string solid = Option(options, "solid", "Auto");
        if (solid == "Non-solid")
            args.Add("-ms=off");
        else if (solid == "Solid")
            args.Add("-ms=on");
        else if (solid != "Auto")
            args.Add("-ms=" + SizeSwitch(solid));
        string threads = Option(options, "threads", "Auto");
        if (threads != "Auto")
            args.Add("-mmt=" + threads);
        if (Option(options, "times", "false") == "true")
        {
            args.Add("-mtc=on");
            args.Add("-mta=on");
        }
        if (!string.IsNullOrEmpty(password))
        {
            args.Add("-p" + password);
            if (Option(options, "encryptNames", "false") == "true")
                args.Add("-mhe=on");
        }
        return args;
    }

    /// <summary>7-Zip's -u switch of an update mode.</summary>
    internal static string UpdateSwitch(PackUpdateMode mode) => mode switch
    {
        PackUpdateMode.UpdateAndAdd => "-up1q1r2x1y2z1w2",
        PackUpdateMode.Freshen => "-up1q1r0x1y2z1w2",
        PackUpdateMode.Synchronize => "-up1q0r2x1y2z1w2",
        _ => "-up1q1r2x2y2z2w2", // add and replace
    };

    /// <summary>The sources as 7-Zip should store them: relative to the base directory where possible.</summary>
    private static List<string> SourceNames(IEnumerable<string> sources, string baseDirectory)
    {
        var names = new List<string>();
        foreach (string source in sources)
        {
            string full = Path.GetFullPath(source).TrimEnd('\\');
            string relative = Path.GetRelativePath(baseDirectory, full);
            names.Add(relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? full : relative);
        }
        return names;
    }

    private static (long Bytes, int Files) Measure(IEnumerable<string> sources)
    {
        long bytes = 0;
        int files = 0;
        foreach (string source in sources)
        {
            try
            {
                if (File.Exists(source))
                {
                    bytes += new FileInfo(source).Length;
                    files++;
                }
                else if (Directory.Exists(source))
                {
                    foreach (FileInfo file in new DirectoryInfo(source).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
                    {
                        bytes += file.Length;
                        files++;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // measured as far as possible
            }
        }
        return (bytes, files);
    }

    /// <summary>Runs 7-Zip for a transfer: progress, pause, and an error question when it fails (retry / skip / cancel).</summary>
    private static bool RunJob(List<string> args, string? workingDirectory, IArchiveJob job, ProgressTracker tracker, string subject, OperationResult result)
    {
        while (true)
        {
            SevenZipResult run = SevenZipTool.Run(args, workingDirectory, percent =>
            {
                tracker.SetBytes(tracker.TotalBytes * percent / 100);
                tracker.ReportTo(job);
            }, job);
            if (run.Ok)
            {
                tracker.SetBytes(tracker.TotalBytes);
                tracker.ReportTo(job, force: true);
                return true;
            }
            switch (Wait(job.ResolveErrorAsync(new OperationError(subject, run.Problem))))
            {
                case ErrorChoice.Retry:
                    continue;
                case ErrorChoice.Cancel:
                    result.Cancelled = true;
                    return false;
                default:
                    result.Failed++;
                    return false;
            }
        }
    }

    public Task<OperationResult> PackAsync(PackRequest request, IArchiveJob job) => Task.Run(() => Pack(request, job));

    private static OperationResult Pack(PackRequest request, IArchiveJob job)
    {
        var result = new OperationResult();
        string archivePath = Path.GetFullPath(request.ArchivePath);
        bool sfx = Option(request.Options, "sfx", "false") == "true";
        string? sfxModule = SevenZipTool.Folder is { } folder ? Path.Combine(folder, "7z.sfx") : null;
        if (sfx && (sfxModule == null || !File.Exists(sfxModule)))
            throw new InvalidOperationException("7-Zip's self-extractor (7z.sfx) wasn't found next to 7z.exe.");
        string finalPath = sfx ? Path.ChangeExtension(archivePath, ".exe") : archivePath;
        string baseDirectory = Path.GetFullPath(request.BaseDirectory);
        var (bytes, files) = Measure(request.Sources);
        var tracker = new ProgressTracker(OperationKind.Pack, bytes, files);
        tracker.Start(request.Sources.Count == 1 ? request.Sources[0] : baseDirectory);

        var args = new List<string>();
        bool update = request.UpdateMode != PackUpdateMode.Replace && !sfx && request.VolumeSize == 0 && File.Exists(finalPath);
        string target = finalPath;
        if (update)
        {
            // into the existing archive (7-Zip writes a new one and replaces the old one itself)
            args.Add("u");
            args.Add(UpdateSwitch(request.UpdateMode));
        }
        else
        {
            bool exists = File.Exists(finalPath) || (request.VolumeSize > 0 && File.Exists(finalPath + ".001"));
            if (exists)
            {
                string existingPath = File.Exists(finalPath) ? finalPath : finalPath + ".001";
                var existing = new FileInfo(existingPath);
                ConflictChoice choice = Wait(job.ResolveConflictAsync(new ConflictInfo(
                    request.Sources.Count == 1 ? request.Sources[0] : baseDirectory, bytes, DateTime.Now,
                    existingPath, existing.Length, existing.LastWriteTime, IsDirectory: false, TargetAttributes: existing.Attributes)));
                switch (choice)
                {
                    case ConflictChoice.Skip or ConflictChoice.SkipAll:
                        result.Skipped++;
                        return result;
                    case ConflictChoice.Cancel:
                        result.Cancelled = true;
                        return result;
                    case ConflictChoice.Rename or ConflictChoice.RenameAll:
                        finalPath = FileOperationEngine.UniqueName(finalPath);
                        break;
                }
            }
            // a new archive under a temporary name; it replaces the old one when complete
            target = Path.Combine(Path.GetDirectoryName(finalPath)!, Path.GetFileNameWithoutExtension(finalPath) + ".vibe-partial" + Path.GetExtension(finalPath));
            args.Add("a");
            args.Add("-t7z");
            if (sfx)
                args.Add("-sfx" + sfxModule);
            if (request.VolumeSize > 0)
                args.Add("-v" + request.VolumeSize.ToString(CultureInfo.InvariantCulture) + "b");
        }
        args.AddRange(CompressionSwitches(request.Compression, request.Options, request.Password));
        if (request.PathMode == PackPathMode.Full)
            args.Add("-spf2");
        args.AddRange(new[] { "-ssw", "-y", "-bsp1", "-bso0", "-bse2", "--", target });
        args.AddRange(SourceNames(request.Sources, baseDirectory));

        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        try
        {
            if (!RunJob(args, baseDirectory, job, tracker, finalPath, result))
            {
                CleanUp(target, update);
                return result;
            }
        }
        catch
        {
            CleanUp(target, update);
            throw;
        }
        if (!update)
        {
            if (request.VolumeSize > 0 && !sfx)
            {
                // name.vibe-partial.7z.001 ... → name.7z.001 ...; parts of an earlier, longer split go
                int parts = 0;
                for (int i = 1; File.Exists($"{target}.{i:000}"); i++, parts++)
                    File.Move($"{target}.{i:000}", $"{finalPath}.{i:000}", overwrite: true);
                for (int i = parts + 1; parts > 0 && File.Exists($"{finalPath}.{i:000}"); i++)
                    TryDelete($"{finalPath}.{i:000}");
                if (File.Exists(finalPath + ".001"))
                    finalPath += ".001";
                else if (File.Exists(target))
                    File.Move(target, finalPath, overwrite: true); // smaller than one volume: 7-Zip wrote one file
            }
            else
            {
                if (File.Exists(finalPath))
                    File.SetAttributes(finalPath, FileAttributes.Normal);
                File.Move(target, finalPath, overwrite: true);
            }
        }
        result.Succeeded += files;
        result.CreatedTopLevelNames.Add(Path.GetFileName(finalPath));
        return result;
    }

    private static void CleanUp(string target, bool update)
    {
        if (update)
            return;
        TryDelete(target);
        for (int i = 1; File.Exists($"{target}.{i:000}"); i++)
            TryDelete($"{target}.{i:000}");
    }

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
            // left behind
        }
    }

    // ---- unpacking -------------------------------------------------------------------------------

    private static string? Normalize(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return null;
        string joined = string.Join('/', key.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != "."));
        return joined.Length == 0 ? null : joined;
    }

    public Task<OperationResult> UnpackAsync(UnpackRequest request, IArchiveJob job) => Task.Run(() => Unpack(request, job));

    private static OperationResult Unpack(UnpackRequest request, IArchiveJob job)
    {
        var result = new OperationResult();
        string archivePath = request.ArchivePath;
        string target = Path.GetFullPath(request.TargetDirectory);
        job.Report(new OperationProgress { Kind = OperationKind.Unpack, Analyzing = true, CurrentSource = archivePath });
        if (ListWithPassword(archivePath, job) is not { } listing)
        {
            result.Cancelled = true;
            return result;
        }
        string? password = listing.Password;
        string basePath = Normalize(request.BasePath) ?? string.Empty;
        List<string>? selected = request.Items?.Select(Normalize).Where(i => i != null).Select(i => i!).ToList();
        var mask = new WildcardMask(string.IsNullOrWhiteSpace(request.Mask) ? "*.*" : request.Mask);
        bool Wanted(string path)
        {
            if (basePath.Length > 0 && !path.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase))
                return false;
            if (selected != null && !selected.Any(s => path.Equals(s, StringComparison.OrdinalIgnoreCase) || path.StartsWith(s + "/", StringComparison.OrdinalIgnoreCase)))
                return false;
            return mask.IsMatch(Path.GetFileName(path));
        }
        var files = listing.Items.Where(i => !i.IsDirectory && Wanted(i.Path)).ToList();
        if (files.Count == 0)
            return result;

        // encrypted data: the password first
        if (password == null && files.Any(f => f.Encrypted))
        {
            password = Wait(job.AskPasswordAsync(archivePath, wrongPassword: false));
            if (password == null)
            {
                result.Cancelled = true;
                return result;
            }
        }

        // files that exist: one answer for all of them (7-Zip overwrites, skips or renames them all)
        string Relative(string path) => basePath.Length > 0 ? path[(basePath.Length + 1)..] : path;
        SevenZipItem? clash = files.FirstOrDefault(f => File.Exists(Path.Combine(target, Relative(f.Path).Replace('/', '\\'))));
        string overwrite = "-aoa";
        int skipped = 0;
        if (clash != null)
        {
            string existingPath = Path.Combine(target, Relative(clash.Path).Replace('/', '\\'));
            var existing = new FileInfo(existingPath);
            ConflictChoice choice = Wait(job.ResolveConflictAsync(new ConflictInfo(archivePath + "\\" + clash.Path.Replace('/', '\\'), clash.Size,
                clash.Modified ?? DateTime.MinValue, existingPath, existing.Length, existing.LastWriteTime, IsDirectory: false, TargetAttributes: existing.Attributes)));
            switch (choice)
            {
                case ConflictChoice.Cancel:
                    result.Cancelled = true;
                    return result;
                case ConflictChoice.Skip or ConflictChoice.SkipAll:
                    overwrite = "-aos";
                    skipped = files.Count(f => File.Exists(Path.Combine(target, Relative(f.Path).Replace('/', '\\'))));
                    break;
                case ConflictChoice.Rename or ConflictChoice.RenameAll:
                    overwrite = "-aou";
                    break;
            }
        }

        // a base directory can't be left out by 7-Zip: then into a temporary folder first, and moved from there
        Directory.CreateDirectory(target);
        string extractTo = basePath.Length > 0 ? Path.Combine(target, "~vibe-7z-" + Guid.NewGuid().ToString("N")[..8]) : target;
        var tracker = new ProgressTracker(OperationKind.Unpack, files.Sum(f => f.Size), files.Count);
        tracker.Start(archivePath);
        while (true)
        {
            var args = new List<string> { "x", SevenZipTool.PasswordSwitch(password), "-o" + extractTo, overwrite, "-y", "-bsp1", "-bso0", "-bse2" };
            bool everything = selected == null && basePath.Length == 0 && mask.IsMatch("any.name") && request.Mask is "*.*" or "*" or "" or null;
            args.Add("--");
            args.Add(archivePath);
            if (!everything)
            {
                // exactly the wanted files (an archive with many files: their directories instead)
                if (files.Count <= 2000)
                    args.AddRange(files.Select(f => f.Path.Replace('/', '\\')));
                else if (selected != null)
                    args.AddRange(selected.Select(s => s.Replace('/', '\\')));
                else if (basePath.Length > 0)
                    args.Add(basePath.Replace('/', '\\'));
            }
            SevenZipResult run = SevenZipTool.Run(args, null, percent =>
            {
                tracker.SetBytes(tracker.TotalBytes * percent / 100);
                tracker.ReportTo(job);
            }, job);
            if (run.Ok)
                break;
            if (run.WrongPassword)
            {
                password = Wait(job.AskPasswordAsync(archivePath, wrongPassword: true));
                if (password == null)
                {
                    result.Cancelled = true;
                    DeleteFolder(extractTo, target);
                    return result;
                }
                continue;
            }
            ErrorChoice error = Wait(job.ResolveErrorAsync(new OperationError(archivePath, run.Problem)));
            if (error == ErrorChoice.Retry)
                continue;
            result.Failed++;
            if (error == ErrorChoice.Cancel)
                result.Cancelled = true;
            break;
        }
        if (extractTo != target)
        {
            MoveTree(Path.Combine(extractTo, basePath.Replace('/', '\\')), target, overwrite);
            DeleteFolder(extractTo, target);
        }
        tracker.SetBytes(tracker.TotalBytes);
        tracker.ReportTo(job, force: true);
        if (result.Failed == 0 && !result.Cancelled)
        {
            result.Succeeded = files.Count - skipped;
            result.Skipped += skipped;
            foreach (string top in files.Select(f => Relative(f.Path).Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase))
                result.CreatedTopLevelNames.Add(top);
        }
        return result;
    }

    /// <summary>Moves what was extracted into a temporary folder to its place (with 7-Zip's overwrite / skip / rename answer).</summary>
    private static void MoveTree(string from, string to, string overwrite)
    {
        if (!Directory.Exists(from))
            return;
        Directory.CreateDirectory(to);
        foreach (string directory in Directory.GetDirectories(from))
            MoveTree(directory, Path.Combine(to, Path.GetFileName(directory)), overwrite);
        foreach (string file in Directory.GetFiles(from))
        {
            string destination = Path.Combine(to, Path.GetFileName(file));
            if (File.Exists(destination))
            {
                if (overwrite == "-aos")
                    continue;
                if (overwrite == "-aou")
                    destination = FileOperationEngine.UniqueName(destination);
                else
                    File.SetAttributes(destination, FileAttributes.Normal);
            }
            File.Move(file, destination, overwrite: true);
        }
    }

    private static void DeleteFolder(string folder, string target)
    {
        if (folder == target)
            return;
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // left behind
        }
    }

    // ---- changing 7z archives in place ---------------------------------------------------------------

    public Task<OperationResult> AddAsync(ArchiveAddRequest request, IArchiveJob job) => Task.Run(() => Add(request, job));

    private static OperationResult Add(ArchiveAddRequest request, IArchiveJob job)
    {
        var result = new OperationResult();
        string archivePath = request.ArchivePath;
        if (ListWithPassword(archivePath, job) is not { } listing)
        {
            result.Cancelled = true;
            return result;
        }
        string? password = listing.Password;
        if (password == null && listing.Items.Any(i => i.Encrypted))
        {
            // new files get the archive's password
            password = Wait(job.AskPasswordAsync(archivePath, wrongPassword: false));
            if (password == null)
            {
                result.Cancelled = true;
                return result;
            }
        }
        string targetPath = Normalize(request.TargetPath) ?? string.Empty;
        var existing = new HashSet<string>(listing.Items.Where(i => !i.IsDirectory).Select(i => i.Path), StringComparer.OrdinalIgnoreCase);

        // files of the same name: one answer for all (overwrite = 7-Zip's "add", skip = keep the archive's)
        string command = "a";
        string? updateSwitch = request.UpdateMode == PackUpdateMode.Replace ? null : UpdateSwitch(request.UpdateMode);
        if (updateSwitch == null)
        {
            foreach (string source in request.Sources)
            {
                string name = Path.GetFileName(source.TrimEnd('\\'));
                string inArchive = targetPath.Length > 0 ? targetPath + "/" + name : name;
                bool clash = File.Exists(source)
                    ? existing.Contains(inArchive)
                    : existing.Any(e => e.StartsWith(inArchive + "/", StringComparison.OrdinalIgnoreCase));
                if (!clash)
                    continue;
                var info = new FileInfo(source);
                ConflictChoice choice = Wait(job.ResolveConflictAsync(new ConflictInfo(source, File.Exists(source) ? info.Length : 0,
                    info.LastWriteTime, archivePath + "\\" + inArchive.Replace('/', '\\'), 0, DateTime.MinValue, IsDirectory: !File.Exists(source))));
                if (choice == ConflictChoice.Cancel)
                {
                    result.Cancelled = true;
                    return result;
                }
                if (choice is ConflictChoice.Skip or ConflictChoice.SkipAll or ConflictChoice.Rename or ConflictChoice.RenameAll)
                    updateSwitch = "-up1q1r2x1y1z1w1"; // new names only
                break;
            }
        }
        if (updateSwitch != null)
            command = "u";

        // into a directory of the archive: 7-Zip stores paths as given, so the files are staged under that directory
        string? staging = null;
        string workingDirectory;
        List<string> names;
        if (targetPath.Length > 0)
        {
            staging = Path.Combine(Path.GetTempPath(), "VibeSalamander-7z-" + Guid.NewGuid().ToString("N")[..8]);
            string inside = Path.Combine(staging, targetPath.Replace('/', '\\'));
            Directory.CreateDirectory(inside);
            foreach (string source in request.Sources)
                CopyTree(source, Path.Combine(inside, Path.GetFileName(source.TrimEnd('\\'))));
            workingDirectory = staging;
            names = new List<string> { targetPath.Split('/')[0] };
        }
        else
        {
            workingDirectory = Path.GetDirectoryName(Path.GetFullPath(request.Sources[0]).TrimEnd('\\')) ?? Environment.CurrentDirectory;
            names = SourceNames(request.Sources, workingDirectory);
        }
        try
        {
            var (bytes, files) = Measure(request.Sources);
            var tracker = new ProgressTracker(OperationKind.Pack, bytes, files);
            tracker.Start(archivePath);
            var args = new List<string> { command };
            if (updateSwitch != null)
                args.Add(updateSwitch);
            // (no password switch without a password: for "a" and "u" it would encrypt the new files)
            args.AddRange(CompressionSwitches(request.Compression, new Dictionary<string, string>(), password));
            if (request.PathMode == PackPathMode.Full)
                args.Add("-spf2");
            args.AddRange(new[] { "-ssw", "-y", "-bsp1", "-bso0", "-bse2", "--", archivePath });
            args.AddRange(names);
            if (RunJob(args, workingDirectory, job, tracker, archivePath, result))
                result.Succeeded += files;
        }
        finally
        {
            if (staging != null)
            {
                try
                {
                    Directory.Delete(staging, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // a temporary folder left behind
                }
            }
        }
        return result;
    }

    private static void CopyTree(string source, string destination)
    {
        if (File.Exists(source))
        {
            File.Copy(source, destination, overwrite: true);
            return;
        }
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.GetDirectories(source))
            CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
        foreach (string file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
    }

    public Task<OperationResult> DeleteAsync(ArchiveDeleteRequest request, IArchiveJob job) => Task.Run(() =>
    {
        var result = new OperationResult();
        if (ListWithPassword(request.ArchivePath, job) is not { } listing)
        {
            result.Cancelled = true;
            return result;
        }
        var items = request.Items.Select(Normalize).Where(i => i != null).Select(i => i!.Replace('/', '\\')).ToList();
        if (items.Count == 0)
            return result;
        var tracker = new ProgressTracker(OperationKind.Delete, 0, items.Count);
        var args = new List<string> { "d", "-y", "-bsp1", "-bso0", "-bse2" };
        if (listing.Password != null)
            args.Add("-p" + listing.Password); // encrypted names: needed to read the archive
        args.AddRange(new[] { "--", request.ArchivePath });
        args.AddRange(items);
        if (RunJob(args, null, job, tracker, request.ArchivePath, result))
            result.Succeeded += items.Count;
        return result;
    });
}
