// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Text;

namespace VibeSalamander.Plugins.Git;

/// <summary>What git.exe answered.</summary>
internal sealed record GitResult(int ExitCode, string Output, string Error)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>What to tell the user when it failed: git's own message.</summary>
    public string Message => (Error.Trim().Length > 0 ? Error : Output).Trim();
}

/// <summary>
/// The user's own Git (git.exe), started by its full path - never looked up in the repository's folder, so a
/// repository can't put a git.exe of its own in the way. It runs what changes a repository or talks to a server:
/// stage, unstage, discard, commit, fetch, pull, push, clone.
/// </summary>
internal static class GitProgram
{
    /// <summary>The git.exe to use: the one set in the plugin's settings, else the first on the PATH, else Git for Windows' usual places.</summary>
    public static string? Find(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured) && File.Exists(configured))
            return configured;
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string dir = Environment.ExpandEnvironmentVariables(folder.Trim().Trim('"'));
                if (!Path.IsPathFullyQualified(dir))
                    continue; // a relative PATH entry would mean the current folder: never
                string candidate = Path.Combine(dir, "git.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // a malformed PATH entry
            }
        }
        foreach (string root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        })
        {
            if (root.Length == 0)
                continue;
            string candidate = Path.Combine(root, "Git", "cmd", "git.exe");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// Runs git with the arguments (each passed as is, no shell) in the folder. Its progress lines (stderr, with
    /// --progress) go to <paramref name="progress"/>; cancelling ends git and what it started.
    /// </summary>
    public static async Task<GitResult> RunAsync(string git, string folder, IReadOnlyList<string> arguments,
        CancellationToken cancellation = default, string? input = null, Action<string>? progress = null)
    {
        var start = new ProcessStartInfo(git)
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input != null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            // a commit message in any language: git reads UTF-8 (no byte order mark, or it would be in the message)
            StandardInputEncoding = input != null ? new UTF8Encoding(false) : null,
        };
        // quoted paths and messages as they are (UTF-8, no \ooo escapes); never wait for a password on a console
        // nobody sees (a credential manager still asks in its own window)
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.quotepath=false");
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_PAGER"] = string.Empty;
        start.Environment["GIT_EDITOR"] = "true"; // a commit or merge never waits in an editor nobody sees
        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.Start();
        if (input != null)
        {
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
        }
        Task readOut = Task.Run(async () =>
        {
            char[] buffer = new char[4096];
            int read;
            while ((read = await process.StandardOutput.ReadAsync(buffer)) > 0)
                lock (output)
                    output.Append(buffer, 0, read);
        });
        Task readErr = Task.Run(async () =>
        {
            // progress comes on stderr as lines ending in \r (updated in place) or \n
            var line = new StringBuilder();
            char[] buffer = new char[1024];
            int read;
            while ((read = await process.StandardError.ReadAsync(buffer)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    char c = buffer[i];
                    if (c is '\r' or '\n')
                    {
                        if (line.Length > 0)
                        {
                            string text = line.ToString();
                            progress?.Invoke(text);
                            if (c == '\n')
                                lock (error)
                                    error.AppendLine(text);
                            line.Clear();
                        }
                    }
                    else
                    {
                        line.Append(c);
                    }
                }
            }
            if (line.Length > 0)
                lock (error)
                    error.AppendLine(line.ToString());
        });
        try
        {
            await process.WaitForExitAsync(cancellation);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // ended meanwhile
            }
            throw;
        }
        await Task.WhenAll(readOut, readErr);
        return new GitResult(process.ExitCode, output.ToString(), error.ToString());
    }
}
