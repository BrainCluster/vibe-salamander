// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;

namespace VibeSalamander.Plugins.Git;

/// <summary>
/// The Git plugin (a separate download, like 7-Zip): Git in the panels (the branch with commits to push and to pull,
/// each item colored by its state, a Git column); changes to stage, unstage, discard and commit, compared in the
/// file comparator; history as a commit graph, blame, and any commit's whole tree to browse and copy from; fetch,
/// pull, push and clone as jobs in the queue. Repositories are read inside the program (libgit2), so a repository
/// never makes it run a hook or a program; what changes a repository or talks to a server runs in the user's own
/// git.exe, by its full path.
/// </summary>
public sealed class GitPlugin : IVibePlugin, ICommandContributor
{
    public string Id => "vibe.git";
    public string DisplayName => "Git";
    public Version Version => new(1, 0);
    public string Description => "Git in the panels: branch, states and a Git column; stage, commit, compare; history, blame, browse a version; fetch, pull, push and clone.";
    public string Author => "Vibe Salamander (libgit2 through LibGit2Sharp)";
    public string? IconGlyph => "";

    // Panel decorators, the comparator and plugin jobs came after 0.9.1: the release that ships them sets this to
    // its own version (until then, builds of the branch still say 0.9.1).
    public Version? MinimumHostVersion => null;

    private IPluginHost? _host;
    private GitSettings _settings = new();
    private string? _settingsFile;
    internal GitRepositories Repositories { get; } = new();

    public IReadOnlyList<PluginCommand> Commands { get; private set; } = Array.Empty<PluginCommand>();

    internal string T(string text) => _host?.Ui.T(text) ?? text;

    internal GitSettings Settings => _settings;

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _settingsFile = Path.Combine(host.GetDataDirectory(this), "settings.json");
        _settings = GitSettings.Load(_settingsFile);
        host.RegisterPanelDecorator(new GitDecorator(Repositories, T));
        host.RegisterFileSystem(new GitVersionFileSystem(this));
        Commands = new[]
        {
            new PluginCommand("changes", "Changes...", null, ChangesAsync),
            new PluginCommand("history", "History...", null, HistoryAsync),
            new PluginCommand("fetch", "Fetch", null, FetchAsync, SeparatorBefore: true),
            new PluginCommand("pull", "Pull", null, PullAsync),
            new PluginCommand("push", "Push", null, PushAsync),
            new PluginCommand("clone", "Clone...", null, CloneAsync),
            // these two also in the right-click menu of a file in a repository
            new PluginCommand("file-history", "Git History of This File...", null, FileHistoryAsync, AppliesTo: IsInRepository, SeparatorBefore: true),
            new PluginCommand("blame", "Git Blame...", null, BlameAsync, AppliesTo: IsInRepository),
        };
        host.RegisterCommands(this);
    }

    public void Shutdown() => Repositories.Dispose();

    // ---- settings page ------------------------------------------------------------------------------------

    public IReadOnlyList<PluginField>? GetSettings() => new[]
    {
        new PluginField
        {
            Label = T("Git program (git.exe); empty: the one on the PATH"),
            Value = _settings.GitPath,
            Placeholder = GitProgram.Find(null) ?? T("Git isn't installed"),
        },
        new PluginField { Label = T("Clone into (empty: the active panel's folder)"), Value = _settings.CloneFolder },
    };

    public void ApplySettings(IReadOnlyList<PluginField> fields)
    {
        _settings.GitPath = fields[0].Value.Trim().Trim('"');
        _settings.CloneFolder = fields[1].Value.Trim().Trim('"');
        if (_settingsFile != null)
            _settings.Save(_settingsFile);
    }

    // ---- git.exe ---------------------------------------------------------------------------------------------

    /// <summary>git.exe's full path, or null: not installed (nor set).</summary>
    internal string? GitExe() => GitProgram.Find(_settings.GitPath);

    internal Task SayGitIsMissingAsync(IPluginListWindow? owner) =>
        _host!.Ui.ShowMessageAsync("Git", T("This needs Git (git.exe), which isn't installed here. Get it from git-scm.com, or set where it is in Configuration → Plugins → Git → Settings."), owner);

    // ---- commands --------------------------------------------------------------------------------------------

    /// <summary>The repository of the active panel's folder, or null (said in a notification).</summary>
    private string? ActiveRepository(IPluginHost host)
    {
        string? root = GitRepositories.RootOf(host.ActivePanel.Path);
        if (root == null)
            host.Notify(T("The active panel isn't in a Git repository."), isError: true);
        return root;
    }

    private async Task ChangesAsync(IPluginHost host)
    {
        if (ActiveRepository(host) is string root)
            await new GitChangesWindow(this, host, root).ShowAsync();
    }

    private async Task HistoryAsync(IPluginHost host)
    {
        if (ActiveRepository(host) is string root)
            await new GitHistoryWindow(this, host, root, null).ShowAsync();
    }

    /// <summary>A file (not a folder) in a working tree.</summary>
    private static bool IsInRepository(string path) =>
        File.Exists(path) && Path.GetDirectoryName(path) is string folder && GitRepositories.RootOf(folder) != null;

    /// <summary>The focused file's repository and its path there ('/' separated), or null (said).</summary>
    private (string Root, string Path)? FocusedFile(IPluginHost host)
    {
        Core.FileSystem.FileEntry? item = host.ActivePanel.FocusedItem;
        if (item == null || item.IsDirectory || item.IsUpDir || !File.Exists(item.FullPath) ||
            GitRepositories.RootOf(Path.GetDirectoryName(item.FullPath)!) is not string root)
        {
            host.Notify(T("Focus a file in a Git repository."), isError: true);
            return null;
        }
        return (root, Path.GetRelativePath(root, item.FullPath).Replace('\\', '/'));
    }

    private async Task FileHistoryAsync(IPluginHost host)
    {
        if (FocusedFile(host) is (string root, string path))
            await new GitHistoryWindow(this, host, root, path).ShowAsync();
    }

    private async Task BlameAsync(IPluginHost host)
    {
        if (FocusedFile(host) is (string root, string path))
            await new GitBlameWindow(this, host, root, path).ShowAsync();
    }

    // ---- fetch, pull, push, clone: jobs in the queue -----------------------------------------------------------

    /// <summary>A job of git in the repository; when it ends, the panels read the repository again.</summary>
    private async Task RemoteJobAsync(IPluginHost host, string titleFormat, string glyph, Func<string, IReadOnlyList<string>> arguments)
    {
        if (ActiveRepository(host) is not string root)
            return;
        if (GitExe() is not string git)
        {
            await SayGitIsMissingAsync(null);
            return;
        }
        IReadOnlyList<string> args = arguments(root);
        host.RunJob(GitRemoteJobs.Job(
            string.Format(CultureInfo.CurrentCulture, titleFormat, Path.GetFileName(root)),
            root, glyph, git, root, args, () => Repositories.Refresh(root)));
    }

    private Task FetchAsync(IPluginHost host) => RemoteJobAsync(host, T("Fetch {0}"), "\uE896", _ => GitRemoteJobs.FetchArguments());

    private Task PullAsync(IPluginHost host) => RemoteJobAsync(host, T("Pull {0}"), "\uE896", _ => GitRemoteJobs.PullArguments());

    private Task PushAsync(IPluginHost host) => RemoteJobAsync(host, T("Push {0}"), "\uE898", root =>
    {
        (string? branch, string? remote) = Repositories.Read(root, GitRemoteJobs.PushTarget);
        return GitRemoteJobs.PushArguments(branch, remote);
    });

    private async Task CloneAsync(IPluginHost host)
    {
        if (GitExe() is not string git)
        {
            await SayGitIsMissingAsync(null);
            return;
        }
        string into = _settings.CloneFolder.Length > 0 && Directory.Exists(_settings.CloneFolder)
            ? _settings.CloneFolder
            : host.ActivePanel.Path;
        var url = new PluginField { Label = T("Repository address"), Placeholder = "https://github.com/owner/project.git" };
        var folder = new PluginField { Label = T("Into the folder"), Value = into };
        var name = new PluginField { Label = T("Folder name"), Placeholder = T("(empty: from the address)") };
        bool ok = await host.Ui.EditFieldsAsync(T("Clone a Git Repository"), null, new[] { url, folder, name }, () =>
        {
            if (url.Value.Trim().Length == 0)
                return T("Type the repository's address.");
            if (!Path.IsPathFullyQualified(folder.Value.Trim()) || !Directory.Exists(folder.Value.Trim()))
                return T("The folder doesn't exist.");
            string n = (name.Value.Trim().Length > 0 ? name.Value.Trim() : GitRemoteJobs.FolderNameOf(url.Value));
            if (n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return T("The folder name can't contain \\ / : * ? \" < > |.");
            string target = Path.Combine(folder.Value.Trim(), n);
            return Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()
                ? T("That folder already exists and isn't empty.")
                : null;
        });
        if (!ok)
            return;
        string parent = folder.Value.Trim();
        string folderName = name.Value.Trim().Length > 0 ? name.Value.Trim() : GitRemoteJobs.FolderNameOf(url.Value);
        string address = url.Value.Trim();
        // after the clone: show it in the panel, if the panel is still where the clone went (on the UI thread)
        SynchronizationContext? ui = SynchronizationContext.Current;
        IPanelContext panel = host.ActivePanel;
        host.RunJob(GitRemoteJobs.Job(
            string.Format(CultureInfo.CurrentCulture, T("Clone {0}"), folderName),
            $"{address}  →  {Path.Combine(parent, folderName)}", "\uE8C8", git, parent,
            GitRemoteJobs.CloneArguments(address, folderName),
            () => ui?.Post(_ =>
            {
                if (Directory.Exists(Path.Combine(parent, folderName)) &&
                    string.Equals(panel.Path.TrimEnd('\\'), parent.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    _ = panel.RefreshAsync();
            }, null)));
    }
}
