// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using VibeSalamander.Core.Viewing;

namespace VibeSalamander.Core.Tests;

/// <summary>The viewer's and the editor's file handling: encodings, lines of any length, searching, hex, saving.</summary>
public sealed class ViewingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vibe-view").FullName;

    public void Dispose()
    {
        foreach (string f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string Write(string name, string text, Encoding? encoding = null) =>
        Write(name, (encoding ?? new UTF8Encoding(false)).GetPreamble().Concat((encoding ?? new UTF8Encoding(false)).GetBytes(text)).ToArray());

    private static TextFile Indexed(string path, TextEncoding? encoding = null)
    {
        TextFile file = TextFile.Open(path, encoding);
        file.IndexAsync(CancellationToken.None).GetAwaiter().GetResult();
        return file;
    }

    // ---- encodings ---------------------------------------------------------------------------------------

    [Fact]
    public void Encodings_are_recognized()
    {
        Assert.Equal("UTF-8 with BOM", TextEncodings.Detect(new UTF8Encoding(true).GetPreamble().Concat("hi"u8.ToArray()).ToArray()).Name);
        Assert.Equal("UTF-16 LE", TextEncodings.Detect(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("hi")).ToArray()).Name);
        Assert.Equal("UTF-16 BE", TextEncodings.Detect(Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes("hi")).ToArray()).Name);
        TextEncoding noBom = TextEncodings.Detect(Encoding.Unicode.GetBytes("plain text without a mark"));
        Assert.Equal(2, noBom.UnitSize);
        Assert.False(noBom.HasBom);
        Assert.Equal("UTF-8", TextEncodings.Detect(Encoding.UTF8.GetBytes("Žuta kuća – čšćđž")).Name);
        Assert.Equal(0, TextEncodings.Detect(new byte[] { 0x8A, 0x75, 0x74, 0x61 }).BomLength); // "Šuta" in 1250: not UTF-8 → ANSI
        Assert.NotEqual("UTF-8", TextEncodings.Detect(new byte[] { 0x8A, 0x75, 0x74, 0x61 }).Name);
    }

    [Fact]
    public void Binary_files_are_recognized()
    {
        Assert.True(TextEncodings.LooksBinary(new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03 }, TextEncodings.Utf8));
        Assert.False(TextEncodings.LooksBinary("line one\r\nline two\ttab"u8, TextEncodings.Utf8));
        Assert.False(TextEncodings.LooksBinary(Encoding.Unicode.GetBytes("text"), TextEncodings.Utf16Le)); // zeros are UTF-16's
    }

    [Fact]
    public void A_utf8_sequence_cut_at_the_end_still_counts_as_utf8()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("abc č");
        Assert.True(TextEncodings.IsUtf8(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.False(TextEncodings.IsUtf8(new byte[] { 0xC3, 0x28 }));
    }

    // ---- lines -------------------------------------------------------------------------------------------

    [Fact]
    public void Lines_in_every_line_ending()
    {
        using TextFile file = Indexed(Write("a.txt", "one\r\ntwo\nthree\r\n\r\nlast"));
        Assert.Equal(5, file.LineCount);
        Assert.Equal(new[] { "one", "two", "three", "", "last" }, file.GetLines(0, 10));
        Assert.Equal("three", file.GetLine(2));
        Assert.Equal(string.Empty, file.GetLine(99));
    }

    [Fact]
    public void A_final_line_break_makes_no_empty_line_and_an_empty_file_has_none()
    {
        using TextFile file = Indexed(Write("b.txt", "a\nb\n"));
        Assert.Equal(2, file.LineCount);
        using TextFile empty = Indexed(Write("e.txt", Array.Empty<byte>()));
        Assert.Equal(0, empty.LineCount);
        Assert.Empty(empty.GetLines(0, 10));
    }

    [Fact]
    public void Many_lines_are_found_through_the_sparse_index()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 1000; i++)
            sb.Append("line ").Append(i).Append('\n');
        using TextFile file = Indexed(Write("many.txt", sb.ToString()));
        Assert.Equal(1000, file.LineCount);
        Assert.Equal("line 0", file.GetLine(0));
        Assert.Equal("line 63", file.GetLine(63));
        Assert.Equal("line 64", file.GetLine(64));
        Assert.Equal("line 999", file.GetLine(999));
        Assert.Equal(new[] { "line 126", "line 127", "line 128", "line 129" }, file.GetLines(126, 4));
        Assert.Equal(500, file.LineAt(file.LineOffset(500)));
        Assert.Equal(500, file.LineAt(file.LineOffset(500) + 3));
    }

    [Fact]
    public void A_huge_line_is_shown_in_pieces_without_cutting_characters()
    {
        // 20,000 "č" (2 bytes each): pieces of about 8 KB, every one decodes cleanly
        string line = new('č', 20_000);
        using TextFile file = Indexed(Write("long.txt", line + "\nnext"));
        Assert.True(file.LineCount >= 4);
        IReadOnlyList<string> pieces = file.GetLines(0, (int)file.LineCount);
        Assert.Equal("next", pieces[^1]);
        Assert.Equal(line, string.Concat(pieces.Take(pieces.Count - 1)));
        Assert.DoesNotContain(pieces, p => p.Contains('�'));
    }

    [Fact]
    public void Utf16_lines()
    {
        string path = Write("u16.txt", "první\r\ndruhý\nтретий", Encoding.Unicode);
        using TextFile file = Indexed(path);
        Assert.Equal(2, file.Encoding.UnitSize);
        Assert.Equal(new[] { "první", "druhý", "третий" }, file.GetLines(0, 3));
        using TextFile be = Indexed(Write("u16be.txt", "a\nb", Encoding.BigEndianUnicode));
        Assert.Equal(new[] { "a", "b" }, be.GetLines(0, 3));
    }

    [Fact]
    public void A_chosen_encoding_is_used()
    {
        string path = Write("1250.txt", new byte[] { 0x8A, 0x75, 0x74, 0x61 }); // "Šuta" in Windows-1250
        using TextFile file = Indexed(path, TextEncodings.CodePage(1250));
        Assert.Equal("Šuta", file.GetLine(0));
    }

    [Fact]
    public void Reading_forward_and_backward_gives_every_line_once()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 300; i++)
            sb.Append(i).Append("\r\n");
        using TextFile file = Indexed(Write("rw.txt", sb.ToString()));
        Assert.Equal(Enumerable.Range(0, 300).Select(i => i.ToString()), file.ReadForward(0).Select(l => l.Text));
        Assert.Equal(Enumerable.Range(150, 150).Select(i => (long)i), file.ReadForward(150).Select(l => l.Line));
        Assert.Equal(Enumerable.Range(0, 200).Reverse().Select(i => i.ToString()), file.ReadBackward(199).Select(l => l.Text));
    }

    // ---- search ------------------------------------------------------------------------------------------

    private TextFile SearchFile() => Indexed(Write("s.txt", "The cat sat.\nconcatenate\nA CAT here\nno match\ncat"));

    [Fact]
    public void Find_forward_and_wrap_around()
    {
        using TextFile file = SearchFile();
        var options = new FindOptions("cat");
        Assert.Equal(new TextMatch(0, 4, 3), TextSearch.Find(file, options, 0, 0, false, out bool wrapped));
        Assert.False(wrapped);
        Assert.Equal(new TextMatch(1, 3, 3), TextSearch.Find(file, options, 0, 5, false, out _));
        Assert.Equal(new TextMatch(4, 0, 3), TextSearch.Find(file, options, 3, 0, false, out _));
        Assert.Equal(new TextMatch(0, 4, 3), TextSearch.Find(file, options, 4, 1, false, out wrapped));
        Assert.True(wrapped);
    }

    [Fact]
    public void Find_with_case_whole_words_backward_and_regex()
    {
        using TextFile file = SearchFile();
        Assert.Equal(new TextMatch(2, 2, 3), TextSearch.Find(file, new FindOptions("CAT", MatchCase: true), 0, 0, false, out _));
        Assert.Equal(new TextMatch(2, 2, 3), TextSearch.Find(file, new FindOptions("cat", WholeWord: true), 1, 0, false, out _)); // not "concatenate"
        Assert.Equal(new TextMatch(1, 3, 3), TextSearch.Find(file, new FindOptions("cat"), 2, 2, true, out _));
        Assert.Equal(new TextMatch(4, 0, 3), TextSearch.Find(file, new FindOptions("cat"), 0, 4, true, out bool wrapped));
        Assert.True(wrapped);
        Assert.Equal(new TextMatch(0, 4, 5), TextSearch.Find(file, new FindOptions(@"c\w+ s", Regex: true), 0, 0, false, out _));
        Assert.Null(TextSearch.Find(file, new FindOptions("dog"), 0, 0, false, out _));
        Assert.NotNull(TextSearch.Problem(new FindOptions("(", Regex: true)));
    }

    [Fact]
    public void Bytes_are_found_both_ways()
    {
        byte[] data = Enumerable.Range(0, 3_000_000).Select(i => (byte)(i % 251)).ToArray();
        byte[] Read(long offset, int count) => data.AsSpan((int)offset, (int)Math.Min(count, data.Length - offset)).ToArray();
        byte[] pattern = { 10, 11, 12 };
        Assert.Equal(10, ByteSearch.Find(Read, data.Length, pattern, 0, false));
        Assert.Equal(261, ByteSearch.Find(Read, data.Length, pattern, 11, false));
        long last = ByteSearch.Find(Read, data.Length, pattern, data.Length, true);
        Assert.Equal(10 + 251 * ((data.Length - 13) / 251), last);
        Assert.Equal(10, ByteSearch.Find(Read, data.Length, pattern, 261, true));
        Assert.Equal(-1, ByteSearch.Find(Read, data.Length, pattern, 10, true));
    }

    [Theory]
    [InlineData("4D 5A 90", new byte[] { 0x4D, 0x5A, 0x90 })]
    [InlineData("4d5a90", new byte[] { 0x4D, 0x5A, 0x90 })]
    [InlineData("0x4D, 0x5A", new byte[] { 0x4D, 0x5A })]
    [InlineData("A", new byte[] { 0x0A })]
    public void Hex_patterns(string text, byte[] bytes) =>
        Assert.Equal(bytes, ByteSearch.Pattern(text, asHex: true, Encoding.UTF8));

    [Fact]
    public void A_bad_hex_pattern_is_refused() => Assert.Null(ByteSearch.Pattern("4G", asHex: true, Encoding.UTF8));

    [Fact]
    public void Hex_rows()
    {
        string row = HexFormat.Row("Hello World\r\n\0\xFF~"u8.ToArray().Take(16).ToArray(), 16, 8);
        Assert.Equal("00000010  48 65 6C 6C 6F 20 57 6F  72 6C 64 0D 0A 00 C3 BF  Hello World.....", row);
        Assert.Equal('4', row[HexFormat.HexColumn(0, 8)]);
        Assert.Equal('7', row[HexFormat.HexColumn(8, 8)]);
        Assert.Equal('H', row[HexFormat.CharColumn(0, 8)]);
        Assert.Equal("00000000  41" + new string(' ', 48) + "A", HexFormat.Row(new byte[] { 0x41 }, 0, 8));
        Assert.Equal(12, HexFormat.OffsetDigits(5_000_000_000));
    }

    // ---- editing: load and save --------------------------------------------------------------------------

    [Fact]
    public void Line_endings_are_recognized_and_changed()
    {
        Assert.Equal((LineEnding.CrLf, false), TextDocuments.DetectLineEnding("a\r\nb\r\n"));
        Assert.Equal((LineEnding.Lf, true), TextDocuments.DetectLineEnding("a\nb\nc\r\n"));
        Assert.Equal((LineEnding.CrLf, false), TextDocuments.DetectLineEnding("no breaks"));
        Assert.Equal("a\nb\nc\n", TextDocuments.WithLineEndings("a\r\nb\rc\n", LineEnding.Lf));
        Assert.Equal("a\r\nb\r\n", TextDocuments.WithLineEndings("a\nb\r", LineEnding.CrLf));
    }

    [Fact]
    public void Saving_keeps_the_encoding_and_its_mark()
    {
        string path = Write("bom.txt", "první\r\n", new UTF8Encoding(true));
        LoadedText loaded = TextDocuments.Load(path);
        Assert.Equal("první\r\n", loaded.Text); // no BOM character in the text
        Assert.True(loaded.Encoding.HasBom);
        TextDocuments.Save(path, loaded.Text + "druhý\n", loaded.Encoding, loaded.LineEnding);
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal("první\r\ndruhý\r\n", Encoding.UTF8.GetString(bytes[3..]));

        string u16 = Write("u16.txt", "a\nb", Encoding.Unicode);
        LoadedText wide = TextDocuments.Load(u16);
        TextDocuments.Save(u16, "x\ny", wide.Encoding, LineEnding.Lf);
        Assert.Equal(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("x\ny")), File.ReadAllBytes(u16));
    }

    [Fact]
    public void Saving_is_safe_and_can_keep_a_backup()
    {
        string path = Write("keep.txt", "old");
        File.SetAttributes(path, FileAttributes.Hidden);
        TextDocuments.Save(path, "new", TextEncodings.Utf8, LineEnding.CrLf, backup: true);
        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal("old", File.ReadAllText(path + ".bak"));
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden)); // the file's attributes stay
        Assert.Equal(2, Directory.GetFiles(_root).Length); // no temporary file left

        string readOnly = Write("ro.txt", "fixed");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        Assert.Throws<UnauthorizedAccessException>(() => TextDocuments.Save(readOnly, "changed", TextEncodings.Utf8, LineEnding.CrLf));
        Assert.Equal("fixed", File.ReadAllText(readOnly));
        Assert.Equal(3, Directory.GetFiles(_root).Length);

        string fresh = Path.Combine(_root, "new.txt");
        TextDocuments.Save(fresh, "made", TextEncodings.Utf8, LineEnding.Lf);
        Assert.Equal("made", File.ReadAllText(fresh));
    }

    [Fact]
    public void Characters_an_encoding_cannot_hold_are_counted()
    {
        Assert.Equal(0, TextDocuments.Unencodable("plain ascii", TextEncodings.CodePage(1252)));
        Assert.Equal(0, TextDocuments.Unencodable("čćž", TextEncodings.CodePage(1250)));
        Assert.Equal(2, TextDocuments.Unencodable("čć in 1252", TextEncodings.CodePage(1252)));
        Assert.Equal(0, TextDocuments.Unencodable("anything 日本", TextEncodings.Utf8));
    }

    [Fact]
    public void A_file_too_large_to_edit_is_refused()
    {
        string path = Path.Combine(_root, "big.bin");
        using (var stream = File.Create(path))
            stream.SetLength(TextDocuments.MaxEditableBytes + 1);
        Assert.Throws<IOException>(() => TextDocuments.Load(path));
    }

    // ---- modes -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("photo.JPG", ViewerMode.Image)]
    [InlineData("scan.pdf", ViewerMode.Pdf)]
    [InlineData("song.flac", ViewerMode.Media)]
    [InlineData("README.md", ViewerMode.Preview)]
    [InlineData("analysis.ipynb", ViewerMode.Preview)]
    [InlineData("page.html", ViewerMode.Preview)]
    [InlineData("data.json", ViewerMode.Text)]
    [InlineData("notes.txt", ViewerMode.Text)]
    public void Files_open_in_their_mode(string name, ViewerMode mode) =>
        Assert.Equal(mode, ViewerModes.Initial(name, looksBinary: false));

    [Fact]
    public void Modes_offered()
    {
        Assert.Equal(new[] { ViewerMode.Tree, ViewerMode.Text, ViewerMode.Hex }, ViewerModes.For("a.yaml"));
        Assert.Equal(new[] { ViewerMode.Image, ViewerMode.Tree, ViewerMode.Text, ViewerMode.Hex }, ViewerModes.For("logo.svg"));
        Assert.Equal(new[] { ViewerMode.Text, ViewerMode.Hex }, ViewerModes.For("program.cs"));
        Assert.Equal(ViewerMode.Hex, ViewerModes.Initial("program.exe", looksBinary: true));
    }
}
