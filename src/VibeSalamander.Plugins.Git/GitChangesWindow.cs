// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using LibGit2Sharp;

namespace VibeSalamander.Plugins.Git;

/// <summary>
/// A repository's changes: staged, not staged, not in Git yet, conflicts. Stage, unstage, discard and commit run in
/// the user's git.exe; Compare opens the file comparator on the change (the staged or committed version against the
/// working file, or for a staged change HEAD's against the staged one).
/// </summary>
internal sealed class GitChangesWindow
{
    private readonly GitPlugin _plugin;
    private readonly IPluginHost _host;
    private readonly string _root;
    private IPluginListWindow? _window;
    private bool _busy;

    public GitChangesWindow(GitPlugin plugin, IPluginHost host, string root)
    {
        _plugin = plugin;
        _host = host;
        _root = root;
    }

    private string T(string text) => _plugin.T(text);

    public async Task ShowAsync()
    {
        _window = _host.Ui.OpenListWindow(T("Git Changes") + " - " + Path.GetFileName(_root), new[]
        {
            new PluginColumn(T("Area"), 110),
            new PluginColumn(T("Change"), 100),
            new PluginColumn(T("File"), 480),
        });
        _window.SetButtons(new[]
        {
            new PluginButton("stage", T("Stage"), () => _ = OnSelectedAsync(StageAsync)),
            new PluginButton("unstage", T("Unstage"), () => _ = OnSelectedAsync(UnstageAsync)),
            new PluginButton("discard", T("Discard..."), () => _ = OnSelectedAsync(DiscardAsync)),
            new PluginButton("compare", T("Compare"), () => _ = OnSelectedAsync(CompareAsync)),
            new PluginButton("commit", T("Commit..."), () => _ = CommitAsync(), IsDefault: true),
        });
        _window.RowMenu = row => row.Tag is not GitChange change ? Array.Empty<PluginMenuItem>() : new[]
        {
            new PluginMenuItem(T("Compare"), null, () => _ = CompareAsync(change), change.Area is ChangeArea.Changes or ChangeArea.Staged),
            new PluginMenuItem(T("Stage"), null, () => _ = StageAsync(change), change.Area != ChangeArea.Staged),
            new PluginMenuItem(T("Unstage"), null, () => _ = UnstageAsync(change), change.Area == ChangeArea.Staged),
            new PluginMenuItem(T("Discard..."), null, () => _ = DiscardAsync(change), change.Area is ChangeArea.Changes or ChangeArea.Untracked),
            new PluginMenuItem(T("Show in Panel"), null, () => _ = ShowInPanelAsync(change)),
            new PluginMenuItem(T("Stage All"), null, () => _ = RunAsync(GitActions.StageAll())),
        };
        _plugin.Repositories.Changed += OnRepositoryChanged;
        _window.Closed += (_, _) => _plugin.Repositories.Changed -= OnRepositoryChanged;
        await FillAsync();
    }

    private void OnRepositoryChanged(object? sender, string root)
    {
        if (string.Equals(root, _root, StringComparison.OrdinalIgnoreCase) && !_busy)
            _ = FillAsync(); // the window's methods may be called from any thread
    }

    private string AreaText(ChangeArea area) => area switch
    {
        ChangeArea.Staged => T("Staged"),
        ChangeArea.Changes => T("Not staged"),
        ChangeArea.Untracked => T("New"),
        _ => T("Conflict"),
    };

    private string KindText(ChangeKind kind) => kind switch
    {
        ChangeKind.Modified => T("Modified"),
        ChangeKind.Added => T("Added"),
        ChangeKind.Deleted => T("Deleted"),
        ChangeKind.Renamed => T("Renamed"),
        ChangeKind.TypeChanged => T("Type changed"),
        ChangeKind.New => T("New"),
        _ => T("Conflict"),
    };

    private async Task FillAsync()
    {
        IPluginListWindow? window = _window;
        if (window == null || window.IsClosed)
            return;
        IReadOnlyList<GitChange> changes;
        try
        {
            changes = await Task.Run(() => _plugin.Repositories.Read(_root, GitChanges.Read));
        }
        catch (Exception ex)
        {
            window.SetStatus(ex.Message);
            return;
        }
        if (window.IsClosed)
            return;
        string? selected = (window.SelectedRow?.Tag as GitChange)?.Path;
        foreach (IPluginRow row in window.Rows.ToList())
            row.Remove();
        foreach (GitChange change in changes)
        {
            string shown = change.Path.Replace('/', '\\') + (change.OldPath != null ? "  ←  " + change.OldPath.Replace('/', '\\') : string.Empty);
            IPluginRow row = window.AddRow(new[] { AreaText(change.Area), KindText(change.Kind), shown });
            row.Tag = change;
        }
        int staged = changes.Count(c => c.Area == ChangeArea.Staged);
        string branch = await Task.Run(() => _plugin.Repositories.Read(_root, r => GitStatusReader.BranchOf(r).Text));
        window.SetStatus(changes.Count == 0
            ? string.Format(T("{0}: nothing to commit, the working tree is clean"), branch)
            : string.Format(T("{0}: {1} changes, {2} staged"), branch, changes.Count, staged));
        _ = selected;
    }

    private async Task OnSelectedAsync(Func<GitChange, Task> action)
    {
        if (_window?.SelectedRow?.Tag is GitChange change)
            await action(change);
        else
            _window?.SetStatus(T("Select a file first."));
    }

    /// <summary>Runs git.exe in the repository; its message in a dialog when it fails. True: it succeeded.</summary>
    private async Task<bool> RunAsync(IReadOnlyList<string> arguments, string? input = null)
    {
        if (_plugin.GitExe() is not string git)
        {
            await _plugin.SayGitIsMissingAsync(_window);
            return false;
        }
        _busy = true;
        _window?.SetStatus(T("Git is working..."));
        try
        {
            GitResult result = await GitProgram.RunAsync(git, _root, arguments, input: input);
            if (!result.Succeeded)
                await _host.Ui.ShowMessageAsync("Git", result.Message, _window);
            return result.Succeeded;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            await _host.Ui.ShowMessageAsync("Git", ex.Message, _window);
            return false;
        }
        finally
        {
            _busy = false;
            _plugin.Repositories.Refresh(_root); // the panels and this window read it again
            await FillAsync();
        }
    }

    private Task StageAsync(GitChange change) => change.Area == ChangeArea.Staged ? Task.CompletedTask : RunAsync(GitActions.Stage(change));

    private async Task UnstageAsync(GitChange change)
    {
        if (change.Area != ChangeArea.Staged)
            return;
        bool noCommits = _plugin.Repositories.Read(_root, r => r.Info.IsHeadUnborn);
        await RunAsync(GitActions.Unstage(change, noCommits));
    }

    private async Task DiscardAsync(GitChange change)
    {
        string path = Path.Combine(_root, change.Path.Replace('/', '\\'));
        if (change.Area == ChangeArea.Untracked)
        {
            if (!await _host.Ui.ConfirmAsync(T("Discard"), string.Format(T("Move the new file \"{0}\" to the Recycle Bin?"), change.Path), _window))
                return;
            // to the Recycle Bin, through the shell (as Delete does in the panels)
            await Core.Interop.ShellOperations.DeleteAsync(IntPtr.Zero, new[] { path }, permanent: false);
            _plugin.Repositories.Refresh(_root);
            await FillAsync();
            return;
        }
        if (GitActions.Discard(change) is not string[] arguments)
        {
            _window?.SetStatus(T("Unstage it first: only changes that aren't staged can be discarded."));
            return;
        }
        if (await _host.Ui.ConfirmAsync(T("Discard"),
                string.Format(T("Discard the changes to \"{0}\"? The file goes back to its staged (or last committed) version; the changes are lost."), change.Path),
                _window))
            await RunAsync(arguments);
    }

    private async Task CompareAsync(GitChange change)
    {
        if (change.Area is not (ChangeArea.Changes or ChangeArea.Staged))
        {
            _window?.SetStatus(T("A new file has nothing to compare with yet."));
            return;
        }
        string folder = Path.Combine(Path.GetTempPath(), "VibeSalamander", "Git", Guid.NewGuid().ToString("N")[..8]);
        var sides = await Task.Run(() => _plugin.Repositories.Read(_root, r => GitChanges.Sides(r, change, folder)));
        if (sides is not { } s)
        {
            _window?.SetStatus(T("There is no earlier version to compare with."));
            return;
        }
        _host.CompareFiles(s.Left, s.Right, s.LeftTitle, s.RightTitle);
    }

    private async Task ShowInPanelAsync(GitChange change)
    {
        string path = Path.Combine(_root, change.Path.Replace('/', '\\'));
        await _host.ActivePanel.NavigateAsync(Path.GetDirectoryName(path)!, Path.GetFileName(path));
    }

    private async Task CommitAsync()
    {
        IReadOnlyList<GitChange> changes = await Task.Run(() => _plugin.Repositories.Read(_root, GitChanges.Read));
        bool stageAll = false;
        if (!changes.Any(c => c.Area == ChangeArea.Staged))
        {
            if (changes.Count == 0)
            {
                _window?.SetStatus(T("Nothing to commit."));
                return;
            }
            if (!await _host.Ui.ConfirmAsync(T("Commit"), T("Nothing is staged. Stage all the changes (new files too) and commit them?"), _window))
                return;
            stageAll = true;
        }
        var message = new PluginField { Label = T("Message"), Placeholder = T("What this commit does") };
        var amend = new PluginField { Label = T("Amend the last commit (replace it with this one)"), Kind = PluginFieldKind.Check, Value = "false" };
        if (!await _host.Ui.EditFieldsAsync(T("Commit"), string.Format(T("{0} staged changes on {1}"),
                stageAll ? changes.Count : changes.Count(c => c.Area == ChangeArea.Staged),
                _plugin.Repositories.Read(_root, r => GitStatusReader.BranchOf(r).Text)),
                new[] { message, amend },
                () => message.Value.Trim().Length == 0 ? T("Write a message.") : null, _window))
            return;
        if (stageAll && !await RunAsync(GitActions.StageAll()))
            return;
        if (await RunAsync(GitActions.Commit(amend.Value == "true"), message.Value.Trim() + "\n"))
            _host.Notify(T("Committed."));
    }
}
