// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using LibGit2Sharp;

namespace VibeSalamander.Plugins.Git;

/// <summary>One commit of a history.</summary>
internal sealed record GitCommitInfo(string Sha, IReadOnlyList<string> Parents, string Message, string Author, DateTimeOffset When,
    IReadOnlyList<string> Labels)
{
    public string ShortSha => Sha[..7];
}

/// <summary>A changed file of a commit (against its first parent).</summary>
internal sealed record GitCommitFile(string Path, string Change, string? OldPath);

/// <summary>One line of blame: who last changed it, in which commit.</summary>
internal sealed record GitBlameLine(int Number, string Text, string Sha, string Author, DateTimeOffset When, bool FirstOfHunk);

/// <summary>Histories, commits and blame, read by libgit2 (no program runs).</summary>
internal static class GitHistory
{
    /// <summary>
    /// The newest commits of every branch (local and the remotes'), each before its parents; or, with a path, the
    /// commits that changed that file (following renames).
    /// </summary>
    public static IReadOnlyList<GitCommitInfo> Read(Repository repository, int max, string? path = null)
    {
        if (repository.Info.IsHeadUnborn)
            return Array.Empty<GitCommitInfo>();
        var labels = new Dictionary<string, List<string>>();
        void Label(string? sha, string text)
        {
            if (sha == null)
                return;
            if (!labels.TryGetValue(sha, out List<string>? list))
                labels[sha] = list = new List<string>();
            list.Add(text);
        }
        foreach (Branch branch in repository.Branches)
            if (branch.Tip != null && !branch.FriendlyName.EndsWith("/HEAD", StringComparison.Ordinal))
                Label(branch.Tip.Sha, branch.FriendlyName);
        foreach (Tag tag in repository.Tags)
            Label((tag.PeeledTarget as Commit)?.Sha, "tag " + tag.FriendlyName);
        if (repository.Info.IsHeadDetached)
            Label(repository.Head.Tip?.Sha, "HEAD");

        IEnumerable<Commit> commits;
        if (path != null)
        {
            commits = repository.Commits.QueryBy(path, new CommitFilter { SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time })
                .Select(e => e.Commit);
        }
        else
        {
            var tips = repository.Branches.Where(b => b.Tip != null).Select(b => (object)b.Tip).Append(repository.Head.Tip).ToList();
            commits = repository.Commits.QueryBy(new CommitFilter
            {
                IncludeReachableFrom = tips,
                SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time,
            });
        }
        return commits.Take(max).Select(c => new GitCommitInfo(c.Sha, c.Parents.Select(p => p.Sha).ToList(), c.MessageShort,
            c.Author.Name, c.Author.When, labels.TryGetValue(c.Sha, out List<string>? l) ? l : (IReadOnlyList<string>)Array.Empty<string>())).ToList();
    }

    /// <summary>The files a commit changed against its first parent (all of them for the first commit).</summary>
    public static IReadOnlyList<GitCommitFile> Files(Repository repository, string sha)
    {
        Commit commit = repository.Lookup<Commit>(sha) ?? throw new InvalidOperationException("No commit " + sha);
        Tree? parent = commit.Parents.FirstOrDefault()?.Tree;
        TreeChanges changes = repository.Diff.Compare<TreeChanges>(parent, commit.Tree, new CompareOptions { Similarity = SimilarityOptions.Renames });
        return changes.Select(c => new GitCommitFile(c.Path, c.Status switch
            {
                ChangeKind.Added => "Added",
                ChangeKind.Deleted => "Deleted",
                ChangeKind.Renamed => "Renamed",
                ChangeKind.Copied => "Copied",
                ChangeKind.TypeChanged => "Type changed",
                _ => "Modified",
            }, c.Status is ChangeKind.Renamed or ChangeKind.Copied ? c.OldPath : null))
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A file's version in a commit, written to a temporary file (as checked out); null: not in that commit.</summary>
    public static string? WriteVersion(Repository repository, string sha, string path, string folder, string label)
    {
        if (repository.Lookup<Commit>(sha)?[path]?.Target is not Blob blob)
            return null;
        string target = Path.Combine(folder, label, Path.GetFileName(path));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using (Stream content = blob.GetContentStream(new FilteringOptions(path)))
        using (FileStream file = File.Create(target))
            content.CopyTo(file);
        File.SetAttributes(target, FileAttributes.ReadOnly);
        return target;
    }

    /// <summary>Who last changed each line of a file, as it is in HEAD; null: the file isn't in HEAD.</summary>
    public static IReadOnlyList<GitBlameLine>? Blame(Repository repository, string path)
    {
        if (repository.Info.IsHeadUnborn || repository.Head.Tip[path]?.Target is not Blob blob)
            return null;
        string text;
        using (var reader = new StreamReader(blob.GetContentStream(new FilteringOptions(path))))
            text = reader.ReadToEnd();
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0)
            lines = lines[..^1];
        var result = new GitBlameLine[lines.Length];
        foreach (BlameHunk hunk in repository.Blame(path))
        {
            for (int i = 0; i < hunk.LineCount; i++)
            {
                int index = hunk.FinalStartLineNumber + i; // 0-based in LibGit2Sharp
                if (index < 0 || index >= lines.Length)
                    continue;
                result[index] = new GitBlameLine(index + 1, lines[index], hunk.FinalCommit.Sha, hunk.FinalCommit.Author.Name,
                    hunk.FinalCommit.Author.When, i == 0);
            }
        }
        for (int i = 0; i < result.Length; i++)
            result[i] ??= new GitBlameLine(i + 1, lines[i], string.Empty, string.Empty, default, true);
        return result;
    }
}
