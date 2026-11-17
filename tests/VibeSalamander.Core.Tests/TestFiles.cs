// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using VibeSalamander.Core.Operations;

namespace VibeSalamander.Core.Tests;

/// <summary>What the tests of file operations share: links, and deleting a test folder that has them.</summary>
internal static class TestFiles
{
    /// <summary>A directory junction (mklink /J needs no administrator rights, unlike symbolic links).</summary>
    public static void Junction(string link, string target)
    {
        Mklink("/J", link, target);
        Assert.True(Directory.Exists(link), "the junction could not be created");
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
    }

    /// <summary>A second name for the same file (mklink /H needs no administrator rights).</summary>
    public static void HardLink(string link, string target)
    {
        Mklink("/H", link, target);
        Assert.True(File.Exists(link), "the hard link could not be created");
    }

    private static void Mklink(string kind, string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/c mklink {kind} \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process process = Process.Start(start)!;
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"mklink {kind} failed: {error}");
    }

    /// <summary>Deletes a test folder: links are removed without going into them, read-only files are deleted too.</summary>
    public static void DeleteTree(string directory)
    {
        var all = new EnumerationOptions { AttributesToSkip = 0 };
        foreach (string dir in Directory.GetDirectories(directory, "*", all))
        {
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(dir);
            else
                DeleteTree(dir);
        }
        foreach (string file in Directory.GetFiles(directory, "*", all))
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }
        Directory.Delete(directory);
    }
}

/// <summary>Answers the copy engine's questions through functions and counts them.</summary>
internal sealed class OperationAnswers : IOperationCallbacks
{
    public Func<ConflictInfo, ConflictChoice> Conflict { get; init; } = _ => ConflictChoice.Overwrite;
    public Func<OperationError, ErrorChoice> Error { get; init; } = _ => ErrorChoice.Skip;
    public List<OperationError> Errors { get; } = new();
    public int Conflicts { get; private set; }

    public Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict)
    {
        Conflicts++;
        return Task.FromResult(Conflict(conflict));
    }

    public Task<ErrorChoice> ResolveErrorAsync(OperationError error)
    {
        Errors.Add(error);
        return Task.FromResult(Error(error));
    }

    /// <summary>The question before the work starts ("not enough space: copy anyway?"): yes unless set.</summary>
    public Func<string, bool> Confirm { get; init; } = _ => true;
    public List<string> Confirmations { get; } = new();

    public Task<bool> ConfirmAsync(string title, string message)
    {
        Confirmations.Add(message);
        return Task.FromResult(Confirm(message));
    }
}

/// <summary>Reports progress synchronously (Progress&lt;T&gt; would post to a thread pool and arrive late).</summary>
internal sealed class ImmediateProgress(Action<OperationProgress> report) : IProgress<OperationProgress>
{
    public void Report(OperationProgress value) => report(value);
}
