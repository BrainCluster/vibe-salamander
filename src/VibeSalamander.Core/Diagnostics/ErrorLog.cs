// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace VibeSalamander.Core.Diagnostics;

/// <summary>
/// The error log (Help → Error Log): one text file a day in %LocalAppData%\VibeSalamander\Logs with the errors the
/// program met: crashes with their details, errors it showed, failed file operations, plugins that failed. It stays on
/// this computer; it is there to be read, or sent along with a bug report. Logs older than 30 days are deleted.
/// A day's file stops growing at 5 MB (an operation with thousands of errors doesn't fill the disk).
/// </summary>
public static class ErrorLog
{
    public const int KeepDays = 30;
    public const long MaxFileBytes = 5L * 1024 * 1024;

    private const string CrashMarker = "last-crash.txt";

    private static readonly object Gate = new();
    private static string? _folder;
    private static string _version = "?";
    private static bool _headerWritten;
    private static string? _fullDay;

    // the same error again and again (a broken plugin asked for every file, Skip All over thousands of files):
    // written once, then counted
    private static string? _lastEntry;
    private static DateTime _lastEntryAt;
    private static int _repeats;

    /// <summary>The folder of the logs.</summary>
    public static string Folder => _folder ??= Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeSalamander", "Logs");

    /// <summary>At start: the program's version for the session header; old logs are deleted.</summary>
    /// <param name="folder">Another folder than the usual one (tests).</param>
    public static void Start(string version, string? folder = null)
    {
        lock (Gate)
        {
            _version = version;
            if (folder != null)
                _folder = folder;
            _headerWritten = false;
            _fullDay = null;
            _lastEntry = null;
            _repeats = 0;
        }
        try
        {
            if (!Directory.Exists(Folder))
                return;
            DateTime cutoff = DateTime.Now.AddDays(-KeepDays);
            foreach (string file in Directory.EnumerateFiles(Folder, "errors-*.log"))
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // only housekeeping
        }
    }

    /// <summary>Today's log file (it may not exist yet: nothing went wrong today).</summary>
    public static string TodaysFile => FileFor(DateTime.Now);

    private static string FileFor(DateTime day) => Path.Combine(Folder, $"errors-{day:yyyy-MM-dd}.log");

    /// <summary>The newest log file, or null when there is none.</summary>
    public static string? NewestFile()
    {
        try
        {
            return Directory.Exists(Folder)
                ? Directory.EnumerateFiles(Folder, "errors-*.log").OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>An error the user was told about, or one handled without telling (where: "Copy", "Plugin 7-Zip", ...).</summary>
    public static void Error(string where, string message) => Write("ERROR", where, message);

    /// <summary>An exception that was caught and handled: its type, message and stack.</summary>
    public static void Exception(string where, Exception exception) => Write("ERROR", where, exception.ToString());

    /// <summary>An exception nothing handled: the program ends. The next start says so (<see cref="TakeCrashNotice"/>).</summary>
    public static void Crash(string where, Exception? exception)
    {
        Write("CRASH", where, exception?.ToString() ?? "(no details)");
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, CrashMarker), DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the log entry is what matters
        }
    }

    /// <summary>True (once) when the program crashed the last time it ran.</summary>
    public static bool TakeCrashNotice()
    {
        try
        {
            string marker = Path.Combine(Folder, CrashMarker);
            if (!File.Exists(marker))
                return false;
            File.Delete(marker);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void Write(string kind, string where, string message)
    {
        lock (Gate)
        {
            try
            {
                DateTime now = DateTime.Now;
                string entry = kind + "|" + where + "|" + message;
                if (entry == _lastEntry && now - _lastEntryAt < TimeSpan.FromMinutes(1))
                {
                    _repeats++;
                    _lastEntryAt = now;
                    return;
                }
                string file = FileFor(now);
                string day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (_fullDay == day)
                    return;
                Directory.CreateDirectory(Folder);
                long size = File.Exists(file) ? new FileInfo(file).Length : 0;
                var text = new StringBuilder();
                if (_repeats > 0)
                {
                    text.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                        .Append("  NOTE   The error before this one happened ").Append(_repeats.ToString(CultureInfo.InvariantCulture))
                        .Append(" more times.").AppendLine();
                    _repeats = 0;
                }
                _lastEntry = entry;
                _lastEntryAt = now;
                if (size >= MaxFileBytes)
                {
                    _fullDay = day;
                    text.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                        .Append("  NOTE   The log is full for today: further errors aren't written.").AppendLine();
                    File.AppendAllText(file, text.ToString(), Encoding.UTF8);
                    return;
                }
                if (!_headerWritten || size == 0)
                {
                    // once per session (and at the top of each day's file): what was running
                    _headerWritten = true;
                    text.AppendLine()
                        .Append("==== Vibe Salamander ").Append(_version)
                        .Append(" (").Append(RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()).Append(")")
                        .Append(", ").Append(RuntimeInformation.OSDescription.Trim())
                        .Append(", ").Append(RuntimeInformation.FrameworkDescription)
                        .Append(", process ").Append(Environment.ProcessId.ToString(CultureInfo.InvariantCulture))
                        .Append(" ====").AppendLine();
                }
                text.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                    .Append("  ").Append(kind.PadRight(5)).Append("  ").Append(where).Append(": ")
                    .Append(message.Replace("\r\n", "\n").Replace("\n", Environment.NewLine + "        ")).AppendLine();
                File.AppendAllText(file, text.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a log that can't be written must never cause another error
            }
        }
    }
}
