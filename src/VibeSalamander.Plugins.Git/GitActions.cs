// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Plugins.Git;

/// <summary>
/// The git.exe command lines for what the changes window does. Paths are literal (--literal-pathspecs: a name like
/// ":(top)" or "*.cs" is a file, not a pattern) and come after "--" (a name starting with "-" is a file too).
/// </summary>
internal static class GitActions
{
    private const string Literal = "--literal-pathspecs";

    public static string[] Stage(GitChange change) => new[] { Literal, "add", "-A", "--", change.Path };

    public static string[] StageAll() => new[] { "add", "-A" };

    public static string[] Unstage(GitChange change, bool noCommitsYet)
    {
        if (noCommitsYet)
            return new[] { Literal, "rm", "--cached", "-q", "--", change.Path };
        return change.OldPath != null
            ? new[] { Literal, "restore", "--staged", "--", change.Path, change.OldPath }
            : new[] { Literal, "restore", "--staged", "--", change.Path };
    }

    /// <summary>Back to the staged (or last committed) version; null: not by git (an untracked file goes to the Recycle Bin).</summary>
    public static string[]? Discard(GitChange change) =>
        change.Area == ChangeArea.Changes ? new[] { Literal, "restore", "--worktree", "--", change.Path } : null;

    /// <summary>The message comes on standard input (any text, any length, no quoting).</summary>
    public static string[] Commit(bool amend) =>
        amend ? new[] { "commit", "--amend", "-F", "-" } : new[] { "commit", "-F", "-" };
}
