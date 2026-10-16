// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using VibeSalamander.Plugins.Checksum;

namespace VibeSalamander.Core.Tests;

public sealed class ChecksumPluginTests
{
    private static readonly byte[] Check = Encoding.ASCII.GetBytes("123456789");

    [Fact]
    public void Hashes_match_the_standard_check_values()
    {
        using var hasher = new MultiHasher(HashKinds.All);
        hasher.Append(Check);
        var result = hasher.Finish();
        Assert.Equal("cbf43926", result[HashKind.Crc]);
        Assert.Equal("25f9e794323b453885f5181f1b624d0b", result[HashKind.Md5]);
        Assert.Equal("f7c3bc1d808e04732adf679965ccc34ca7ae3441", result[HashKind.Sha1]);
        Assert.Equal("15e2b0d3c33891ebb0f1ef609ec419420c20e320ce94c65fbc8c3312448eb225", result[HashKind.Sha256]);
    }

    [Fact]
    public void Written_files_are_detected_and_read_back()
    {
        foreach (HashKind kind in HashKinds.All)
        {
            string hex = new string('a', HashKinds.HexLength(kind));
            var writer = new StringWriter();
            ChecksumFile.Write(writer, kind, new[] { new ChecksumLine(@"sub\file name.txt", hex), new ChecksumLine("b.bin", hex) });
            string text = writer.ToString();
            Assert.Equal(kind, ChecksumFile.Detect(text));
            var lines = ChecksumFile.Parse(text, kind, @"C:\x\list" + HashKinds.Extension(kind));
            Assert.Equal(2, lines.Count);
            Assert.Equal(@"sub\file name.txt", lines[0].FileName);
            Assert.Equal(hex, lines[0].Hex);
        }
    }

    [Theory]
    [InlineData("eb5ba72b4164d765a79a7e06cee4eead *apache.zip", "apache.zip")]
    [InlineData("eb5ba72b4164d765a79a7e06cee4eead  dir/apache.zip", @"dir\apache.zip")]
    [InlineData("MD5 (apache (1).zip) = eb5ba72b4164d765a79a7e06cee4eead", "apache (1).zip")]
    [InlineData("MD5(file().txt)= eb5ba72b4164d765a79a7e06cee4eead", "file().txt")]
    public void Md5_lines_in_common_formats(string line, string name)
    {
        string text = "# comment\n" + line + "\n";
        Assert.Equal(HashKind.Md5, ChecksumFile.Detect(text));
        var parsed = ChecksumFile.Parse(text, HashKind.Md5, @"C:\x\a.md5");
        Assert.Equal(name, parsed[0].FileName);
        Assert.Equal("eb5ba72b4164d765a79a7e06cee4eead", parsed[0].Hex);
    }

    [Fact]
    public void Sfv_lines_and_a_single_hash_without_a_name()
    {
        Assert.Equal(HashKind.Crc, ChecksumFile.Detect("; comment\nmy file.bin CBF43926\n"));
        Assert.Equal("my file.bin", ChecksumFile.Parse("my file.bin   CBF43926 \n", HashKind.Crc, @"C:\a.sfv")[0].FileName);

        // only the hash: the file is named like the checksum file without its extension
        var single = ChecksumFile.Parse("eb5ba72b4164d765a79a7e06cee4eead\n", HashKind.Md5, @"C:\x\setup.exe.md5");
        Assert.Equal("setup.exe", single[0].FileName);

        Assert.Null(ChecksumFile.Detect("this is not a checksum file\n"));
    }
}
