// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.Operations;
using VibeSalamander.Plugins;
using VibeSalamander.Plugins.SevenZip;

namespace VibeSalamander.Core.Tests;

public sealed class SevenZipPluginTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vibe-7z-tests-" + Guid.NewGuid().ToString("N"));

    public SevenZipPluginTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "docs"));
        File.WriteAllText(Path.Combine(_root, "src", "readme.txt"), "hello");
        File.WriteAllBytes(Path.Combine(_root, "src", "docs", "data.bin"), Enumerable.Range(0, 200_000).Select(i => (byte)(i * 13)).ToArray());
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

    private sealed class FakeJob : IArchiveJob
    {
        public string? Password { get; init; }
        public CancellationToken Cancellation => CancellationToken.None;
        public void WaitWhilePaused() { }
        public void Report(OperationProgress progress) { }
        public Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict) => Task.FromResult(ConflictChoice.Overwrite);
        public Task<ErrorChoice> ResolveErrorAsync(OperationError error) => throw new InvalidOperationException(error.Message);
        public Task<string?> AskPasswordAsync(string archivePath, bool wrongPassword) => Task.FromResult(wrongPassword ? null : Password);
    }

    [Fact]
    public void Listing_is_parsed()
    {
        const string output = """
            7-Zip 26.03 (x64) : Copyright (c) 1999-2026 Igor Pavlov

            Listing archive: test.7z

            --
            Path = test.7z
            Type = 7z
            Physical Size = 1234
            Solid = +
            Method = LZMA2:24

            ----------
            Path = docs
            Folder = +
            Size = 0
            Modified = 2026-10-20 10:11:12

            Path = docs\data.bin
            Folder = -
            Size = 200000
            Modified = 2026-10-20 10:11:13.1234567
            Encrypted = -
            CRC = 1A2B3C4D

            """;
        var (archive, items) = SevenZipTool.ParseListing(output);
        Assert.Equal("7z", archive["Type"]);
        Assert.Equal("LZMA2:24", archive["Method"]);
        Assert.Equal(2, items.Count);
        Assert.True(items[0].IsDirectory);
        Assert.Equal("docs/data.bin", items[1].Path);
        Assert.Equal(200000, items[1].Size);
        Assert.Equal(0x1A2B3C4Du, items[1].Crc);
        Assert.Equal(new DateTime(2026, 10, 20, 10, 11, 13), items[1].Modified!.Value.AddTicks(-(items[1].Modified!.Value.Ticks % TimeSpan.TicksPerSecond)));
    }

    [Fact]
    public void Settings_become_7zip_switches()
    {
        var options = new Dictionary<string, string>
        {
            ["method"] = "LZMA2", ["dictionary"] = "64 MB", ["word"] = "64", ["solid"] = "4 GB", ["threads"] = "4", ["encryptNames"] = "true",
        };
        List<string> args = SevenZipArchiver.CompressionSwitches(ArchiveCompression.Maximum, options, "pw");
        Assert.Equal(new[] { "-mx=9", "-m0=LZMA2", "-md=64m", "-mfb=64", "-ms=4g", "-mmt=4", "-ppw", "-mhe=on" }, args);
        Assert.Equal(new[] { "-mx=0", "-ms=off" }, SevenZipArchiver.CompressionSwitches(ArchiveCompression.Store,
            new Dictionary<string, string> { ["solid"] = "Non-solid" }, null));
        Assert.Equal("-up1q1r2x1y2z1w2", SevenZipArchiver.UpdateSwitch(PackUpdateMode.UpdateAndAdd));
        Assert.Equal("7z", SevenZipArchiver.KindOf(@"C:\x\backup.7z.001"));
        Assert.Equal("cab", SevenZipArchiver.KindOf("setup.CAB"));
        Assert.Null(SevenZipArchiver.KindOf("disk.iso")); // disk images stay with Mount
    }

    [Fact]
    public async Task Pack_and_unpack_with_7zip()
    {
        if (!SevenZipArchiver.Available)
            return; // 7-Zip isn't installed here
        var archiver = new SevenZipArchiver();
        string archive = Path.Combine(_root, "test.7z");
        OperationResult packed = await archiver.PackAsync(new PackRequest
        {
            Sources = new[] { Path.Combine(_root, "src", "readme.txt"), Path.Combine(_root, "src", "docs") },
            BaseDirectory = Path.Combine(_root, "src"),
            ArchivePath = archive,
            FormatId = "7z",
            Password = "secret",
            Options = new Dictionary<string, string> { ["encryptNames"] = "true", ["solid"] = "Solid" },
        }, new FakeJob());
        Assert.Equal(0, packed.Failed);
        Assert.True(File.Exists(archive));
        Assert.True(archiver.CanEdit(archive));

        string target = Path.Combine(_root, "out");
        OperationResult unpacked = await archiver.UnpackAsync(new UnpackRequest { ArchivePath = archive, TargetDirectory = target },
            new FakeJob { Password = "secret" });
        Assert.Equal(0, unpacked.Failed);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(target, "readme.txt")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(_root, "src", "docs", "data.bin")), File.ReadAllBytes(Path.Combine(target, "docs", "data.bin")));

        // only the files of a directory, without it (the panel's directory inside the archive)
        string inner = Path.Combine(_root, "inner");
        await archiver.UnpackAsync(new UnpackRequest { ArchivePath = archive, TargetDirectory = inner, BasePath = "docs", Items = new[] { "docs/data.bin" } },
            new FakeJob { Password = "secret" });
        Assert.True(File.Exists(Path.Combine(inner, "data.bin")));
        Assert.False(Directory.Exists(Path.Combine(inner, "docs")));
    }
}
