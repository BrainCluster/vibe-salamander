// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.Diagnostics;

namespace VibeSalamander.Core.Tests;

[Collection("ErrorLog")] // the log is one per program: these tests don't run at the same time
public sealed class ErrorLogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "vibe-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Errors_go_to_todays_file_after_a_session_header()
    {
        ErrorLog.Start("1.2.3", _folder);
        Assert.False(File.Exists(ErrorLog.TodaysFile)); // nothing went wrong: no file

        ErrorLog.Error("Copy", @"C:\a.txt: Access is denied.");
        ErrorLog.Exception("Plugin", new InvalidOperationException("broken\nsecond line"));

        string text = File.ReadAllText(ErrorLog.TodaysFile);
        Assert.Contains("==== Vibe Salamander 1.2.3", text);
        Assert.Contains(@"ERROR  Copy: C:\a.txt: Access is denied.", text);
        Assert.Contains("System.InvalidOperationException: broken", text);
        Assert.Contains("        second line", text); // continuation lines are indented
        Assert.Single(text.Split("==== Vibe Salamander").Skip(1)); // one header per session
        Assert.Equal(ErrorLog.TodaysFile, ErrorLog.NewestFile());
    }

    [Fact]
    public void The_same_error_again_is_counted_not_repeated()
    {
        ErrorLog.Start("1.0", _folder);
        for (int i = 0; i < 4; i++)
            ErrorLog.Error("Plugin", "the same");
        ErrorLog.Error("Copy", "another");

        string text = File.ReadAllText(ErrorLog.TodaysFile);
        Assert.Single(text.Split("Plugin: the same").Skip(1));
        Assert.Contains("happened 3 more times", text);
        Assert.Contains("Copy: another", text);
    }

    [Fact]
    public void A_crash_is_noticed_once_at_the_next_start()
    {
        ErrorLog.Start("1.0", _folder);
        Assert.False(ErrorLog.TakeCrashNotice());
        ErrorLog.Crash("Unhandled", new NullReferenceException("boom"));

        ErrorLog.Start("1.0", _folder);
        Assert.True(ErrorLog.TakeCrashNotice());
        Assert.False(ErrorLog.TakeCrashNotice());
        Assert.Contains("CRASH  Unhandled: System.NullReferenceException: boom", File.ReadAllText(ErrorLog.TodaysFile));
    }

    [Fact]
    public void Old_logs_are_deleted_and_a_full_day_stops_growing()
    {
        Directory.CreateDirectory(_folder);
        string old = Path.Combine(_folder, "errors-2020-01-01.log");
        File.WriteAllText(old, "old");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-(ErrorLog.KeepDays + 1)));
        ErrorLog.Start("1.0", _folder);
        Assert.False(File.Exists(old));

        File.WriteAllText(ErrorLog.TodaysFile, new string('x', (int)ErrorLog.MaxFileBytes));
        ErrorLog.Error("Copy", "one");
        ErrorLog.Error("Copy", "two");
        string tail = File.ReadAllText(ErrorLog.TodaysFile)[(int)ErrorLog.MaxFileBytes..];
        Assert.Contains("The log is full for today", tail);
        Assert.DoesNotContain("one", tail);
        Assert.DoesNotContain("two", tail);
    }
}
