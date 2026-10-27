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

        Directory.CreateDirectory(Target);
        Directory.CreateDirectory(Many);
        for (int i = 0; i < 5000; i++)
            File.WriteAllText(Path.Combine(Many, $"file {i:0000}.txt"), "x");
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
