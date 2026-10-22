// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using VibeSalamander.Plugins.Archive;

namespace VibeSalamander.Core.Tests;

/// <summary>Test archive, self-extracting archives and ZIP comments (the archive plugin's commands).</summary>
public sealed class ArchiveToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vibe-archive-tools-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private static readonly byte[] Pattern = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("VIBE-PATTERN-0123456789-", 400)));

    public ArchiveToolsTests()
    {
        _source = Path.Combine(_root, "src");
        Directory.CreateDirectory(Path.Combine(_source, "docs"));
        File.WriteAllText(Path.Combine(_source, "readme.txt"), "hello");
        File.WriteAllText(Path.Combine(_source, "docs", "a.md"), "# a");
        File.WriteAllBytes(Path.Combine(_source, "docs", "pattern.bin"), Pattern);
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

    private string MakeZip(CompressionLevel level = CompressionLevel.Optimal)
    {
        string zip = Path.Combine(_root, "test.zip");
        ZipFile.CreateFromDirectory(_source, zip, level, includeBaseDirectory: false);
        return zip;
    }

    private static List<TestedFile> RunTest(string path, string kind)
    {
        var results = new List<TestedFile>();
        ArchiveTest.Run(path, kind, null, results.Add, _ => { }, CancellationToken.None);
        return results;
    }

    [Fact]
    public void Test_PassesAGoodZip()
    {
        List<TestedFile> results = RunTest(MakeZip(), "zip");
        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Null(r.Problem));
        Assert.Contains(results, r => r.Name.EndsWith("pattern.bin", StringComparison.Ordinal) && r.Size == Pattern.Length);
    }

    [Fact]
    public void Test_FindsDamagedData()
    {
        string zip = MakeZip(CompressionLevel.NoCompression);
        byte[] bytes = File.ReadAllBytes(zip);
        int at = bytes.AsSpan().IndexOf(Pattern.AsSpan(0, 64));
        Assert.True(at > 0);
        bytes[at + 100] ^= 0x55; // one changed byte in the stored data
        File.WriteAllBytes(zip, bytes);

        List<TestedFile> results = RunTest(zip, "zip");
        Assert.Contains(results, r => r.Name.EndsWith("pattern.bin", StringComparison.Ordinal) && r.Problem != null);
        Assert.Contains(results, r => r.Name.EndsWith("readme.txt", StringComparison.Ordinal) && r.Problem == null);
    }

    [Fact]
    public void Test_ReadsTarArchives()
    {
        string tar = Path.Combine(_root, "test.tar");
        TarFile.CreateFromDirectory(_source, tar, includeBaseDirectory: false);
        List<TestedFile> results = RunTest(tar, "tar");
        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Null(r.Problem));
    }

    [Fact]
    public void Comment_IsWrittenInPlaceAndRead()
    {
        string zip = MakeZip();
        const string text = "Release 1.0\nŽivjeli – sretno!";
        Zip.WriteComment(zip, text);
        using (var file = File.OpenRead(zip))
            Assert.Equal(text, Zip.DecodeComment(Zip.ReadDirectory(file).Comment));
        using (ZipArchive archive = ZipFile.OpenRead(zip))
            Assert.Equal(3, archive.Entries.Count(e => e.Length > 0));

        Zip.WriteComment(zip, string.Empty);
        using (var file = File.OpenRead(zip))
            Assert.Empty(Zip.ReadDirectory(file).Comment);
        Assert.Empty(RunTest(zip, "zip").Where(r => r.Problem != null));
    }

    [Fact]
    public void SfxSettings_EscapeLinesAndBackslashes()
    {
        string settings = new SfxOptions { Title = "Setup", Message = "Line 1\nC:\\Path", Target = @"%TEMP%\app", Quiet = true }.ToSettings();
        Assert.Contains("message=Line 1\\nC:\\\\Path\n", settings);
        Assert.Contains("target=%TEMP%\\\\app\n", settings);
        Assert.Contains("quiet=1\n", settings);
    }

    [Fact]
    public void SelfExtractor_CompilesAndTheExeIsStillAZip()
    {
        if (SelfExtractor.FindCompiler() == null)
            return; // only on Windows
        string zip = MakeZip();
        Zip.WriteComment(zip, "kept");
        byte[] stub = SelfExtractor.GetStub(Path.Combine(_root, "cache"));
        Assert.Equal(stub.Length, SelfExtractor.ImageEnd(stub));

        string exe = Path.Combine(_root, "test.exe");
        SelfExtractor.Make(zip, exe, stub, new SfxOptions { Title = "Test", Target = "out" }, _ => { }, CancellationToken.None);

        byte[] written = File.ReadAllBytes(exe);
        Assert.Equal("MZ", Encoding.ASCII.GetString(written, 0, 2));
        Assert.Equal("VIBESFX1", Encoding.ASCII.GetString(written, stub.Length, 8));
        using (ZipArchive archive = ZipFile.OpenRead(exe))
        {
            ZipArchiveEntry pattern = archive.Entries.Single(e => e.Name == "pattern.bin");
            using var data = new MemoryStream();
            using (Stream input = pattern.Open())
                input.CopyTo(data);
            Assert.Equal(Pattern, data.ToArray());
        }
        Assert.All(RunTest(exe, "zip"), r => Assert.Null(r.Problem));
    }
}
