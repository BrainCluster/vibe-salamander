// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace VibeSalamander.Plugins.Archive;

/// <summary>What a self-extracting archive does when it runs.</summary>
internal sealed class SfxOptions
{
    /// <summary>The window's title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>A text above the folder choice (several lines).</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Where it extracts to: relative to the program's folder; %TEMP% and the like work.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>A program (in the extracted files, or any command) started afterwards; "" = open the folder.</summary>
    public string Run { get; set; } = string.Empty;

    /// <summary>Extracts at once, without a window (errors still show).</summary>
    public bool Quiet { get; set; }

    /// <summary>"key=value" lines for the extractor (line breaks and backslashes escaped).</summary>
    public string ToSettings()
    {
        static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\r\n", "\n").Replace("\n", "\\n");
        return $"title={Escape(Title)}\nmessage={Escape(Message)}\ntarget={Escape(Target)}\nrun={Escape(Run)}\nquiet={(Quiet ? "1" : "0")}\n";
    }
}

/// <summary>
/// Self-extracting archives (Salamander's "Create self-extracting archive"): a small Windows program followed by
/// a few settings and the ZIP archive. The program is compiled once from the source below with the C# compiler
/// that is part of Windows (.NET Framework 4), so it runs on any Windows 10 or 11 without anything to install,
/// and it unpacks with the Framework's ZipArchive. The ZIP's entries are copied as they are; their offsets count
/// from the start of the program file, so ZIP tools (and Windows) can still open the .exe as an archive.
/// </summary>
internal static class SelfExtractor
{
    /// <summary>Marks the settings that follow the program.</summary>
    private static readonly byte[] Magic = "VIBESFX1"u8.ToArray();

    /// <summary>The C# compiler of the .NET Framework that comes with Windows.</summary>
    internal static string? FindCompiler()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (string framework in new[] { "Framework64", "Framework" })
        {
            string csc = Path.Combine(windows, "Microsoft.NET", framework, "v4.0.30319", "csc.exe");
            if (File.Exists(csc))
                return csc;
        }
        return null;
    }

    /// <summary>The extractor program (compiled on first use and kept in <paramref name="cacheDirectory"/>).</summary>
    internal static byte[] GetStub(string cacheDirectory)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(StubSource)))[..12];
        string exe = Path.Combine(cacheDirectory, $"sfx-{hash}.exe");
        if (File.Exists(exe))
            return File.ReadAllBytes(exe);
        string csc = FindCompiler() ?? throw new InvalidOperationException(
            "Self-extracting archives need the C# compiler of the .NET Framework 4 that comes with Windows (csc.exe), and it wasn't found.");
        Directory.CreateDirectory(cacheDirectory);
        string source = Path.Combine(cacheDirectory, $"sfx-{hash}.cs");
        string temp = exe + ".tmp";
        File.WriteAllText(source, StubSource, Encoding.UTF8);
        var start = new ProcessStartInfo(csc)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = cacheDirectory,
        };
        foreach (string argument in new[]
                 {
                     "/nologo", "/target:winexe", "/optimize+", "/platform:anycpu", "/out:" + temp,
                     "/r:System.dll", "/r:System.Drawing.dll", "/r:System.Windows.Forms.dll",
                     "/r:System.IO.Compression.dll", "/r:System.IO.Compression.FileSystem.dll", source,
                 })
            start.ArgumentList.Add(argument);
        using (Process process = Process.Start(start) ?? throw new InvalidOperationException("The C# compiler didn't start."))
        {
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(120_000))
            {
                process.Kill();
                throw new InvalidOperationException("The C# compiler didn't finish.");
            }
            if (process.ExitCode != 0 || !File.Exists(temp))
                throw new InvalidOperationException("The self-extractor couldn't be compiled:\n" + output.Trim());
        }
        File.Move(temp, exe, overwrite: true);
        Archiver.TryDelete(source);
        return File.ReadAllBytes(exe);
    }

    /// <summary>Where a Windows program's image ends (the end of its last section): the extractor finds its settings there.</summary>
    internal static long ImageEnd(ReadOnlySpan<byte> exe)
    {
        int pe = BinaryPrimitives.ReadInt32LittleEndian(exe[0x3C..]);
        int sections = BinaryPrimitives.ReadUInt16LittleEndian(exe[(pe + 6)..]);
        int optional = BinaryPrimitives.ReadUInt16LittleEndian(exe[(pe + 20)..]);
        int table = pe + 24 + optional;
        long end = 0;
        for (int i = 0; i < sections; i++)
        {
            int section = table + i * 40;
            long size = BinaryPrimitives.ReadUInt32LittleEndian(exe[(section + 16)..]);
            long pointer = BinaryPrimitives.ReadUInt32LittleEndian(exe[(section + 20)..]);
            end = Math.Max(end, pointer + size);
        }
        return end;
    }

    /// <summary>
    /// Writes the self-extracting archive: the extractor, the settings, then the ZIP's entries (copied, not unpacked)
    /// and its comment. Throws when the ZIP has encrypted files (the extractor can't decrypt them).
    /// </summary>
    internal static void Make(string zipPath, string exePath, byte[] stub, SfxOptions options, Action<double> progress, CancellationToken ct)
    {
        using var source = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var (records, comment) = Zip.ReadDirectory(source);
        if (records.Any(r => r.IsEncrypted))
            throw new InvalidDataException("This archive has files protected by a password: a self-extracting archive can't unpack them. Pack the files without a password first.");
        long total = Math.Max(1, source.Length);
        long done = 0;
        string temp = exePath + ".vibe-tmp";
        try
        {
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                output.Write(stub, 0, (int)Math.Min(stub.Length, ImageEnd(stub)));
                byte[] settings = Encoding.UTF8.GetBytes(options.ToSettings());
                byte[] length = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(length, settings.Length);
                output.Write(Magic);
                output.Write(length);
                output.Write(settings);
                var zip = new ZipBuilder(output);
                foreach (ZipRecord record in records)
                {
                    ct.ThrowIfCancellationRequested();
                    zip.CopyRaw(source, record, n =>
                    {
                        done += n;
                        progress(Math.Min(1, done / (double)total));
                    });
                }
                zip.Finish(comment);
            }
            File.Move(temp, exePath, overwrite: true);
        }
        catch
        {
            Archiver.TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// The extractor (C# 5 for the compiler of .NET Framework 4). It reads its settings after its own image,
    /// shows a small window (folder, overwrite, open the folder), extracts with ZipArchive and can run a
    /// program afterwards. "/s" extracts without a window, "/d:folder" chooses the folder.
    /// </summary>
    internal const string StubSource = """
        using System;
        using System.Collections.Generic;
        using System.Diagnostics;
        using System.Drawing;
        using System.IO;
        using System.IO.Compression;
        using System.Runtime.InteropServices;
        using System.Text;
        using System.Threading;
        using System.Windows.Forms;

        [assembly: System.Reflection.AssemblyTitle("Self-extracting archive")]
        [assembly: System.Reflection.AssemblyProduct("Vibe Salamander self-extracting archive")]

        static class Program
        {
            [DllImport("user32.dll")]
            static extern bool SetProcessDPIAware();

            internal static Dictionary<string, string> Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            internal static string Get(string key, string fallback)
            {
                string value;
                return Settings.TryGetValue(key, out value) && value.Length > 0 ? value : fallback;
            }

            [STAThread]
            static int Main(string[] args)
            {
                string self = Application.ExecutablePath;
                try { ReadSettings(self); } catch (Exception) { }
                string name = Path.GetFileNameWithoutExtension(self);
                string target = Get("target", name);
                bool quiet = Get("quiet", "0") == "1";
                foreach (string arg in args)
                {
                    if (arg.Equals("/s", StringComparison.OrdinalIgnoreCase) || arg.Equals("-s", StringComparison.OrdinalIgnoreCase))
                        quiet = true;
                    else if (arg.StartsWith("/d:", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("-d:", StringComparison.OrdinalIgnoreCase))
                        target = arg.Substring(3);
                }
                target = Resolve(target, self);
                try { SetProcessDPIAware(); } catch (Exception) { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                if (quiet)
                {
                    try
                    {
                        Extractor.Extract(self, target, true, null, null);
                        Finish(target, false);
                        return 0;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, Get("title", name), MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return 1;
                    }
                }
                Application.Run(new SfxForm(self, target));
                return 0;
            }

            /// <summary>Environment variables expanded; relative to the program's folder.</summary>
            static string Resolve(string target, string self)
            {
                string expanded = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));
                if (!Path.IsPathRooted(expanded))
                    expanded = Path.Combine(Path.GetDirectoryName(self), expanded);
                return Path.GetFullPath(expanded);
            }

            /// <summary>Runs the program of the settings, or opens the folder.</summary>
            internal static void Finish(string target, bool openFolder)
            {
                string run = Get("run", "");
                if (run.Length > 0)
                {
                    string inside = Path.Combine(target, run);
                    var start = new ProcessStartInfo(File.Exists(inside) ? inside : run);
                    start.WorkingDirectory = target;
                    start.UseShellExecute = true;
                    Process.Start(start);
                }
                else if (openFolder)
                {
                    Process.Start("explorer.exe", "\"" + target + "\"");
                }
            }

            static void ReadSettings(string self)
            {
                using (var file = new FileStream(self, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(file))
                {
                    file.Position = 0x3C;
                    int pe = reader.ReadInt32();
                    file.Position = pe + 6;
                    int sections = reader.ReadUInt16();
                    file.Position = pe + 20;
                    int optional = reader.ReadUInt16();
                    long table = pe + 24 + optional;
                    long end = 0;
                    for (int i = 0; i < sections; i++)
                    {
                        file.Position = table + i * 40 + 16;
                        long size = reader.ReadUInt32();
                        long pointer = reader.ReadUInt32();
                        end = Math.Max(end, pointer + size);
                    }
                    file.Position = end;
                    if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != "VIBESFX1")
                        return;
                    int length = reader.ReadInt32();
                    string text = Encoding.UTF8.GetString(reader.ReadBytes(length));
                    foreach (string line in text.Split('\n'))
                    {
                        int equals = line.IndexOf('=');
                        if (equals > 0)
                            Settings[line.Substring(0, equals)] = Unescape(line.Substring(equals + 1));
                    }
                }
            }

            static string Unescape(string value)
            {
                var result = new StringBuilder();
                for (int i = 0; i < value.Length; i++)
                {
                    if (value[i] == '\\' && i + 1 < value.Length)
                    {
                        i++;
                        result.Append(value[i] == 'n' ? "\r\n" : value[i].ToString());
                    }
                    else
                    {
                        result.Append(value[i]);
                    }
                }
                return result.ToString();
            }
        }

        static class Extractor
        {
            /// <summary>Count, total size.</summary>
            internal static long[] Summary(string self)
            {
                using (var file = new FileStream(self, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var zip = new ZipArchive(file, ZipArchiveMode.Read))
                {
                    long count = 0, size = 0;
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                            continue;
                        count++;
                        size += entry.Length;
                    }
                    return new long[] { count, size };
                }
            }

            internal static void Extract(string self, string target, bool overwrite, Action<long, long> progress, Func<bool> cancelled)
            {
                using (var file = new FileStream(self, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var zip = new ZipArchive(file, ZipArchiveMode.Read))
                {
                    string root = Path.GetFullPath(target);
                    string prefix = root.TrimEnd('\\') + "\\";
                    Directory.CreateDirectory(root);
                    long total = 0, done = 0;
                    foreach (ZipArchiveEntry entry in zip.Entries)
                        total += entry.Length;
                    byte[] buffer = new byte[1 << 16];
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        string name = entry.FullName.Replace('/', '\\');
                        string path = Path.GetFullPath(Path.Combine(root, name.TrimStart('\\')));
                        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            continue; // a name that points out of the folder
                        if (name.EndsWith("\\"))
                        {
                            Directory.CreateDirectory(path);
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        if (File.Exists(path))
                        {
                            if (!overwrite)
                            {
                                done += entry.Length;
                                continue;
                            }
                            File.SetAttributes(path, FileAttributes.Normal);
                        }
                        using (Stream input = entry.Open())
                        using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            int read;
                            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                if (cancelled != null && cancelled())
                                    throw new OperationCanceledException();
                                output.Write(buffer, 0, read);
                                done += read;
                                if (progress != null)
                                    progress(done, total);
                            }
                        }
                        try { File.SetLastWriteTime(path, entry.LastWriteTime.LocalDateTime); } catch (Exception) { }
                    }
                }
            }
        }

        sealed class SfxForm : Form
        {
            readonly string _self;
            readonly TextBox _folder = new TextBox();
            readonly CheckBox _overwrite = new CheckBox();
            readonly CheckBox _open = new CheckBox();
            readonly ProgressBar _progress = new ProgressBar();
            readonly Button _extract = new Button();
            readonly Button _cancel = new Button();
            readonly Label _status = new Label();
            volatile bool _cancelled;
            bool _running;

            static string FormatSize(long bytes)
            {
                string[] units = { "B", "KB", "MB", "GB", "TB" };
                double value = bytes;
                int unit = 0;
                while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
                return (unit == 0 ? value.ToString("0") : value.ToString(value < 10 ? "0.##" : "0.#")) + " " + units[unit];
            }

            public SfxForm(string self, string target)
            {
                _self = self;
                string title = Program.Get("title", Path.GetFileNameWithoutExtension(self));
                Text = title;
                Font = SystemFonts.MessageBoxFont;
                AutoScaleMode = AutoScaleMode.Font;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                StartPosition = FormStartPosition.CenterScreen;
                ShowIcon = false;
                Padding = new Padding(16);
                AutoSize = true;
                AutoSizeMode = AutoSizeMode.GrowAndShrink;

                var layout = new TableLayoutPanel();
                layout.ColumnCount = 2;
                layout.AutoSize = true;
                layout.Dock = DockStyle.Fill;
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

                var heading = new Label();
                heading.Text = title;
                heading.Font = new Font(Font.FontFamily, Font.Size * 1.35f, FontStyle.Regular);
                heading.AutoSize = true;
                heading.Margin = new Padding(0, 0, 0, 8);
                layout.Controls.Add(heading, 0, 0);
                layout.SetColumnSpan(heading, 2);

                string summary;
                try
                {
                    long[] counts = Extractor.Summary(self);
                    summary = counts[0] + (counts[0] == 1 ? " file, " : " files, ") + FormatSize(counts[1]);
                }
                catch (Exception ex)
                {
                    summary = "The archive can't be read: " + ex.Message;
                }
                string message = Program.Get("message", "");
                var text = new Label();
                text.Text = (message.Length > 0 ? message + "\r\n\r\n" : "") + summary;
                text.AutoSize = true;
                text.MaximumSize = new Size(520, 0);
                text.Margin = new Padding(0, 0, 0, 12);
                layout.Controls.Add(text, 0, 1);
                layout.SetColumnSpan(text, 2);

                var label = new Label();
                label.Text = "Extract to:";
                label.AutoSize = true;
                label.Margin = new Padding(0, 0, 0, 4);
                layout.Controls.Add(label, 0, 2);
                layout.SetColumnSpan(label, 2);

                _folder.Text = target;
                _folder.Width = 420;
                _folder.Margin = new Padding(0, 0, 8, 8);
                layout.Controls.Add(_folder, 0, 3);
                var browse = new Button();
                browse.Text = "Browse...";
                browse.AutoSize = true;
                browse.Margin = new Padding(0, 0, 0, 8);
                browse.Click += delegate
                {
                    using (var dialog = new FolderBrowserDialog())
                    {
                        dialog.SelectedPath = _folder.Text;
                        dialog.ShowNewFolderButton = true;
                        if (dialog.ShowDialog(this) == DialogResult.OK)
                            _folder.Text = dialog.SelectedPath;
                    }
                };
                layout.Controls.Add(browse, 1, 3);

                _overwrite.Text = "Overwrite existing files";
                _overwrite.Checked = true;
                _overwrite.AutoSize = true;
                layout.Controls.Add(_overwrite, 0, 4);
                layout.SetColumnSpan(_overwrite, 2);
                _open.Text = Program.Get("run", "").Length > 0 ? "Start " + Program.Get("run", "") + " when done" : "Open the folder when done";
                _open.Checked = true;
                _open.AutoSize = true;
                _open.Margin = new Padding(3, 3, 3, 12);
                layout.Controls.Add(_open, 0, 5);
                layout.SetColumnSpan(_open, 2);

                _progress.Width = 420;
                _progress.Height = 6;
                _progress.Margin = new Padding(0, 0, 8, 4);
                _progress.Visible = false;
                layout.Controls.Add(_progress, 0, 6);
                layout.SetColumnSpan(_progress, 2);
                _status.AutoSize = true;
                _status.Margin = new Padding(0, 0, 0, 8);
                layout.Controls.Add(_status, 0, 7);
                layout.SetColumnSpan(_status, 2);

                var buttons = new FlowLayoutPanel();
                buttons.FlowDirection = FlowDirection.RightToLeft;
                buttons.AutoSize = true;
                buttons.Dock = DockStyle.Fill;
                buttons.Margin = new Padding(0);
                _cancel.Text = "Cancel";
                _cancel.AutoSize = true;
                _cancel.MinimumSize = new Size(88, 0);
                _cancel.Click += delegate { if (_running) _cancelled = true; else Close(); };
                _extract.Text = "Extract";
                _extract.AutoSize = true;
                _extract.MinimumSize = new Size(88, 0);
                _extract.Click += delegate { Start(); };
                buttons.Controls.Add(_cancel);
                buttons.Controls.Add(_extract);
                layout.Controls.Add(buttons, 0, 8);
                layout.SetColumnSpan(buttons, 2);

                Controls.Add(layout);
                AcceptButton = _extract;
                CancelButton = _cancel;
            }

            void Start()
            {
                string target = _folder.Text.Trim();
                if (target.Length == 0)
                    return;
                _running = true;
                _cancelled = false;
                _extract.Enabled = false;
                _folder.Enabled = false;
                _progress.Visible = true;
                _status.Text = "Extracting...";
                bool overwrite = _overwrite.Checked;
                bool open = _open.Checked;
                var thread = new Thread(delegate ()
                {
                    string error = null;
                    try
                    {
                        Extractor.Extract(_self, target, overwrite, delegate (long done, long total)
                        {
                            int percent = total > 0 ? (int)(done * 100 / total) : 100;
                            BeginInvoke((Action)delegate { _progress.Value = Math.Max(0, Math.Min(100, percent)); });
                        }, delegate { return _cancelled; });
                    }
                    catch (OperationCanceledException)
                    {
                        error = "";
                    }
                    catch (Exception ex)
                    {
                        error = ex.Message;
                    }
                    BeginInvoke((Action)delegate { Done(target, error, open); });
                });
                thread.IsBackground = true;
                thread.Start();
            }

            void Done(string target, string error, bool open)
            {
                _running = false;
                if (error == null)
                {
                    try { Program.Finish(target, open); } catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                    Close();
                    return;
                }
                _extract.Enabled = true;
                _folder.Enabled = true;
                _progress.Visible = false;
                _status.Text = error.Length == 0 ? "Cancelled." : "Not extracted: " + error;
            }

            protected override void OnFormClosing(FormClosingEventArgs e)
            {
                if (_running)
                {
                    _cancelled = true;
                    e.Cancel = true;
                }
                base.OnFormClosing(e);
            }
        }
        """;
}
