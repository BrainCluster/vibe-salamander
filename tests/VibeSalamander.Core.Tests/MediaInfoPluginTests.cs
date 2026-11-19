// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Plugins.MediaInfo;

namespace VibeSalamander.Core.Tests;

/// <summary>The Media Info plugin: MediaInfo's library reads a file, and its report is read back into sections.</summary>
public sealed class MediaInfoPluginTests : IDisposable
{
    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "vibe-media-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>A second of 16-bit stereo PCM at 44.1 kHz, as a WAV file.</summary>
    private string Wave()
    {
        string path = Path.Combine(_folder, "tone.wav");
        const int rate = 44100, channels = 2, bits = 16;
        int dataBytes = rate * channels * bits / 8;
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)channels);
        w.Write(rate);
        w.Write(rate * channels * bits / 8);
        w.Write((short)(channels * bits / 8));
        w.Write((short)bits);
        w.Write("data"u8);
        w.Write(dataBytes);
        for (int i = 0; i < rate; i++)
        {
            short sample = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 8000);
            w.Write(sample);
            w.Write(sample);
        }
        return path;
    }

    private static string Where()
    {
        string[] found = Directory.GetFiles(AppContext.BaseDirectory, "MediaInfo*.dll", SearchOption.AllDirectories);
        return "MediaInfo files next to the tests: " + string.Join(", ", found.Select(f => Path.GetRelativePath(AppContext.BaseDirectory, f)));
    }

    [Fact]
    public void MediaInfos_library_loads()
    {
        string? version = MediaReport.LibraryVersion();
        Assert.True(version != null, "MediaInfo.dll doesn't load. " + Where());
        Assert.Matches(@"^\d+\.\d+", version);
    }

    [Fact]
    public void A_wave_file_reads_as_pcm_audio()
    {
        string? report = MediaReport.Inform(Wave(), complete: false);
        Assert.True(report != null, "MediaInfo can't open the file. " + Where());
        IReadOnlyList<MediaSection> sections = MediaReport.Sections(report!);
        Assert.Equal(new[] { "General", "Audio" }, sections.Select(s => s.Title));
        var general = sections[0].Fields.ToDictionary(f => f.Field, f => f.Value);
        var audio = sections[1].Fields.ToDictionary(f => f.Field, f => f.Value);
        Assert.Equal("Wave", general["Format"]);
        Assert.Equal("1 s 0 ms", general["Duration"]);
        Assert.Equal("PCM", audio["Format"]);
        Assert.Equal("2 channels", audio["Channel(s)"]);
        Assert.Equal("44.1 kHz", audio["Sampling rate"]);
        Assert.Equal("16 bits", audio["Bit depth"]);
    }

    [Fact]
    public void The_complete_view_has_more_fields()
    {
        string wave = Wave();
        int basic = MediaReport.Sections(MediaReport.Inform(wave, complete: false)!).Sum(s => s.Fields.Count);
        int complete = MediaReport.Sections(MediaReport.Inform(wave, complete: true)!).Sum(s => s.Fields.Count);
        Assert.True(complete > basic, $"{complete} fields complete, {basic} basic");
    }

    [Theory]
    [InlineData("HTML", "<html")]
    [InlineData("XML", "<MediaInfo")]
    [InlineData("JSON", "\"media\"")]
    public void Reports_save_as_html_xml_and_json(string output, string sign) =>
        Assert.Contains(sign, MediaReport.Inform(Wave(), complete: false, output), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void A_file_that_isnt_media_has_no_report_worth_showing()
    {
        string text = Path.Combine(_folder, "notes.txt");
        File.WriteAllText(text, "just words");
        string? report = MediaReport.Inform(text, complete: false);
        // MediaInfo opens any file; for one it doesn't know, there's no stream besides General
        Assert.True(report == null || MediaReport.Sections(report).All(s => s.Title == "General"));
    }

    [Fact]
    public void Several_files_save_as_one_valid_report()
    {
        string[] files = { @"C:\a.wav", @"C:\b.wav" };
        Assert.Equal("[\n{\"a\":1},\n{\"b\":2}\n]\n", MediaInfoPlugin.Join(files, new[] { "{\"a\":1}", "{\"b\":2}" }, "JSON"));
        string xml = MediaInfoPlugin.Join(files, new[] { "<?xml version=\"1.0\"?>\n<MediaInfo>a</MediaInfo>", "<MediaInfo>b</MediaInfo>" }, "XML");
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(xml, @"<\?xml").Count);
        Assert.Contains("<MediaInfoReports>\n<MediaInfo>a</MediaInfo>\n<MediaInfo>b</MediaInfo>\n</MediaInfoReports>", xml);
        string text = MediaInfoPlugin.Join(files, new[] { "General\nFormat : Wave\n", null }, "Text");
        Assert.Contains(@"C:\a.wav", text);
        Assert.Contains("(MediaInfo can't read this file)", text);
    }

    [Fact]
    public void A_report_reads_back_as_sections_of_fields()
    {
        const string text = "General\nComplete name                            : C:\\x.mkv\nFormat                                   : Matroska\n\n" +
                            "Video\nID                                       : 1\nFormat                                   : AVC\n\n" +
                            "Audio #1\nFormat                                   : AAC LC\nTitle                                    : Commentary : director\n";
        IReadOnlyList<MediaSection> sections = MediaReport.Sections(text);
        Assert.Equal(new[] { "General", "Video", "Audio #1" }, sections.Select(s => s.Title));
        Assert.Equal(("Complete name", @"C:\x.mkv"), sections[0].Fields[0]);
        Assert.Equal(("Title", "Commentary : director"), sections[2].Fields[1]); // a value with " : " in it stays whole
    }
}
