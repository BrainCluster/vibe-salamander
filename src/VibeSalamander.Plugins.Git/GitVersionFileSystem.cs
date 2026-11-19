// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using LibGit2Sharp;
using VibeSalamander.Core.Operations;

namespace VibeSalamander.Plugins.Git;

/// <summary>
/// Browse this version: a commit's whole tree in a panel, read-only, to look at and copy files out of (F5). Paths:
/// git-version://&lt;commit&gt;/&lt;the repository's folder, escaped&gt;/&lt;folder in the commit&gt;. Going up from its top
/// returns to the repository's folder on disk.
/// </summary>
internal sealed class GitVersionFileSystem : IPluginFileSystem
{
    public const string Scheme = "git-version://";

    private readonly GitPlugin _plugin;

    public GitVersionFileSystem(GitPlugin plugin) => _plugin = plugin;

    public string DisplayName => "Git versions";

    /// <summary>The path of a folder of a commit ("" = its top).</summary>
    public static string PathOf(string root, string sha, string inner = "") =>
        Scheme + sha + "/" + Uri.EscapeDataString(root.TrimEnd('\\')) + "/" + inner.Trim('/');

    /// <summary>(repository folder, commit, folder in the commit with '/'), or null: not one of these paths.</summary>
    internal static (string Root, string Sha, string Inner)? Parse(string path)
    {
        if (!path.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
            return null;
        string rest = path[Scheme.Length..];
        int slash = rest.IndexOf('/');
        if (slash <= 0)
            return null;
        string sha = rest[..slash];
        rest = rest[(slash + 1)..];
        slash = rest.IndexOf('/');
        string root = Uri.UnescapeDataString(slash < 0 ? rest : rest[..slash]);
        string inner = slash < 0 ? string.Empty : rest[(slash + 1)..].Trim('/');
        return root.Length == 0 || sha.Length < 4 ? null : (root, sha, inner);
    }

    public bool CanHandle(string path) => Parse(path) != null;

    public bool IsConnected(string path) => false; // nothing to disconnect: no "Disconnect?" when leaving

    public Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken cancellationToken)
    {
        (string root, string sha, string inner) = Parse(path) ?? throw new IOException("Not a Git version path: " + path);
        return Task.Run(() => _plugin.Repositories.Read(root, repository =>
        {
            Commit commit = repository.Lookup<Commit>(sha) ?? throw new IOException($"The commit {sha} isn't in this repository.");
            Tree tree = inner.Length == 0 ? commit.Tree
                : commit[inner]?.Target as Tree ?? throw new IOException($"\"{inner}\" isn't a folder in commit {sha[..Math.Min(7, sha.Length)]}.");
            DateTime when = commit.Committer.When.LocalDateTime;
            var items = new List<RemoteItem>();
            foreach (TreeEntry entry in tree)
            {
                bool isDirectory = entry.TargetType == TreeEntryTargetType.Tree;
                long size = entry.Target is Blob blob ? blob.Size : 0;
                items.Add(new RemoteItem(entry.Name, Combine(path, entry.Name), isDirectory, size, when,
                    IsHidden: entry.Name.StartsWith('.'), IsLink: entry.Mode == Mode.SymbolicLink)
                {
                    AttributesText = entry.Mode switch
                    {
                        Mode.ExecutableFile => "executable",
                        Mode.SymbolicLink => "link",
                        Mode.GitLink => "submodule",
                        _ => null,
                    },
                });
            }
            return (IReadOnlyList<RemoteItem>)items;
        }), cancellationToken);
    }

    public string? GetParent(string path)
    {
        if (Parse(path) is not (string root, string sha, string inner))
            return null;
        if (inner.Length == 0)
            return root; // up from the commit's top: the repository's folder on disk
        int slash = inner.LastIndexOf('/');
        return PathOf(root, sha, slash < 0 ? string.Empty : inner[..slash]);
    }

    public string GetDisplayPath(string path)
    {
        if (Parse(path) is not (string root, string sha, string inner))
            return path;
        return $"{root} @ {sha[..Math.Min(7, sha.Length)]}" + (inner.Length > 0 ? "\\" + inner.Replace('/', '\\') : string.Empty);
    }

    public string Combine(string directory, string name) => directory.TrimEnd('/') + "/" + name;

    /// <summary>Copies files and folders out of the commit (as checked out: line endings converted).</summary>
    public Task<OperationResult> DownloadAsync(IReadOnlyList<RemoteItem> items, string targetDirectory, IArchiveJob job) => Task.Run(() =>
    {
        var result = new OperationResult();
        if (items.Count == 0 || Parse(items[0].Path) is not (string root, string sha, _))
            return result;
        ConflictChoice? forAll = null;
        _plugin.Repositories.Read(root, repository =>
        {
            Commit commit = repository.Lookup<Commit>(sha) ?? throw new IOException($"The commit {sha} isn't in this repository.");
            var files = new List<(string Inner, string Target, Blob Blob)>();
            void Collect(string inner, string target)
            {
                TreeEntry? entry = commit[inner];
                if (entry?.Target is Tree tree)
                {
                    Directory.CreateDirectory(target);
                    foreach (TreeEntry child in tree)
                        Collect(inner + "/" + child.Name, Path.Combine(target, child.Name));
                }
                else if (entry?.Target is Blob blob)
                {
                    files.Add((inner, target, blob));
                }
            }
            foreach (RemoteItem item in items)
                if (Parse(item.Path) is (_, _, string inner))
                    Collect(inner, Path.Combine(targetDirectory, item.Name));
            var tracker = new ProgressTracker(OperationKind.Copy, files.Sum(f => f.Blob.Size), files.Count);
            foreach ((string inner, string target, Blob blob) in files)
            {
                job.Cancellation.ThrowIfCancellationRequested();
                job.WaitWhilePaused();
                tracker.Start(inner);
                tracker.ReportTo(job);
                string destination = target;
                if (File.Exists(destination))
                {
                    var existing = new FileInfo(destination);
                    ConflictChoice choice = forAll ?? job.ResolveConflictAsync(new ConflictInfo(inner, blob.Size, commit.Committer.When.LocalDateTime,
                        destination, existing.Length, existing.LastWriteTime, IsDirectory: false, existing.Attributes)).GetAwaiter().GetResult();
                    switch (choice)
                    {
                        case ConflictChoice.Cancel:
                            throw new OperationCanceledException();
                        case ConflictChoice.SkipAll:
                            forAll = ConflictChoice.Skip;
                            goto case ConflictChoice.Skip;
                        case ConflictChoice.Skip:
                            result.Skipped++;
                            tracker.AddBytes(blob.Size);
                            tracker.ItemDone();
                            continue;
                        case ConflictChoice.RenameAll:
                            forAll = ConflictChoice.Rename;
                            goto case ConflictChoice.Rename;
                        case ConflictChoice.Rename:
                            destination = FileOperationEngine.UniqueName(destination);
                            break;
                        case ConflictChoice.OverwriteAll or ConflictChoice.ResumeAll:
                            forAll = ConflictChoice.Overwrite;
                            break;
                    }
                    if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(destination, File.GetAttributes(destination) & ~FileAttributes.ReadOnly);
                }
                using (Stream content = blob.GetContentStream(new FilteringOptions(inner)))
                using (FileStream file = File.Create(destination))
                    content.CopyTo(file);
                File.SetLastWriteTime(destination, commit.Committer.When.LocalDateTime);
                tracker.AddBytes(blob.Size);
                tracker.ItemDone();
                result.Succeeded++;
                tracker.ReportTo(job, force: true);
            }
            return true;
        });
        return result;
    });

    private static Exception ReadOnly() => new NotSupportedException("A commit's files can't be changed: copy them out (F5) and change the copies.");

    public Task<OperationResult> UploadAsync(IReadOnlyList<string> sources, string targetDirectory, IArchiveJob job) => throw ReadOnly();
    public Task<OperationResult> DeleteAsync(IReadOnlyList<RemoteItem> items, IArchiveJob job) => throw ReadOnly();
    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) => throw ReadOnly();
    public Task RenameAsync(string path, string newPath, CancellationToken cancellationToken) => throw ReadOnly();
}
