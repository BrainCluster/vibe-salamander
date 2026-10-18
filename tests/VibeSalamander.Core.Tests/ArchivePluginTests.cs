// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using VibeSalamander.Core.Operations;
using VibeSalamander.Plugins;
using VibeSalamander.Plugins.Archive;

namespace VibeSalamander.Core.Tests;

public sealed class ArchivePluginTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vibe-archive-tests-" + Guid.NewGuid().ToString("N"));

    public ArchivePluginTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "docs", "deep"));
        File.WriteAllText(Path.Combine(_root, "src", "readme.txt"), "hello");
        File.WriteAllText(Path.Combine(_root, "src", "docs", "a.md"), "# a");
        File.WriteAllBytes(Path.Combine(_root, "src", "docs", "deep", "data.bin"), Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7)).ToArray());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Answers questions with fixed choices and records them.</summary>
    private sealed class FakeJob : IArchiveJob
    {
        public ConflictChoice Conflict { get; init; } = ConflictChoice.Overwrite;
        public List<string?> Passwords { get; init; } = new();
        public int Conflicts { get; private set; }
        public int PasswordQuestions { get; private set; }
        public CancellationToken Cancellation => CancellationToken.None;
        public void WaitWhilePaused() { }
        public void Report(OperationProgress progress) { }

        public Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict)
        {
            Conflicts++;
            return Task.FromResult(Conflict);
        }

        public Task<ErrorChoice> ResolveErrorAsync(OperationError error) => Task.FromResult(ErrorChoice.Skip);

        public Task<string?> AskPasswordAsync(string archivePath, bool wrongPassword)
        {
            string? answer = PasswordQuestions < Passwords.Count ? Passwords[PasswordQuestions] : null;
            PasswordQuestions++;
            return Task.FromResult(answer);
        }
    }

    private IArchiver NewArchiver()
    {
        IArchiver? archiver = null;
        new ArchivePlugin().Initialize(new CapturingHost(a => archiver = a));
        return archiver!;
    }

    private sealed class CapturingHost : IPluginHost
    {
        private readonly Action<IArchiver> _register;
        public CapturingHost(Action<IArchiver> register) => _register = register;
        public Version HostVersion => new(1, 0);
        public string GetDataDirectory(IVibePlugin plugin) => Path.GetTempPath();
        public void RegisterArchiver(IArchiver archiver) => _register(archiver);
        public void RegisterCommands(ICommandContributor contributor) { }
        public IPanelContext ActivePanel => throw new NotSupportedException();
        public IPanelContext InactivePanel => throw new NotSupportedException();
        public void Notify(string message, bool isError = false) { }
        public IPluginUi Ui => throw new NotSupportedException();
    }

    private async Task<string> PackAsync(IArchiver archiver, string formatId, string name, IArchiveJob? job = null)
    {
        string archive = Path.Combine(_root, name);
        OperationResult result = await archiver.PackAsync(new PackRequest
        {
            Sources = new[] { Path.Combine(_root, "src", "readme.txt"), Path.Combine(_root, "src", "docs") },
            BaseDirectory = Path.Combine(_root, "src"),
            ArchivePath = archive,
            FormatId = formatId,
        }, job ?? new FakeJob());
        Assert.False(result.Cancelled);
        Assert.Equal(0, result.Failed);
        Assert.Equal(3, result.Succeeded);
        Assert.True(File.Exists(archive));
        Assert.False(File.Exists(archive + ".vibe-partial"));
        return archive;
    }

    private void AssertUnpacked(string target)
    {
        Assert.Equal("hello", File.ReadAllText(Path.Combine(target, "readme.txt")));
        Assert.Equal("# a", File.ReadAllText(Path.Combine(target, "docs", "a.md")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(_root, "src", "docs", "deep", "data.bin")),
                     File.ReadAllBytes(Path.Combine(target, "docs", "deep", "data.bin")));
    }

    [Theory]
    [InlineData("zip", "test.zip")]
    [InlineData("tar", "test.tar")]
    [InlineData("tar.gz", "test.tar.gz")]
    public async Task Pack_and_unpack_round_trip(string format, string name)
    {
        IArchiver archiver = NewArchiver();
        string archive = await PackAsync(archiver, format, name);
        Assert.True(archiver.CanUnpack(archive));
        string target = Path.Combine(_root, "out-" + format);
        OperationResult result = await archiver.UnpackAsync(new UnpackRequest { ArchivePath = archive, TargetDirectory = target }, new FakeJob());
        Assert.Equal(0, result.Failed);
        Assert.Equal(3, result.Succeeded);
        AssertUnpacked(target);
        Assert.Contains("docs", result.CreatedTopLevelNames);
    }

    [Fact]
    public async Task Zip_entries_use_relative_names_with_slashes()
    {
        string archive = await PackAsync(NewArchiver(), "zip", "names.zip");
        using ZipArchive zip = ZipFile.OpenRead(archive);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("readme.txt", names);
        Assert.Contains("docs/a.md", names);
        Assert.Contains("docs/deep/data.bin", names);
    }

    [Fact]
    public async Task Unpack_with_mask_takes_only_matching_files()
    {
        IArchiver archiver = NewArchiver();
        string archive = await PackAsync(archiver, "zip", "mask.zip");
        string target = Path.Combine(_root, "masked");
        OperationResult result = await archiver.UnpackAsync(new UnpackRequest { ArchivePath = archive, TargetDirectory = target, Mask = "*.md" }, new FakeJob());
        Assert.Equal(1, result.Succeeded);
        Assert.True(File.Exists(Path.Combine(target, "docs", "a.md")));
        Assert.False(File.Exists(Path.Combine(target, "readme.txt")));
    }

    [Fact]
    public async Task Existing_files_follow_the_conflict_answer()
    {
        IArchiver archiver = NewArchiver();
        string archive = await PackAsync(archiver, "zip", "conflict.zip");
        string target = Path.Combine(_root, "existing");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "readme.txt"), "mine");

        var skip = new FakeJob { Conflict = ConflictChoice.SkipAll };
        OperationResult skipped = await archiver.UnpackAsync(new UnpackRequest { ArchivePath = archive, TargetDirectory = target }, skip);
        Assert.Equal(1, skip.Conflicts);
        Assert.Equal(1, skipped.Skipped);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(target, "readme.txt")));

        var keepBoth = new FakeJob { Conflict = ConflictChoice.Rename };
        await archiver.UnpackAsync(new UnpackRequest { ArchivePath = archive, TargetDirectory = target }, keepBoth);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(target, "readme (2).txt")));

        var overwrite = new FakeJob { Conflict = ConflictChoice.OverwriteAll };
        await archiver.UnpackAsync(new UnpackRequest { ArchivePath = archive, TargetDirectory = target }, overwrite);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(target, "readme.txt")));
    }

    [Fact]
    public async Task Packing_onto_an_existing_archive_asks_and_can_keep_both()
    {
        IArchiver archiver = NewArchiver();
        string first = await PackAsync(archiver, "zip", "same.zip");
        var job = new FakeJob { Conflict = ConflictChoice.Rename };
        OperationResult result = await archiver.PackAsync(new PackRequest
        {
            Sources = new[] { Path.Combine(_root, "src", "readme.txt") },
            BaseDirectory = Path.Combine(_root, "src"),
            ArchivePath = first,
            FormatId = "zip",
        }, job);
        Assert.Equal(1, job.Conflicts);
        Assert.Contains("same (2).zip", result.CreatedTopLevelNames);
        Assert.True(File.Exists(Path.Combine(_root, "same (2).zip")));
    }

    [Fact]
    public async Task Entries_outside_the_target_are_skipped()
    {
        string archive = Path.Combine(_root, "evil.zip");
        using (ZipArchive zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("../escaped.txt").Open()))
                w.Write("x");
            using (var w = new StreamWriter(zip.CreateEntry("ok/inside.txt").Open()))
                w.Write("y");
        }
        string target = Path.Combine(_root, "safe");
        OperationResult result = await NewArchiver().UnpackAsync(new UnpackRequest { ArchivePath = archive, TargetDirectory = target }, new FakeJob());
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
        Assert.True(File.Exists(Path.Combine(target, "ok", "inside.txt")));
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public async Task A_single_gzip_file_unpacks_to_its_name()
    {
        string gz = Path.Combine(_root, "notes.txt.gz");
        using (var file = File.Create(gz))
        using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
        using (var w = new StreamWriter(gzip))
            w.Write("compressed notes");
        IArchiver archiver = NewArchiver();
        Assert.True(archiver.CanUnpack(gz));
        string target = Path.Combine(_root, "gz");
        OperationResult result = await archiver.UnpackAsync(new UnpackRequest { ArchivePath = gz, TargetDirectory = target }, new FakeJob());
        Assert.Equal(1, result.Succeeded);
        Assert.Equal("compressed notes", File.ReadAllText(Path.Combine(target, "notes.txt")));
    }

    [Theory]
    [InlineData("a.zip", true)]
    [InlineData("a.TAR.GZ", true)]
    [InlineData("a.7z", true)]
    [InlineData("a.rar", true)]
    [InlineData("a.txt", false)]
    [InlineData("zip", false)]
    public void CanUnpack_by_name(string name, bool expected) =>
        Assert.Equal(expected, NewArchiver().CanUnpack(Path.Combine(_root, name)));

    [Fact]
    public void Entry_names_are_relative_to_the_base_directory()
    {
        Assert.Equal("docs/a.md", Archiver.EntryName(@"C:\work\docs\a.md", @"C:\work"));
        Assert.Equal("other.txt", Archiver.EntryName(@"D:\elsewhere\other.txt", @"C:\work"));
    }

    [Theory]
    [InlineData("zip", "list.zip")]
    [InlineData("tar.gz", "list.tar.gz")]
    public async Task ListAsync_returns_all_items_with_slash_paths(string format, string name)
    {
        IArchiver archiver = NewArchiver();
        string archive = await PackAsync(archiver, format, name);
        Assert.True(archiver.CanBrowse(archive));
        IReadOnlyList<ArchiveItem> items = await archiver.ListAsync(archive, CancellationToken.None);
        Assert.Contains(items, i => i.Path == "readme.txt" && !i.IsDirectory && i.Size == 5);
        Assert.Contains(items, i => i.Path == "docs/a.md");
        Assert.Contains(items, i => i.Path == "docs/deep/data.bin" && i.Size == 300_000);
        Assert.Contains(items, i => i.Path == "docs" && i.IsDirectory);
    }

    [Theory]
    [InlineData("zip", "part.zip")]
    [InlineData("tar.gz", "part.tar.gz")]
    public async Task Selected_items_unpack_relative_to_the_base_path(string format, string name)
    {
        IArchiver archiver = NewArchiver();
        string archive = await PackAsync(archiver, format, name);
        string target = Path.Combine(_root, "part-" + format);
        OperationResult result = await archiver.UnpackAsync(new UnpackRequest
        {
            ArchivePath = archive,
            TargetDirectory = target,
            Items = new[] { "docs/deep", "docs/a.md" },
            BasePath = "docs",
        }, new FakeJob());
        Assert.Equal(2, result.Succeeded);
        Assert.True(File.Exists(Path.Combine(target, "deep", "data.bin")));
        Assert.True(File.Exists(Path.Combine(target, "a.md")));
        Assert.False(File.Exists(Path.Combine(target, "readme.txt")));
        Assert.False(Directory.Exists(Path.Combine(target, "docs")));
    }

    [Fact]
    public void Comic_books_and_ebooks_are_not_browsed()
    {
        IArchiver archiver = NewArchiver();
        Assert.False(archiver.CanBrowse(Path.Combine(_root, "book.epub")));
        Assert.True(archiver.CanUnpack(Path.Combine(_root, "book.epub")));
        Assert.True(archiver.CanBrowse(Path.Combine(_root, "lib.jar")));
    }

    // ---- passwords ------------------------------------------------------------------------------

    private async Task<string> PackEncryptedAsync(IArchiver archiver, string name, string password, ArchiveCompression compression = ArchiveCompression.Normal)
    {
        string archive = Path.Combine(_root, name);
        OperationResult result = await archiver.PackAsync(new PackRequest
        {
            Sources = new[] { Path.Combine(_root, "src", "readme.txt"), Path.Combine(_root, "src", "docs") },
            BaseDirectory = Path.Combine(_root, "src"),
            ArchivePath = archive,
            FormatId = "zip",
            Compression = compression,
            Password = password,
        }, new FakeJob());
        Assert.Equal(3, result.Succeeded);
        return archive;
    }

    [Theory]
    [InlineData(ArchiveCompression.Normal)]
    [InlineData(ArchiveCompression.Store)]
    public async Task Encrypted_zip_unpacks_only_with_its_password(ArchiveCompression compression)
    {
        IArchiver archiver = NewArchiver();
        Assert.True(archiver.PackFormats.Single(f => f.Id == "zip").SupportsPassword);
        string archive = await PackEncryptedAsync(archiver, $"secret-{compression}.zip", "pässword 1", compression);

        // the files are AES encrypted (WinZip AES, method 99)
        using (var stream = File.OpenRead(archive))
        {
            var (records, _) = Zip.ReadDirectory(stream);
            Assert.All(records.Where(r => !r.IsDirectory), r => Assert.True(r.IsEncrypted && r.Method == Zip.AesMethod));
            Assert.False(Zip.CheckPassword(stream, records.First(r => !r.IsDirectory), "wrong"));
            Assert.True(Zip.CheckPassword(stream, records.First(r => !r.IsDirectory), "pässword 1"));
        }

        // a wrong password is asked again; the right one unpacks (SharpCompress reads it: another implementation)
        var job = new FakeJob { Passwords = new() { "wrong", "pässword 1" } };
        string target = Path.Combine(_root, "secret-out-" + compression);
        OperationResult result = await archiver.UnpackAsync(new UnpackRequest { ArchivePath = archive, TargetDirectory = target }, job);
        Assert.False(result.Cancelled);
        Assert.Equal(2, job.PasswordQuestions);
        Assert.Equal(3, result.Succeeded);
        AssertUnpacked(target);
    }

    [Fact]
    public async Task Tar_archives_refuse_a_password()
    {
        IArchiver archiver = NewArchiver();
        Assert.False(archiver.PackFormats.Single(f => f.Id == "tar").SupportsPassword);
        await Assert.ThrowsAsync<NotSupportedException>(() => archiver.PackAsync(new PackRequest
        {
            Sources = new[] { Path.Combine(_root, "src", "readme.txt") },
            BaseDirectory = Path.Combine(_root, "src"),
            ArchivePath = Path.Combine(_root, "no.tar"),
            FormatId = "tar",
            Password = "x",
        }, new FakeJob()));
    }

    // ---- changing archives ----------------------------------------------------------------------

    private string NewFile(string name, string text)
    {
        string path = Path.Combine(_root, "new", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static async Task<List<ArchiveItem>> ItemsAsync(IArchiver archiver, string archive) =>
        (await archiver.ListAsync(archive, CancellationToken.None)).ToList();

    [Theory]
    [InlineData("zip", "edit.zip", true)]
    [InlineData("tar", "edit.tar", true)]
    [InlineData("tar.gz", "edit.tar.gz", true)]
    [InlineData("tar.gz", "edit.tgz", true)]
    [InlineData("7z", "edit.7z", false)]
    [InlineData("rar", "edit.rar", false)]
    public void Zip_and_tar_can_be_changed(string format, string name, bool expected)
    {
        _ = format;
        Assert.Equal(expected, NewArchiver().CanEdit(Path.Combine(_root, name)));
    }

    [Theory]
    [InlineData("zip", "add.zip")]
    [InlineData("tar", "add.tar")]
    [InlineData("tar.gz", "add.tar.gz")]
    public async Task Files_and_directories_are_added_into_a_directory_of_the_archive(string format, string name)
    {
        IArchiver archiver = NewArchiver();
        string archive = await PackAsync(archiver, format, name);
        string file = NewFile("added.txt", "added text");
        NewFile(Path.Combine("folder", "inner.txt"), "inner text");
        OperationResult result = await archiver.AddAsync(new ArchiveAddRequest
        {
            ArchivePath = archive,
            Sources = new[] { file, Path.Combine(_root, "new", "folder") },
            TargetPath = "docs",
        }, new FakeJob());
        Assert.False(result.Cancelled);
        Assert.Equal(2, result.Succeeded);
        Assert.False(File.Exists(archive + ".vibe-partial"));

        var items = await ItemsAsync(archiver, archive);
        Assert.Contains(items, i => i.Path == "docs/added.txt" && i.Size == 10);
        Assert.Contains(items, i => i.Path == "docs/folder/inner.txt");
        Assert.Contains(items, i => i.Path == "docs/folder" && i.IsDirectory);

        // everything that was there is still there and unpacks
        string target = Path.Combine(_root, "add-out-" + format);
        OperationResult unpacked = await archiver.UnpackAsync(new UnpackRequest { ArchivePath = archive, TargetDirectory = target }, new FakeJob());
        Assert.Equal(5, unpacked.Succeeded);
        AssertUnpacked(target);
        Assert.Equal("added text", File.ReadAllText(Path.Combine(target, "docs", "added.txt")));
        Assert.Equal("inner text", File.ReadAllText(Path.Combine(target, "docs", "folder", "inner.txt")));
    }

    [Fact]
    public async Task A_zip_changed_in_place_is_still_a_valid_zip()
    {
        IArchiver archiver = NewArchiver();
        string archive = await PackAsync(archiver, "zip", "valid.zip");
        await archiver.AddAsync(new ArchiveAddRequest { ArchivePath = archive, Sources = new[] { NewFile("x.txt", "x") } }, new FakeJob());
        await archiver.DeleteAsync(new ArchiveDeleteRequest { ArchivePath = archive, Items = new[] { "docs/a.md" } }, new FakeJob());
        // .NET's own reader agrees
        using ZipArchive zip = ZipFile.OpenRead(archive);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("x.txt", names);
        Assert.Contains("docs/deep/data.bin", names);
        Assert.DoesNotContain("docs/a.md", names);
        using var reader = new StreamReader(zip.GetEntry("readme.txt")!.Open());
        Assert.Equal("hello", reader.ReadToEnd());
    }

    [Theory]
    [InlineData("zip", "delete.zip")]
    [InlineData("tar", "delete.tar")]
    [InlineData("tar.gz", "delete.tar.gz")]
    public async Task Files_and_directories_are_deleted(string format, string name)
    {
        IArchiver archiver = NewArchiver();
        string archive = await PackAsync(archiver, format, name);
        OperationResult result = await archiver.DeleteAsync(new ArchiveDeleteRequest
        {
            ArchivePath = archive,
            Items = new[] { "readme.txt", "docs/deep" },
        }, new FakeJob());
        Assert.Equal(2, result.Succeeded); // readme.txt and data.bin

        var items = await ItemsAsync(archiver, archive);
        Assert.DoesNotContain(items, i => i.Path == "readme.txt");
        Assert.DoesNotContain(items, i => i.Path.StartsWith("docs/deep", StringComparison.Ordinal));
        Assert.Contains(items, i => i.Path == "docs/a.md");
    }

    [Theory]
    [InlineData("zip", "empty-dir.zip")]
    [InlineData("tar.gz", "empty-dir.tar.gz")]
    public async Task Deleting_everything_in_the_panels_directory_keeps_the_directory(string format, string name)
    {
        IArchiver archiver = NewArchiver();
        string archive = await PackAsync(archiver, format, name);
        await archiver.DeleteAsync(new ArchiveDeleteRequest
        {
            ArchivePath = archive,
            Items = new[] { "docs/deep/data.bin" },
            BasePath = "docs/deep",
        }, new FakeJob());
        var items = await ItemsAsync(archiver, archive);
        Assert.Contains(items, i => i.Path == "docs/deep" && i.IsDirectory);
        Assert.DoesNotContain(items, i => i.Path == "docs/deep/data.bin");
    }

    [Theory]
    [InlineData(ConflictChoice.Skip, "hello", false)]
    [InlineData(ConflictChoice.Overwrite, "new readme", false)]
    [InlineData(ConflictChoice.Rename, "hello", true)]
    public async Task Adding_an_existing_name_follows_the_conflict_answer(ConflictChoice choice, string expectedReadme, bool keptBoth)
    {
        IArchiver archiver = NewArchiver();
        string archive = await PackAsync(archiver, "zip", $"conflict-{choice}.zip");
        var job = new FakeJob { Conflict = choice };
        await archiver.AddAsync(new ArchiveAddRequest { ArchivePath = archive, Sources = new[] { NewFile("readme.txt", "new readme") } }, job);
        Assert.Equal(1, job.Conflicts);
        string target = Path.Combine(_root, "conflict-out-" + choice);
        await archiver.UnpackAsync(new UnpackRequest { ArchivePath = archive, TargetDirectory = target }, new FakeJob());
        Assert.Equal(expectedReadme, File.ReadAllText(Path.Combine(target, "readme.txt")));
        Assert.Equal(keptBoth, File.Exists(Path.Combine(target, "readme (2).txt")));
        var items = await ItemsAsync(archiver, archive);
        Assert.Single(items, i => i.Path == "readme.txt");
    }

    [Fact]
    public async Task Files_added_to_an_encrypted_zip_get_its_password()
    {
        IArchiver archiver = NewArchiver();
        string archive = await PackEncryptedAsync(archiver, "locked.zip", "open sesame");
        var job = new FakeJob { Passwords = new() { "wrong", "open sesame" } };
        OperationResult result = await archiver.AddAsync(new ArchiveAddRequest { ArchivePath = archive, Sources = new[] { NewFile("more.txt", "more") } }, job);
        Assert.Equal(2, job.PasswordQuestions); // the wrong one was noticed
        Assert.Equal(1, result.Succeeded);
        using (var stream = File.OpenRead(archive))
        {
            var (records, _) = Zip.ReadDirectory(stream);
            Assert.True(records.Single(r => r.Name == "more.txt").IsEncrypted);
        }
        string target = Path.Combine(_root, "locked-out");
        OperationResult unpacked = await archiver.UnpackAsync(new UnpackRequest { ArchivePath = archive, TargetDirectory = target },
            new FakeJob { Passwords = new() { "open sesame" } });
        Assert.Equal(4, unpacked.Succeeded);
        Assert.Equal("more", File.ReadAllText(Path.Combine(target, "more.txt")));
        AssertUnpacked(target);
    }

    [Fact]
    public async Task Cancelling_the_password_leaves_the_archive_alone()
    {
        IArchiver archiver = NewArchiver();
        string archive = await PackEncryptedAsync(archiver, "untouched.zip", "pw");
        byte[] before = File.ReadAllBytes(archive);
        OperationResult result = await archiver.AddAsync(new ArchiveAddRequest { ArchivePath = archive, Sources = new[] { NewFile("no.txt", "no") } }, new FakeJob());
        Assert.True(result.Cancelled);
        Assert.Equal(before, File.ReadAllBytes(archive));
    }

    [Fact]
    public void Unique_entry_names_count_up()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "docs/a.txt", "docs/a (2).txt" };
        Assert.Equal("docs/a (3).txt", Archiver.UniqueEntryName("docs/a.txt", taken));
        Assert.Equal("b (2)", Archiver.UniqueEntryName("b", taken));
    }
}
