// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using LibGit2Sharp;

namespace VibeSalamander.Plugins.Git;

/// <summary>What Git says about an item in a panel.</summary>
internal enum GitItemState
{
    Clean,
    /// <summary>Changed, not staged.</summary>
    Modified,
    /// <summary>Staged, then changed again.</summary>
    PartlyStaged,
    /// <summary>Changes staged for the next commit.</summary>
    Staged,
    /// <summary>A new file, staged.</summary>
    Added,
    Renamed,
    /// <summary>Not in Git yet (untracked).</summary>
    New,
    Conflict,
    Ignored,
    /// <summary>A folder with changes inside.</summary>
    ChangesInside,
}

/// <summary>A folder's Git state: the branch line and each item's state (by name; clean items aren't listed).</summary>
internal sealed record GitFolderState(string Branch, string BranchToolTip, IReadOnlyDictionary<string, GitItemState> Items, GitItemState Others);

/// <summary>Reads a folder's Git state from a repository (libgit2: no program runs).</summary>
internal static class GitStatusReader
{
    public static GitFolderState Read(Repository repository, string folder, CancellationToken cancellation = default)
    {
        string root = repository.Info.WorkingDirectory.TrimEnd('\\');
        string relative = Path.GetRelativePath(root, folder).Replace('\\', '/');
        if (relative == ".")
            relative = string.Empty;
        string prefix = relative.Length == 0 ? string.Empty : relative.TrimEnd('/') + "/";

        var options = new StatusOptions
        {
            Show = StatusShowOption.IndexAndWorkDir,
            IncludeUntracked = true,
            RecurseUntrackedDirs = false, // an untracked folder is one entry ("dir/")
            IncludeIgnored = true,
            RecurseIgnoredDirs = false,
            DetectRenamesInIndex = true,
            DetectRenamesInWorkDir = false,
            ExcludeSubmodules = false,
        };
        if (prefix.Length > 0)
            options.PathSpec = new[] { prefix };
        RepositoryStatus status = repository.RetrieveStatus(options);
        cancellation.ThrowIfCancellationRequested();

        var items = new Dictionary<string, GitItemState>(StringComparer.OrdinalIgnoreCase);
        GitItemState others = GitItemState.Clean;
        foreach (StatusEntry entry in status)
        {
            string path = entry.FilePath;
            if (prefix.Length > 0 && string.Equals(path.TrimEnd('/') + "/", prefix, StringComparison.OrdinalIgnoreCase))
            {
                // the folder shown is itself untracked or ignored: so is everything in it
                others = StateOf(entry.State) is GitItemState.New or GitItemState.Ignored ? StateOf(entry.State) : others;
                continue;
            }
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            string rest = path[prefix.Length..];
            int slash = rest.IndexOf('/');
            GitItemState state = StateOf(entry.State);
            if (state == GitItemState.Clean)
                continue;
            if (slash >= 0 && slash < rest.Length - 1)
            {
                // something inside a folder of this one: the folder shows it
                string name = rest[..slash];
                GitItemState inside = state switch
                {
                    GitItemState.Conflict => GitItemState.Conflict,
                    GitItemState.Ignored => GitItemState.Clean, // an ignored file in a folder doesn't make the folder ignored
                    GitItemState.New => GitItemState.New,
                    _ => GitItemState.ChangesInside,
                };
                if (inside != GitItemState.Clean)
                    items[name] = Stronger(items.GetValueOrDefault(name), inside);
                continue;
            }
            string own = rest.TrimEnd('/');
            if (own.Length > 0)
                items[own] = Stronger(items.GetValueOrDefault(own), state);
        }
        (string branch, string tip) = BranchOf(repository);
        return new GitFolderState(branch, tip, items, others);
    }

    /// <summary>One status entry's state.</summary>
    internal static GitItemState StateOf(FileStatus s)
    {
        if ((s & FileStatus.Conflicted) != 0)
            return GitItemState.Conflict;
        if ((s & FileStatus.Ignored) != 0)
            return GitItemState.Ignored;
        bool inWorkdir = (s & (FileStatus.ModifiedInWorkdir | FileStatus.TypeChangeInWorkdir | FileStatus.DeletedFromWorkdir | FileStatus.RenamedInWorkdir)) != 0;
        bool inIndex = (s & (FileStatus.ModifiedInIndex | FileStatus.TypeChangeInIndex | FileStatus.DeletedFromIndex | FileStatus.RenamedInIndex | FileStatus.NewInIndex)) != 0;
        if ((s & FileStatus.NewInWorkdir) != 0 && !inIndex)
            return GitItemState.New;
        if ((s & FileStatus.NewInIndex) != 0)
            return GitItemState.Added;
        if (inIndex && inWorkdir)
            return GitItemState.PartlyStaged;
        if ((s & FileStatus.RenamedInIndex) != 0)
            return GitItemState.Renamed;
        if (inIndex)
            return GitItemState.Staged;
        if (inWorkdir)
            return GitItemState.Modified;
        return GitItemState.Clean;
    }

    /// <summary>What a folder or file shows when several things apply: the one that needs attention first.</summary>
    internal static GitItemState Stronger(GitItemState a, GitItemState b) => Rank(a) >= Rank(b) ? a : b;

    private static int Rank(GitItemState s) => s switch
    {
        GitItemState.Conflict => 9,
        GitItemState.PartlyStaged => 8,
        GitItemState.Modified => 7,
        GitItemState.ChangesInside => 6,
        GitItemState.Staged => 5,
        GitItemState.Renamed => 5,
        GitItemState.Added => 4,
        GitItemState.New => 3,
        GitItemState.Ignored => 1,
        _ => 0,
    };

    /// <summary>"main ↑2 ↓1" and its tooltip; "detached at 1a2b3c4"; " · merging" while a merge (rebase...) is going on.</summary>
    internal static (string Text, string ToolTip) BranchOf(Repository repository)
    {
        Branch head = repository.Head;
        string text, tip;
        if (repository.Info.IsHeadUnborn)
        {
            text = head.FriendlyName;
            tip = $"Branch {head.FriendlyName}, no commits yet";
        }
        else if (repository.Info.IsHeadDetached)
        {
            string sha = head.Tip?.Sha[..7] ?? "?";
            text = "detached at " + sha;
            tip = $"No branch: HEAD is commit {sha}";
        }
        else
        {
            text = head.FriendlyName;
            tip = "Branch " + head.FriendlyName;
            if (head.IsTracking && head.TrackedBranch != null)
            {
                int ahead = head.TrackingDetails.AheadBy ?? 0, behind = head.TrackingDetails.BehindBy ?? 0;
                if (ahead > 0)
                    text += $" ↑{ahead}";
                if (behind > 0)
                    text += $" ↓{behind}";
                tip += $", tracking {head.TrackedBranch.FriendlyName}: " + (ahead == 0 && behind == 0 ? "up to date"
                    : string.Join(", ", new[]
                    {
                        ahead > 0 ? (ahead == 1 ? "1 commit to push" : $"{ahead} commits to push") : null,
                        behind > 0 ? (behind == 1 ? "1 commit to pull" : $"{behind} commits to pull") : null,
                    }.Where(p => p != null)));
            }
            else
            {
                tip += ", not on a server yet";
            }
        }
        string? doing = repository.Info.CurrentOperation switch
        {
            CurrentOperation.Merge => "merging",
            CurrentOperation.Rebase or CurrentOperation.RebaseInteractive or CurrentOperation.RebaseMerge => "rebasing",
            CurrentOperation.CherryPick or CurrentOperation.CherryPickSequence => "cherry-picking",
            CurrentOperation.Revert or CurrentOperation.RevertSequence => "reverting",
            CurrentOperation.Bisect => "bisecting",
            _ => null,
        };
        if (doing != null)
            text += " · " + doing;
        return (text, tip);
    }
}
