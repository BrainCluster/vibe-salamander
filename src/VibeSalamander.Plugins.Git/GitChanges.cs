// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using LibGit2Sharp;

namespace VibeSalamander.Plugins.Git;

/// <summary>Where a change is: staged for the next commit, in the working tree only, not in Git yet, or a conflict.</summary>
internal enum ChangeArea
{
    Staged,
    Changes,
    Untracked,
    Conflict,
}

internal enum ChangeKind
{
    Modified,
    Added,
    Deleted,
    Renamed,
    TypeChanged,
    New,
    Conflict,
}

/// <summary>One row of the changes window: a file (path relative to the root, '/' separated) in one area.</summary>
internal sealed record GitChange(string Path, ChangeArea Area, ChangeKind Kind, string? OldPath = null);

/// <summary>A repository's changes (libgit2: no program runs), and the old versions to compare them with.</summary>
internal static class GitChanges
{
    public static IReadOnlyList<GitChange> Read(Repository repository)
    {
        var options = new StatusOptions
        {
            Show = StatusShowOption.IndexAndWorkDir,
            IncludeUntracked = true,
            RecurseUntrackedDirs = true,
            IncludeIgnored = false,
            DetectRenamesInIndex = true,
        };
        var changes = new List<GitChange>();
        foreach (StatusEntry entry in repository.RetrieveStatus(options))
        {
            FileStatus s = entry.State;
            string path = entry.FilePath;
            if ((s & FileStatus.Conflicted) != 0)
            {
                changes.Add(new GitChange(path, ChangeArea.Conflict, ChangeKind.Conflict));
                continue;
            }
            if ((s & FileStatus.NewInIndex) != 0)
                changes.Add(new GitChange(path, ChangeArea.Staged, ChangeKind.Added));
            else if ((s & FileStatus.RenamedInIndex) != 0)
                changes.Add(new GitChange(path, ChangeArea.Staged, ChangeKind.Renamed, entry.HeadToIndexRenameDetails?.OldFilePath));
            else if ((s & FileStatus.DeletedFromIndex) != 0)
                changes.Add(new GitChange(path, ChangeArea.Staged, ChangeKind.Deleted));
            else if ((s & FileStatus.TypeChangeInIndex) != 0)
                changes.Add(new GitChange(path, ChangeArea.Staged, ChangeKind.TypeChanged));
            else if ((s & FileStatus.ModifiedInIndex) != 0)
                changes.Add(new GitChange(path, ChangeArea.Staged, ChangeKind.Modified));

            if ((s & FileStatus.NewInWorkdir) != 0)
                changes.Add(new GitChange(path, ChangeArea.Untracked, ChangeKind.New));
            else if ((s & FileStatus.DeletedFromWorkdir) != 0)
                changes.Add(new GitChange(path, ChangeArea.Changes, ChangeKind.Deleted));
            else if ((s & FileStatus.TypeChangeInWorkdir) != 0)
                changes.Add(new GitChange(path, ChangeArea.Changes, ChangeKind.TypeChanged));
            else if ((s & FileStatus.ModifiedInWorkdir) != 0)
                changes.Add(new GitChange(path, ChangeArea.Changes, ChangeKind.Modified));
        }
        return changes
            .OrderBy(c => c.Area)
            .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The version to compare a change with, written to a temporary file (as checked out: line endings converted),
    /// and what to call it: the index's for a change not staged, HEAD's for a staged one. Null: there is none (a new
    /// file). The other side: the working file, or for a staged change the index's version.
    /// </summary>
    public static (string Left, string LeftTitle, string Right, string RightTitle)? Sides(Repository repository, GitChange change, string folder)
    {
        string root = repository.Info.WorkingDirectory;
        string name = Path.GetFileName(change.Path);
        string working = Path.Combine(root, change.Path.Replace('/', '\\'));
        switch (change.Area)
        {
            case ChangeArea.Changes:
            {
                string? indexed = WriteIndexVersion(repository, change.Path, folder, "index");
                if (indexed == null)
                    return null;
                return (indexed, $"{name} (staged or last commit)", working, $"{name} (working tree)");
            }
            case ChangeArea.Staged:
            {
                string? head = WriteHeadVersion(repository, change.OldPath ?? change.Path, folder);
                string? indexed = WriteIndexVersion(repository, change.Path, folder, "staged");
                if (indexed == null)
                    return null; // deleted from the index: nothing to compare with
                head ??= WriteText(folder, "HEAD", name, string.Empty); // added: against nothing
                return (head, $"{name} (HEAD)", indexed, $"{name} (staged)");
            }
            default:
                return null;
        }
    }

    private static string? WriteHeadVersion(Repository repository, string path, string folder)
    {
        if (repository.Head.Tip?[path]?.Target is not Blob blob)
            return null;
        return WriteBlob(blob, path, folder, "HEAD");
    }

    private static string? WriteIndexVersion(Repository repository, string path, string folder, string label)
    {
        IndexEntry? entry = repository.Index[path];
        if (entry == null || repository.Lookup<Blob>(entry.Id) is not Blob blob)
            return null;
        return WriteBlob(blob, path, folder, label);
    }

    private static string WriteBlob(Blob blob, string path, string folder, string label)
    {
        string target = Path.Combine(folder, label, Path.GetFileName(path));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using (Stream content = blob.GetContentStream(new FilteringOptions(path)))
        using (FileStream file = File.Create(target))
            content.CopyTo(file);
        File.SetAttributes(target, FileAttributes.ReadOnly); // an old version: not to be edited by mistake
        return target;
    }

    private static string WriteText(string folder, string label, string name, string text)
    {
        string target = Path.Combine(folder, label, name);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, text);
        return target;
    }
}
