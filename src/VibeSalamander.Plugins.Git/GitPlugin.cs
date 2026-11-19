// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

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
        Commands = new[]
        {
            new PluginCommand("changes", "Changes...", null, ChangesAsync),
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
}
