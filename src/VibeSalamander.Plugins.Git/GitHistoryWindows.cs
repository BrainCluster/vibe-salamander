// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using LibGit2Sharp;

namespace VibeSalamander.Plugins.Git;

/// <summary>What the history windows share: a commit's menu (browse it, its files, copy its id).</summary>
internal abstract class GitCommitWindowBase
{
    protected GitCommitWindowBase(GitPlugin plugin, IPluginHost host, string root)
    {
        Plugin = plugin;
        Host = host;
        Root = root;
    }

    protected GitPlugin Plugin { get; }
    protected IPluginHost Host { get; }
    protected string Root { get; }
    protected IPluginListWindow? Window { get; set; }

    protected string T(string text) => Plugin.T(text);

    protected static string When(DateTimeOffset when) =>
        when == default ? string.Empty : when.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);

    /// <summary>The commit's whole tree in the active panel, to look at and copy from.</summary>
    protected async Task BrowseAsync(string sha)
    {
        if (sha.Length == 0)
            return;
        await Host.ActivePanel.NavigateAsync(GitVersionFileSystem.PathOf(Root, sha));
    }

    protected Task FilesAsync(string sha) =>
        sha.Length == 0 ? Task.CompletedTask : new GitCommitFilesWindow(Plugin, Host, Root, sha).ShowAsync();

    protected IReadOnlyList<PluginMenuItem> CommitMenu(string sha, string? message = null)
    {
        var menu = new List<PluginMenuItem>
        {
            new(T("Browse This Version"), null, () => _ = BrowseAsync(sha), sha.Length > 0),
            new(T("Changed Files..."), null, () => _ = FilesAsync(sha), sha.Length > 0),
            new(T("Copy Commit ID"), null, () => Host.Ui.CopyToClipboard(sha), sha.Length > 0),
        };
        if (message != null)
            menu.Add(new PluginMenuItem(T("Copy Message"), null, () => Host.Ui.CopyToClipboard(message)));
        return menu;
    }
}

/// <summary>History: the commit graph of every branch, or the commits that changed one file.</summary>
internal sealed class GitHistoryWindow : GitCommitWindowBase
{
    private const int Max = 2000;
    private readonly string? _path;

    /// <param name="path">A file's path in the repository ('/' separated), or null for the whole repository.</param>
    public GitHistoryWindow(GitPlugin plugin, IPluginHost host, string root, string? path) : base(plugin, host, root) => _path = path;

    public async Task ShowAsync()
    {
        string title = _path == null ? Path.GetFileName(Root) : _path.Replace('/', '\\');
        Window = Host.Ui.OpenListWindow(T("Git History") + " - " + title, new[]
        {
            new PluginColumn(T("Graph"), _path == null ? 120 : 0, Monospace: true),
            new PluginColumn(T("Message"), 460),
            new PluginColumn(T("Author"), 140),
            new PluginColumn(T("Date"), 140),
            new PluginColumn(T("Commit"), 80, Monospace: true),
        });
        Window.RowMenu = row => row.Tag is GitCommitInfo c ? CommitMenu(c.Sha, c.Message) : Array.Empty<PluginMenuItem>();
        Window.SetButtons(new[]
        {
            new PluginButton("browse", T("Browse This Version"), () => _ = OnSelected(c => BrowseAsync(c.Sha))),
            new PluginButton("files", T("Changed Files..."), () => _ = OnSelected(c => FilesAsync(c.Sha)), IsDefault: true),
        });
        Window.SetStatus(T("Reading..."));
        IReadOnlyList<GitCommitInfo> commits = await Task.Run(() => Plugin.Repositories.Read(Root, r => GitHistory.Read(r, Max, _path)));
        IReadOnlyList<string> graph = _path == null
            ? GitGraph.Lay(commits.Select(c => (c.Sha, c.Parents)).ToList())
            : commits.Select(_ => string.Empty).ToList();
        if (Window.IsClosed)
            return;
        for (int i = 0; i < commits.Count; i++)
        {
            GitCommitInfo c = commits[i];
            string labels = c.Labels.Count == 0 ? string.Empty : "[" + string.Join("] [", c.Labels) + "]  ";
            IPluginRow row = Window.AddRow(new[] { graph[i], labels + c.Message, c.Author, When(c.When), c.ShortSha });
            row.Tag = c;
        }
        Window.SetStatus(commits.Count == 0 ? T("No commits yet.")
            : commits.Count >= Max ? string.Format(T("The newest {0} commits"), Max)
            : string.Format(T("{0} commits"), commits.Count));
    }

    private async Task OnSelected(Func<GitCommitInfo, Task> action)
    {
        if (Window?.SelectedRow?.Tag is GitCommitInfo c)
            await action(c);
        else
            Window?.SetStatus(T("Select a commit first."));
    }
}

/// <summary>The files a commit changed; Compare shows a file before and after it.</summary>
internal sealed class GitCommitFilesWindow : GitCommitWindowBase
{
    private readonly string _sha;

    public GitCommitFilesWindow(GitPlugin plugin, IPluginHost host, string root, string sha) : base(plugin, host, root) => _sha = sha;

    public async Task ShowAsync()
    {
        Window = Host.Ui.OpenListWindow(string.Format(T("Commit {0}"), _sha[..7]) + " - " + Path.GetFileName(Root), new[]
        {
            new PluginColumn(T("Change"), 110),
            new PluginColumn(T("File"), 520),
        });
        Window.RowMenu = row => row.Tag is GitCommitFile f ? new[]
        {
            new PluginMenuItem(T("Compare"), null, () => _ = CompareAsync(f)),
            new PluginMenuItem(T("Browse This Version"), null, () => _ = BrowseAsync(_sha)),
        } : Array.Empty<PluginMenuItem>();
        Window.SetButtons(new[]
        {
            new PluginButton("compare", T("Compare"), () => _ = Window?.SelectedRow?.Tag is GitCommitFile f ? CompareAsync(f) : Task.CompletedTask, IsDefault: true),
            new PluginButton("browse", T("Browse This Version"), () => _ = BrowseAsync(_sha)),
        });
        (IReadOnlyList<GitCommitFile> files, string message) = await Task.Run(() => Plugin.Repositories.Read(Root, r =>
            (GitHistory.Files(r, _sha), r.Lookup<Commit>(_sha)?.MessageShort ?? string.Empty)));
        foreach (GitCommitFile f in files)
        {
            IPluginRow row = Window.AddRow(new[] { T(f.Change), f.Path.Replace('/', '\\') + (f.OldPath != null ? "  ←  " + f.OldPath.Replace('/', '\\') : string.Empty) });
            row.Tag = f;
        }
        Window.SetStatus(message);
    }

    /// <summary>The file in the commit's parent against the file in the commit (nothing on the side it didn't exist).</summary>
    private async Task CompareAsync(GitCommitFile file)
    {
        string folder = Path.Combine(Path.GetTempPath(), "VibeSalamander", "Git", Guid.NewGuid().ToString("N")[..8]);
        var sides = await Task.Run(() => Plugin.Repositories.Read(Root, r =>
        {
            string? parent = r.Lookup<Commit>(_sha)?.Parents.FirstOrDefault()?.Sha;
            string name = Path.GetFileName(file.Path);
            string before = (parent != null ? GitHistory.WriteVersion(r, parent, file.OldPath ?? file.Path, folder, "before") : null)
                            ?? Empty(folder, "before", name);
            string after = GitHistory.WriteVersion(r, _sha, file.Path, folder, "after") ?? Empty(folder, "after", name);
            return (before, after, name, parent);
        }));
        Host.CompareFiles(sides.before, sides.after,
            $"{sides.name} ({(sides.parent != null ? sides.parent[..7] : T("nothing"))})", $"{sides.name} ({_sha[..7]})");
    }

    private static string Empty(string folder, string label, string name)
    {
        string path = Path.Combine(folder, label, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }
}

/// <summary>Blame: who last changed each line of a file (as in HEAD), and in which commit.</summary>
internal sealed class GitBlameWindow : GitCommitWindowBase
{
    private readonly string _path;

    public GitBlameWindow(GitPlugin plugin, IPluginHost host, string root, string path) : base(plugin, host, root) => _path = path;

    public async Task ShowAsync()
    {
        IReadOnlyList<GitBlameLine>? lines = await Task.Run(() => Plugin.Repositories.Read(Root, r => GitHistory.Blame(r, _path)));
        if (lines == null)
        {
            Host.Notify(string.Format(T("\"{0}\" isn't in the last commit yet: there is nothing to blame."), _path), isError: true);
            return;
        }
        Window = Host.Ui.OpenListWindow(T("Git Blame") + " - " + _path.Replace('/', '\\'), new[]
        {
            new PluginColumn(T("Commit"), 76, Monospace: true),
            new PluginColumn(T("Author"), 130),
            new PluginColumn(T("Date"), 130),
            new PluginColumn(T("Line"), 56, AlignRight: true, Monospace: true),
            new PluginColumn(T("Text"), 620, Monospace: true),
        });
        Window.RowMenu = row => row.Tag is GitBlameLine l ? CommitMenu(l.Sha) : Array.Empty<PluginMenuItem>();
        Window.SetButtons(new[]
        {
            new PluginButton("files", T("Changed Files..."), () => _ = Window?.SelectedRow?.Tag is GitBlameLine l ? FilesAsync(l.Sha) : Task.CompletedTask, IsDefault: true),
        });
        foreach (GitBlameLine line in lines)
        {
            // who and when on the first line of each run of lines from the same commit
            IPluginRow row = Window.AddRow(new[]
            {
                line.FirstOfHunk && line.Sha.Length > 0 ? line.Sha[..7] : string.Empty,
                line.FirstOfHunk ? line.Author : string.Empty,
                line.FirstOfHunk ? When(line.When) : string.Empty,
                line.Number.ToString(CultureInfo.InvariantCulture),
                line.Text.Replace("\t", "    "),
            });
            row.Tag = line;
        }
        Window.SetStatus(string.Format(T("{0} lines, {1} commits"), lines.Count, lines.Select(l => l.Sha).Distinct().Count()));
    }
}
