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
    internal GitRepositories Repositories { get; } = new();

    public IReadOnlyList<PluginCommand> Commands { get; private set; } = Array.Empty<PluginCommand>();

    internal string T(string text) => _host?.Ui.T(text) ?? text;

    public void Initialize(IPluginHost host)
    {
        _host = host;
        host.RegisterPanelDecorator(new GitDecorator(Repositories, T));
        Commands = Array.Empty<PluginCommand>();
        host.RegisterCommands(this);
    }

    public void Shutdown() => Repositories.Dispose();
}
