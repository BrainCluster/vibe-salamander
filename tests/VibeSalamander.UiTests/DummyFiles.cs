// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO.Compression;

namespace VibeSalamander.UiTests;

/// <summary>
/// A folder of made-up files to look at, the same on every run: documents, pictures, archives, hidden and read-only
/// files, long and non-English names, deep folders and one folder with thousands of files.
/// </summary>
public sealed class DummyFiles : IDisposable
{
    public string Root { get; }
    public string Documents => Path.Combine(Root, "Documents");
    public string Photos => Path.Combine(Root, "Photos");
    public string Target => Path.Combine(Root, "Target");
    public string Many => Path.Combine(Root, "Many files");

    /// <summary>A file of each kind the viewer and the editor know.</summary>
    public string Samples => Path.Combine(Root, "Samples");

    /// <param name="root">Where (the documentation pictures use a short path like C:\Demo); a new temp folder when null.</param>
    public DummyFiles(string? root = null)
    {
        Root = root ?? Path.Combine(Path.GetTempPath(), "vibe-ui-" + Guid.NewGuid().ToString("N")[..8]);
        if (Directory.Exists(Root))
            new DummyFilesCleaner(Root).Dispose(); // left over from an earlier run
        Directory.CreateDirectory(Root);
        var time = new DateTime(2026, 3, 14, 9, 26, 53);

        Text(Documents, "report.txt", "Quarterly report\r\n" + string.Concat(Enumerable.Repeat("Lorem ipsum dolor sit amet. ", 400)), time);
        Text(Documents, "notes.md", "# Notes\r\n- one\r\n- two\r\n", time.AddDays(-3));
        Text(Documents, "budget 2026.csv", "month;amount\r\njan;100\r\nfeb;120\r\n", time.AddDays(-10));
        Text(Documents, "setup.exe", "MZ not really a program", time.AddMonths(-2));
        Text(Documents, "script.ps1", "Write-Host 'hello'", time.AddMonths(-5));
        Text(Documents, "Žuta kuća – čšćđž.txt", "Unicode", time.AddDays(-1));
        Text(Documents, "日本語のファイル.txt", "Unicode", time.AddDays(-1));
        Text(Documents, "a file with a really long name that goes on and on to see how the columns cut it off.txt", "long", time);
        File.SetAttributes(Text(Documents, "read-only.txt", "ro", time), FileAttributes.ReadOnly);
        File.SetAttributes(Text(Documents, "hidden.txt", "hidden", time), FileAttributes.Hidden);
        File.WriteAllBytes(Path.Combine(Documents, "data.bin"), Enumerable.Range(0, 3_000_000).Select(i => (byte)i).ToArray());
        Directory.CreateDirectory(Path.Combine(Documents, "Empty folder"));
        Text(Path.Combine(Documents, "Projects", "Vibe", "src", "deep", "deeper"), "bottom.txt", "deep", time);
        Text(Path.Combine(Documents, "Projects", "Old"), "readme.txt", "old", time.AddYears(-3));

        using (ZipArchive zip = ZipFile.Open(Path.Combine(Documents, "archive.zip"), ZipArchiveMode.Create))
        {
            zip.CreateEntry("inside/one.txt").Open().Dispose();
            using var writer = new StreamWriter(zip.CreateEntry("inside/two.txt").Open());
            writer.Write("two");
        }

        Directory.CreateDirectory(Photos);
        Color[][] palettes =
        {
            new[] { Color.FromArgb(255, 94, 77), Color.FromArgb(255, 195, 0) },
            new[] { Color.FromArgb(46, 134, 222), Color.FromArgb(10, 61, 98) },
            new[] { Color.FromArgb(29, 209, 161), Color.FromArgb(16, 172, 132) },
            new[] { Color.FromArgb(155, 89, 182), Color.FromArgb(232, 67, 147) },
            new[] { Color.FromArgb(253, 203, 110), Color.FromArgb(225, 112, 85) },
            new[] { Color.FromArgb(99, 110, 114), Color.FromArgb(45, 52, 54) },
        };
        for (int i = 0; i < 12; i++)
        {
            Color[] colors = palettes[i % palettes.Length];
            Picture(Path.Combine(Photos, $"holiday {i + 1:00}.{(i % 3 == 0 ? "png" : "jpg")}"), colors[0], colors[1], i);
        }
        Text(Photos, "captions.txt", "photos", time);

        ViewerSamples(Samples, Path.Combine(Photos, "holiday 01.png"), time);

        Directory.CreateDirectory(Target);
        Directory.CreateDirectory(Many);
        for (int i = 0; i < 5000; i++)
            File.WriteAllText(Path.Combine(Many, $"file {i:0000}.txt"), "x");
    }

    /// <summary>Text of many lines, JSON, XML, YAML, Markdown, a notebook, a web page, a PDF, a sound, source code.</summary>
    private static void ViewerSamples(string folder, string picture, DateTime time)
    {
        Directory.CreateDirectory(folder);
        var log = new System.Text.StringBuilder();
        for (int i = 1; i <= 200_000; i++)
            log.Append("2026-03-14 09:").Append((i / 60 % 60).ToString("00")).Append(':').Append((i % 60).ToString("00"))
               .Append("  INFO  request ").Append(i).Append(" served in ").Append(i % 97 + 3).Append(" ms\r\n");
        Text(folder, "server.log", log.ToString(), time);
        Text(folder, "config.json", """
            {
              "name": "Vibe Salamander",
              "version": "0.6.0",
              "panels": { "left": "C:\\Demo\\Documents", "right": "C:\\Demo\\Photos", "view": "Detailed" },
              // comments are fine in a .json for the viewer
              "plugins": ["Archives", "FTP / SFTP", "Checksum", "Disk Map"],
              "transfers": { "parallel": 3, "closeAfterSeconds": 2, "confirmOverwrite": true },
              "colors": null
            }
            """, time);
        Text(folder, "layout.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <window title="Vibe Salamander" width="1280" height="800">
              <panel side="left" path="C:\Demo\Documents"><column name="Name" width="320"/><column name="Size"/></panel>
              <panel side="right" path="C:\Demo\Photos" view="Thumbnails"/>
              <!-- the transfer panel shows below the panels -->
              <transfers visible="true">Copy, move and delete run here.</transfers>
            </window>
            """, time);
        Text(folder, "pipeline.yaml", """
            name: build
            on:
              push:
                branches: [main]
            jobs:
              test:
                runs-on: windows-latest
                steps:
                  - uses: actions/checkout@v4
                  - run: dotnet test
                    timeout-minutes: 20
            """, time);
        string pictureBase64 = Convert.ToBase64String(File.ReadAllBytes(picture));
        Text(folder, "README.md", """
            # Vibe Salamander

            A fast, keyboard-first **dual-pane file manager** for Windows.

            | Key | Action |
            |-----|--------|
            | F3  | View   |
            | F4  | Edit   |
            | F5  | Copy   |

            - [x] Text and hex for any size
            - [x] Pictures, PDFs, audio and video
            - [ ] Coffee

            ```csharp
            Console.WriteLine("Hello from the viewer");
            ```

            > The picture below is a relative link to the Photos folder.

            ![Holiday](../Photos/holiday%2001.png)
            """, time);
        Text(folder, "analysis.ipynb", "{\"cells\": [" +
            "{\"cell_type\": \"markdown\", \"source\": [\"# Transfer speed\\n\", \"Measured on a *USB 3* stick.\"]}," +
            "{\"cell_type\": \"code\", \"execution_count\": 1, \"source\": [\"speeds = [112, 98, 120]\\n\", \"print(sum(speeds) / len(speeds))\"], " +
            "\"outputs\": [{\"output_type\": \"stream\", \"name\": \"stdout\", \"text\": [\"110.0\\n\"]}]}," +
            "{\"cell_type\": \"code\", \"execution_count\": 2, \"source\": \"show(photo)\", " +
            "\"outputs\": [{\"output_type\": \"display_data\", \"data\": {\"image/png\": \"" + pictureBase64 + "\", \"text/plain\": [\"<Figure>\"]}}]}" +
            "], \"metadata\": {}, \"nbformat\": 4, \"nbformat_minor\": 5}", time);
        Text(folder, "page.html", """
            <!DOCTYPE html>
            <html><head><meta charset="utf-8"><title>A page</title>
            <style>body{font-family:Segoe UI,sans-serif;margin:40px;background:#f3f0ff} h1{color:#6b46c1}</style></head>
            <body><h1>A web page</h1><p>Shown without its scripts and without anything from the internet.</p>
            <img src="https://example.com/remote.png" alt="(a remote picture, blocked)">
            <script>document.body.insertAdjacentHTML('beforeend', '<p>Scripts ran.</p>');</script>
            </body></html>
            """, time);
        Text(folder, "Program.cs", """
            // SPDX-License-Identifier: GPL-3.0-or-later
            using System;

            namespace Demo;

            /// <summary>An example for the editor.</summary>
            public static class Program
            {
                public static void Main(string[] args)
                {
                    foreach (string arg in args)
                        Console.WriteLine($"Hello, {arg}!");
                    if (args.Length == 0)
                    {
                        Console.WriteLine("Hello, world!");
                    }
                }
            }
            """, time);
        File.WriteAllBytes(Path.Combine(folder, "document.pdf"), Pdf("Vibe Salamander", "A PDF made by the UI tests: page one."));
        File.WriteAllBytes(Path.Combine(folder, "tone.wav"), Wave(440, 2));
    }

    /// <summary>A small valid PDF with one page of text.</summary>
    private static byte[] Pdf(string title, string line)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            "",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        string content = $"BT /F1 32 Tf 72 740 Td ({title}) Tj ET\nBT /F1 14 Tf 72 700 Td ({line}) Tj ET\n" +
                         "0.42 0.27 0.76 rg 72 300 451 300 re f\n";
        objects[3] = $"<< /Length {content.Length} >>\nstream\n{content}endstream";
        var pdf = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            pdf.Append(offset.ToString("0000000000")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(pdf.ToString());
    }

    /// <summary>A sine tone as a 16-bit mono WAV.</summary>
    private static byte[] Wave(int frequency, int seconds)
    {
        const int rate = 22050;
        int samples = rate * seconds;
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + samples * 2);
        w.Write("WAVEfmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8.ToArray());
        w.Write(samples * 2);
        for (int i = 0; i < samples; i++)
            w.Write((short)(Math.Sin(2 * Math.PI * frequency * i / rate) * 8000));
        w.Flush();
        return stream.ToArray();
    }

    private static string Text(string folder, string name, string content, DateTime time)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTime(path, time);
        return path;
    }

    private static void Picture(string path, Color from, Color to, int seed)
    {
        using var bitmap = new Bitmap(640, 420);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var gradient = new LinearGradientBrush(new Rectangle(0, 0, 640, 420), from, to, 35f + seed * 20))
                g.FillRectangle(gradient, 0, 0, 640, 420);
            var random = new Random(seed);
            for (int k = 0; k < 6; k++)
            {
                using var brush = new SolidBrush(Color.FromArgb(90, 255, 255, 255));
                int size = random.Next(60, 200);
                g.FillEllipse(brush, random.Next(-40, 600), random.Next(-40, 380), size, size);
            }
        }
        bitmap.Save(path, path.EndsWith(".png", StringComparison.Ordinal) ? ImageFormat.Png : ImageFormat.Jpeg);
    }

    public void Dispose() => new DummyFilesCleaner(Root).Dispose();
}

/// <summary>Deletes a dummy files folder (read-only files too); what can't be deleted on a build machine stays.</summary>
internal sealed class DummyFilesCleaner(string root) : IDisposable
{
    public void Dispose()
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a temp folder left behind on a build machine is fine
        }
    }
}
