// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Plugins.Git;

/// <summary>
/// Git in the panels: the branch on the path line (with commits to push and to pull), each item's state in the Git
/// column and its name in the state's color.
/// </summary>
internal sealed class GitDecorator : IPluginPanelDecorator
{
    private readonly GitRepositories _repositories;
    private readonly Func<string, string> T;

    public GitDecorator(GitRepositories repositories, Func<string, string> translate)
    {
        _repositories = repositories;
        T = translate;
        _repositories.Changed += (_, root) => Changed?.Invoke(this, root);
    }

    public string Id => "git";
    public string ColumnHeader => "Git";

    public event EventHandler<string>? Changed;

    public Task<PanelDecoration?> DecorateAsync(string directory, CancellationToken cancellationToken)
    {
        string? root = GitRepositories.RootOf(directory);
        if (root == null)
            return Task.FromResult<PanelDecoration?>(null);
        GitFolderState state = _repositories.Read(root, repository => GitStatusReader.Read(repository, directory, cancellationToken));
        return Task.FromResult<PanelDecoration?>(new PanelDecoration
        {
            Status = state.Branch,
            StatusToolTip = state.BranchToolTip,
            Items = state.Items.ToDictionary(p => p.Key, p => Decoration(p.Value)!, StringComparer.OrdinalIgnoreCase),
            Others = Decoration(state.Others),
        });
    }

    /// <summary>Colors that read on light and dark panels alike.</summary>
    internal ItemDecoration? Decoration(GitItemState state) => state switch
    {
        GitItemState.Modified => new ItemDecoration(T("Modified"), 0xC27C0E, T("Changed, not staged")),
        GitItemState.PartlyStaged => new ItemDecoration(T("Modified"), 0xC27C0E, T("Staged, then changed again")),
        GitItemState.Staged => new ItemDecoration(T("Staged"), 0x2F7FD6, T("Staged for the next commit")),
        GitItemState.Added => new ItemDecoration(T("Added"), 0x2E9E4F, T("New, staged for the next commit")),
        GitItemState.Renamed => new ItemDecoration(T("Renamed"), 0x2F7FD6, T("Renamed, staged for the next commit")),
        GitItemState.New => new ItemDecoration(T("New"), 0x2E9E4F, T("Not in Git yet (untracked)")),
        GitItemState.Conflict => new ItemDecoration(T("Conflict"), 0xD73A49, T("A merge conflict to resolve")),
        GitItemState.Ignored => new ItemDecoration(T("Ignored"), 0x8C8C8C, T("Ignored (.gitignore)")),
        GitItemState.ChangesInside => new ItemDecoration(T("Changes"), 0xC27C0E, T("Changes inside")),
        _ => null,
    };
}
