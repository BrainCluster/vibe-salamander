// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using VibeSalamander.Core.Viewing;

namespace VibeSalamander.Core.Tests;

/// <summary>Convert (Ctrl+K): line endings and encodings of text files, in place.</summary>
public sealed class TextConversionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vibe-convert").FullName;

    public void Dispose() => TestFiles.DeleteTree(_root);

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string Write(string name, string text, Encoding encoding) => Write(name, encoding.GetBytes(text));

    [Fact]
    public void Line_endings_become_the_chosen_ones_and_the_encoding_stays()
    {
        string unix = Write("unix.txt", "one\ntwo\nčtyři\n", new UTF8Encoding(false));

        var (outcome, _) = TextConversion.Convert(unix, new ConversionRequest(LineEnding.CrLf, null));

        Assert.Equal(ConversionOutcome.Converted, outcome);
        Assert.Equal(new UTF8Encoding(false).GetBytes("one\r\ntwo\r\nčtyři\r\n"), File.ReadAllBytes(unix));
    }

    [Fact]
    public void Keeping_line_endings_keeps_mixed_ones_while_the_encoding_changes()
    {
        string mixed = Write("mixed.txt", "a\r\nb\nc\r", new UTF8Encoding(false));

        var (outcome, _) = TextConversion.Convert(mixed, new ConversionRequest(null, TextEncodings.Utf16Le));

        Assert.Equal(ConversionOutcome.Converted, outcome);
        byte[] bytes = File.ReadAllBytes(mixed);
        Assert.Equal(new byte[] { 0xFF, 0xFE }, bytes[..2]);
        Assert.Equal("a\r\nb\nc\r", Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2));
    }

    [Fact]
    public void A_file_already_so_isnt_written_again()
    {
        string windows = Write("win.txt", "x\r\ny\r\n", new UTF8Encoding(false));
        var before = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Local);
        File.SetLastWriteTime(windows, before);

        var (outcome, _) = TextConversion.Convert(windows, new ConversionRequest(LineEnding.CrLf, TextEncodings.Utf8));

        Assert.Equal(ConversionOutcome.Unchanged, outcome);
        Assert.Equal(before, File.GetLastWriteTime(windows));
    }

    [Fact]
    public void Characters_the_new_encoding_cant_hold_leave_the_file_as_it_was()
    {
        byte[] original = new UTF8Encoding(false).GetBytes("日本語 and English\n");
        string japanese = Write("ja.txt", original);

        var (outcome, problem) = TextConversion.Convert(japanese, new ConversionRequest(null, TextEncodings.CodePage(1250)));

        Assert.Equal(ConversionOutcome.Unencodable, outcome);
        Assert.Contains("3", problem);
        Assert.Equal(original, File.ReadAllBytes(japanese));
    }

    [Fact]
    public void Files_that_arent_text_are_left_alone()
    {
        byte[] binary = { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00 };
        string program = Write("tool.exe", binary);

        var (outcome, _) = TextConversion.Convert(program, new ConversionRequest(LineEnding.Lf, TextEncodings.Utf8));

        Assert.Equal(ConversionOutcome.NotText, outcome);
        Assert.Equal(binary, File.ReadAllBytes(program));
    }

    [Fact]
    public void A_file_read_in_the_right_code_page_is_saved_as_UTF8()
    {
        Encoding cp1250 = TextEncodings.CodePage(1250).Encoding;
        string czech = Write("cz.txt", "Žluťoučký kůň\r\n", cp1250);

        var (outcome, _) = TextConversion.Convert(czech, new ConversionRequest(null, TextEncodings.Utf8, ReadAs: TextEncodings.CodePage(1250)));

        Assert.Equal(ConversionOutcome.Converted, outcome);
        Assert.Equal("Žluťoučký kůň\r\n", File.ReadAllText(czech, new UTF8Encoding(false)));
    }
}
