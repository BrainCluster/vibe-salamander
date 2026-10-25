// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace VibeSalamander.Plugins.SevenZip;

/// <summary>What 7z.exe answered.</summary>
/// <param name="ExitCode">0 = fine, 1 = warnings (files that couldn't be read), 2 = error, 7 = bad command line, 8 = no memory, 255 = stopped.</param>
internal sealed record SevenZipResult(int ExitCode, string Output, string Errors)
{
    public bool Ok => ExitCode is 0 or 1;

    public bool WrongPassword =>
        Errors.Contains("Wrong password", StringComparison.OrdinalIgnoreCase) ||
        Output.Contains("Wrong password", StringComparison.OrdinalIgnoreCase) ||
        Errors.Contains("Can not open encrypted archive", StringComparison.OrdinalIgnoreCase);

    /// <summary>The lines that say what went wrong (for messages).</summary>
    public string Problem
    {
        get
        {
            string text = string.Join("\n", (Errors + "\n" + Output).Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && (l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) || l.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                                             l.Contains("password", StringComparison.OrdinalIgnoreCase) || l.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase)))
                .Distinct().Take(8));
            return text.Length > 0 ? text : $"7-Zip stopped with code {ExitCode}.";
        }
    }
}

/// <summary>One item of a 7-Zip listing.</summary>
internal sealed record SevenZipItem(string Path, bool IsDirectory, long Size, DateTime? Modified, bool Encrypted, uint? Crc);

/// <summary>Finds 7-Zip and runs 7z.exe (console, UTF-8 output, progress on stdout).</summary>
internal static class SevenZipTool
{
    /// <summary>Used for archives without a password: 7-Zip never stops to ask on the (missing) console.</summary>
    public const string NoPassword = "-pvibe-salamander-no-password";

    private static string? _exe;
    private static bool _searched;

    /// <summary>7z.exe: the one in the plugin's folder, else an installed 7-Zip; null when there is none.</summary>
    public static string? Exe
    {
        get
        {
            if (_searched)
                return _exe;
            _searched = true;
            var candidates = new List<string>();
            if (Path.GetDirectoryName(typeof(SevenZipTool).Assembly.Location) is { Length: > 0 } pluginFolder)
                candidates.Add(Path.Combine(pluginFolder, "7z.exe"));
            foreach (string key in new[] { @"HKEY_CURRENT_USER\Software\7-Zip", @"HKEY_LOCAL_MACHINE\SOFTWARE\7-Zip" })
            {
                foreach (string value in new[] { "Path64", "Path" })
                {
                    try
                    {
                        if (Microsoft.Win32.Registry.GetValue(key, value, null) is string folder && folder.Length > 0)
                            candidates.Add(Path.Combine(folder, "7z.exe"));
                    }
                    catch (Exception)
                    {
                        // no registry access: the usual folders then
                    }
                }
            }
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe"));
            _exe = candidates.FirstOrDefault(File.Exists);
            return _exe;
        }
    }

    /// <summary>The folder of 7z.exe (its 7z.sfx module is there).</summary>
    public static string? Folder => Exe is { } exe ? Path.GetDirectoryName(exe) : null;

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr process);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr process);

    private static readonly Regex Percent = new(@"(\d{1,3})%", RegexOptions.Compiled);

    /// <summary>
    /// Runs 7z.exe. <paramref name="progress"/> gets the percentage done (add "-bsp1" for it);
    /// <paramref name="job"/> (optional) pauses 7-Zip while the transfer is paused and stops it when cancelled.
    /// </summary>
    public static SevenZipResult Run(IEnumerable<string> arguments, string? workingDirectory = null, Action<int>? progress = null,
        IArchiveJob? job = null, CancellationToken cancellationToken = default)
    {
        string exe = Exe ?? throw new InvalidOperationException("7-Zip isn't installed (and 7z.exe isn't in the plugin's folder).");
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workingDirectory != null)
            start.WorkingDirectory = workingDirectory;
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        // after the command, before any "--" (after which everything is a file name)
        start.ArgumentList.Insert(Math.Min(1, start.ArgumentList.Count), "-sccUTF-8");

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("7-Zip didn't start.");
        process.StandardInput.Close(); // a question on the console gets "no answer" instead of waiting forever
        CancellationToken ct = job?.Cancellation ?? cancellationToken;
        using CancellationTokenRegistration stop = ct.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // ended already
            }
        });

        // pause: 7-Zip is suspended while the transfer panel pauses the job
        var watcherDone = new ManualResetEventSlim();
        Thread? watcher = null;
        if (job != null)
        {
            watcher = new Thread(() =>
            {
                while (!watcherDone.Wait(200))
                {
                    Task wait = Task.Run(job.WaitWhilePaused);
                    if (wait.Wait(100))
                        continue;
                    try
                    {
                        NtSuspendProcess(process.Handle);
                        try
                        {
                            wait.Wait();
                        }
                        catch (AggregateException)
                        {
                            // cancelled: the registration stops 7-Zip
                        }
                        finally
                        {
                            NtResumeProcess(process.Handle);
                        }
                    }
                    catch (Exception)
                    {
                        // the process ended
                    }
                }
            }) { IsBackground = true, Name = "7-Zip pause" };
            watcher.Start();
        }

        Task<string> errors = process.StandardError.ReadToEndAsync();
        var output = new StringBuilder();
        var segment = new StringBuilder();
        char[] buffer = new char[4096];
        int read;
        while ((read = process.StandardOutput.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                char c = buffer[i];
                if (c is '\b' or '\r' or '\n')
                {
                    // progress lines are overwritten with backspaces: each piece may hold "45%"
                    if (segment.Length > 0 && progress != null && Percent.Match(segment.ToString()) is { Success: true } m)
                        progress(Math.Clamp(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), 0, 100));
                    segment.Clear();
                    if (c == '\n')
                        output.Append('\n');
                    continue;
                }
                segment.Append(c);
                output.Append(c);
            }
        }
        process.WaitForExit();
        watcherDone.Set();
        watcher?.Join(1000);
        ct.ThrowIfCancellationRequested();
        return new SevenZipResult(process.ExitCode, output.ToString(), errors.GetAwaiter().GetResult());
    }

    /// <summary>The password switch: the password, or <see cref="NoPassword"/>.</summary>
    public static string PasswordSwitch(string? password) => string.IsNullOrEmpty(password) ? NoPassword : "-p" + password;

    /// <summary>
    /// "7z l -slt": the archive's own properties (Type, Method, Solid, ...) and its items. Throws with 7-Zip's message
    /// when it can't be read; <see cref="PasswordNeededException"/> when the names are encrypted.
    /// </summary>
    public static (Dictionary<string, string> Archive, List<SevenZipItem> Items) List(string archivePath, string? password, CancellationToken ct)
    {
        SevenZipResult result = Run(new[] { "l", "-slt", "-bsp0", PasswordSwitch(password), "--", archivePath }, cancellationToken: ct);
        if (!result.Ok)
        {
            if (result.WrongPassword)
                throw new PasswordNeededException();
            throw new InvalidDataException(result.Problem);
        }
        return ParseListing(result.Output);
    }

    /// <summary>Parses "7z l -slt" (the part before "----------" describes the archive, then one block per item).</summary>
    internal static (Dictionary<string, string> Archive, List<SevenZipItem> Items) ParseListing(string output)
    {
        var archive = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<SevenZipItem>();
        string[] lines = output.Replace("\r", string.Empty).Split('\n');
        int i = 0;
        // the archive's properties start after "--"
        while (i < lines.Length && lines[i].Trim() != "--" && lines[i].Trim() != "----------")
            i++;
        if (i < lines.Length && lines[i].Trim() == "--")
        {
            for (i++; i < lines.Length && lines[i].Trim() != "----------"; i++)
                if (Split(lines[i]) is { } property)
                    archive[property.Key] = property.Value;
        }
        var block = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Flush()
        {
            if (block.TryGetValue("Path", out string? path) && path.Length > 0)
            {
                bool directory = block.TryGetValue("Folder", out string? folder) ? folder == "+" :
                    block.TryGetValue("Attributes", out string? attributes) && attributes.StartsWith('D');
                long size = block.TryGetValue("Size", out string? s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : 0;
                DateTime? modified = block.TryGetValue("Modified", out string? m) &&
                    DateTime.TryParse(m, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime t) ? t : null;
                bool encrypted = block.TryGetValue("Encrypted", out string? e) && e == "+";
                uint? crc = block.TryGetValue("CRC", out string? c) && uint.TryParse(c, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v) ? v : null;
                items.Add(new SevenZipItem(path.Replace('\\', '/').Trim('/'), directory, size, modified, encrypted, crc));
            }
            block.Clear();
        }
        for (; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0)
            {
                Flush();
                continue;
            }
            if (Split(lines[i]) is { } property)
                block[property.Key] = property.Value;
        }
        Flush();
        return (archive, items);
    }

    private static KeyValuePair<string, string>? Split(string line)
    {
        int equals = line.IndexOf(" = ", StringComparison.Ordinal);
        if (equals > 0)
            return new KeyValuePair<string, string>(line[..equals].Trim(), line[(equals + 3)..].Trim());
        return line.EndsWith(" =", StringComparison.Ordinal) ? new KeyValuePair<string, string>(line[..^2].Trim(), string.Empty) : null;
    }
}

internal sealed class PasswordNeededException : Exception
{
}
