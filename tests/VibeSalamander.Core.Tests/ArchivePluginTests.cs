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
}
