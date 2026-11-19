// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using LibGit2Sharp;
using VibeSalamander.Core.Operations;
using VibeSalamander.Plugins;
using VibeSalamander.Plugins.Git;

namespace VibeSalamander.Core.Tests;

/// <summary>The Git plugin reads repositories made in the test (libgit2, no git.exe).</summary>
public sealed class GitPluginTests : IDisposable
{
    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "vibe-git-" + Guid.NewGuid().ToString("N")[..8])).FullName;
    private static readonly Signature Me = new("Test", "test@example.com", DateTimeOffset.Now);

    public void Dispose()
    {
        foreach (string file in Directory.GetFiles(_folder, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal); // git's objects are read-only
        Directory.Delete(_folder, recursive: true);
    }

    private string Write(string root, string relative, string text)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>A repository with a commit: README.md, src\app.cs, src\lib\util.cs, and a .gitignore for *.log and bin\.</summary>
    private string Repo(string name = "repo")
    {
        string root = Path.Combine(_folder, name);
        Repository.Init(root);
        using var repo = new Repository(root);
        Write(root, "README.md", "hello\n");
        Write(root, @"src\app.cs", "class App {}\n");
        Write(root, @"src\lib\util.cs", "class Util {}\n");
        Write(root, ".gitignore", "*.log\nbin/\n");
        LibGit2Sharp.Commands.Stage(repo, "*");
        repo.Commit("first", Me, Me);
        return root;
    }

    private static GitFolderState Read(string folder)
    {
        string root = GitRepositories.RootOf(folder)!;
        using var repo = new Repository(root);
        return GitStatusReader.Read(repo, folder);
    }

    [Fact]
    public void A_folder_outside_git_has_no_repository()
    {
        Assert.Null(GitRepositories.RootOf(_folder));
        string root = Repo();
        Assert.Equal(root, GitRepositories.RootOf(Path.Combine(root, "src", "lib")));
        Assert.Null(GitRepositories.RootOf(Path.Combine(root, ".git"))); // the .git folder itself isn't decorated
    }

    [Fact]
    public void A_clean_repository_shows_its_branch_and_no_states()
    {
        string root = Repo();
        GitFolderState state = Read(root);
        Assert.Empty(state.Items);
        Assert.Equal(GitItemState.Clean, state.Others);
        using var repo = new Repository(root);
        Assert.Equal(repo.Head.FriendlyName, state.Branch);
        Assert.Contains("not on a server yet", state.BranchToolTip);
    }

    [Fact]
    public void Each_file_shows_its_state()
    {
        string root = Repo();
        File.AppendAllText(Path.Combine(root, "README.md"), "more\n");     // modified
        Write(root, "notes.txt", "new\n");                                  // untracked
        Write(root, "debug.log", "x\n");                                    // ignored
        Write(root, "staged.txt", "s\n");                                   // added
        File.AppendAllText(Path.Combine(root, ".gitignore"), "# x\n");      // staged, then changed again
        using (var repo = new Repository(root))
        {
            LibGit2Sharp.Commands.Stage(repo, "staged.txt");
            LibGit2Sharp.Commands.Stage(repo, ".gitignore");
        }
        File.AppendAllText(Path.Combine(root, ".gitignore"), "# y\n");
        GitFolderState state = Read(root);
        Assert.Equal(GitItemState.Modified, state.Items["README.md"]);
        Assert.Equal(GitItemState.New, state.Items["notes.txt"]);
        Assert.Equal(GitItemState.Ignored, state.Items["debug.log"]);
        Assert.Equal(GitItemState.Added, state.Items["staged.txt"]);
        Assert.Equal(GitItemState.PartlyStaged, state.Items[".gitignore"]);
        Assert.False(state.Items.ContainsKey("src")); // nothing changed inside
    }

    [Fact]
    public void A_folder_shows_what_changed_inside_it()
    {
        string root = Repo();
        File.AppendAllText(Path.Combine(root, @"src\lib\util.cs"), "// changed\n");
        Write(root, @"docs\guide.md", "new folder\n");
        Write(root, @"bin\out.dll", "ignored folder\n");
        GitFolderState state = Read(root);
        Assert.Equal(GitItemState.ChangesInside, state.Items["src"]);
        Assert.Equal(GitItemState.New, state.Items["docs"]);
        Assert.Equal(GitItemState.Ignored, state.Items["bin"]);

        GitFolderState src = Read(Path.Combine(root, "src"));
        Assert.Equal(GitItemState.ChangesInside, src.Items["lib"]);
        Assert.False(src.Items.ContainsKey("app.cs"));
        Assert.Equal(GitItemState.Modified, Read(Path.Combine(root, @"src\lib")).Items["util.cs"]);
    }

    [Fact]
    public void Inside_an_untracked_or_ignored_folder_everything_is_so()
    {
        string root = Repo();
        Write(root, @"docs\guide.md", "new\n");
        Write(root, @"bin\out.dll", "ignored\n");
        Assert.Equal(GitItemState.New, Read(Path.Combine(root, "docs")).Others);
        Assert.Equal(GitItemState.Ignored, Read(Path.Combine(root, "bin")).Others);
    }

    [Fact]
    public void A_conflict_shows_on_the_file_and_the_branch_says_merging()
    {
        string root = Repo();
        using (var repo = new Repository(root))
        {
            string main = repo.Head.FriendlyName;
            Branch other = repo.CreateBranch("other");
            LibGit2Sharp.Commands.Checkout(repo, other);
            Write(root, "README.md", "theirs\n");
            LibGit2Sharp.Commands.Stage(repo, "README.md");
            repo.Commit("theirs", Me, Me);
            LibGit2Sharp.Commands.Checkout(repo, repo.Branches[main]);
            Write(root, "README.md", "ours\n");
            LibGit2Sharp.Commands.Stage(repo, "README.md");
            repo.Commit("ours", Me, Me);
            MergeResult result = repo.Merge(other, Me);
            Assert.Equal(MergeStatus.Conflicts, result.Status);
        }
        GitFolderState state = Read(root);
        Assert.Equal(GitItemState.Conflict, state.Items["README.md"]);
        Assert.EndsWith(" · merging", state.Branch);
    }

    [Fact]
    public void The_branch_counts_commits_to_push_and_to_pull()
    {
        string origin = Repo("origin");
        string clone = Path.Combine(_folder, "clone");
        Repository.Clone(origin, clone);
        using (var repo = new Repository(clone))
        {
            Write(clone, "mine.txt", "1\n");
            LibGit2Sharp.Commands.Stage(repo, "mine.txt");
            repo.Commit("mine 1", Me, Me);
            Write(clone, "mine.txt", "2\n");
            LibGit2Sharp.Commands.Stage(repo, "mine.txt");
            repo.Commit("mine 2", Me, Me);
        }
        using (var repo = new Repository(origin))
        {
            Write(origin, "theirs.txt", "1\n");
            LibGit2Sharp.Commands.Stage(repo, "theirs.txt");
            repo.Commit("theirs", Me, Me);
        }
        using (var repo = new Repository(clone))
            LibGit2Sharp.Commands.Fetch(repo, "origin", Array.Empty<string>(), null, null);
        GitFolderState state = Read(clone);
        Assert.EndsWith("↑2 ↓1", state.Branch);
        Assert.Contains("2 commits to push, 1 commit to pull", state.BranchToolTip);
    }

    [Fact]
    public void The_stronger_state_wins()
    {
        Assert.Equal(GitItemState.Conflict, GitStatusReader.Stronger(GitItemState.Modified, GitItemState.Conflict));
        Assert.Equal(GitItemState.ChangesInside, GitStatusReader.Stronger(GitItemState.New, GitItemState.ChangesInside));
        Assert.Equal(GitItemState.New, GitStatusReader.Stronger(GitItemState.Ignored, GitItemState.New));
    }

    [Fact]
    public async Task The_decorator_colors_items_and_says_the_branch()
    {
        string root = Repo();
        File.AppendAllText(Path.Combine(root, "README.md"), "more\n");
        using var repositories = new GitRepositories();
        var decorator = new GitDecorator(repositories, t => t);
        PanelDecoration? decoration = await decorator.DecorateAsync(root, CancellationToken.None);
        Assert.NotNull(decoration);
        Assert.Equal("Modified", decoration!.Items["readme.md"].Text); // any case
        Assert.Equal(0xC27C0Eu, decoration.Items["README.md"].Color);
        Assert.Null(decoration.Others);
        Assert.False(string.IsNullOrEmpty(decoration.Status));
        Assert.Null(await decorator.DecorateAsync(_folder, CancellationToken.None)); // not a repository
    }

    // ---- the changes window: what it lists, the old versions it compares with, and git.exe doing the work ----

    [Fact]
    public void Changes_are_listed_by_area()
    {
        string root = Repo();
        File.AppendAllText(Path.Combine(root, "README.md"), "more\n");                   // not staged
        Write(root, "notes.txt", "new\n");                                               // new
        Write(root, @"docs\a.md", "deep new\n");                                         // new, in a new folder
        File.Delete(Path.Combine(root, @"src\lib\util.cs"));                             // deleted, not staged
        File.AppendAllText(Path.Combine(root, @"src\app.cs"), "// staged\n");            // staged, then changed again
        using (var repo = new Repository(root))
            LibGit2Sharp.Commands.Stage(repo, "src/app.cs");
        File.AppendAllText(Path.Combine(root, @"src\app.cs"), "// again\n");
        IReadOnlyList<GitChange> changes;
        using (var repo = new Repository(root))
            changes = GitChanges.Read(repo);
        Assert.Contains(new GitChange("src/app.cs", ChangeArea.Staged, GitChangeKind.Modified), changes);
        Assert.Contains(new GitChange("src/app.cs", ChangeArea.Changes, GitChangeKind.Modified), changes);
        Assert.Contains(new GitChange("README.md", ChangeArea.Changes, GitChangeKind.Modified), changes);
        Assert.Contains(new GitChange("src/lib/util.cs", ChangeArea.Changes, GitChangeKind.Deleted), changes);
        Assert.Contains(new GitChange("notes.txt", ChangeArea.Untracked, GitChangeKind.New), changes);
        Assert.Contains(new GitChange("docs/a.md", ChangeArea.Untracked, GitChangeKind.New), changes); // files in new folders, one by one
        Assert.Equal(ChangeArea.Staged, changes[0].Area); // staged first
    }

    [Fact]
    public void A_change_compares_with_its_staged_or_committed_version()
    {
        string root = Repo();
        File.WriteAllText(Path.Combine(root, "README.md"), "staged\n");
        using (var repo = new Repository(root))
            LibGit2Sharp.Commands.Stage(repo, "README.md");
        File.WriteAllText(Path.Combine(root, "README.md"), "working\n");
        string temp = Path.Combine(_folder, "compare");
        using var repository = new Repository(root);
        var notStaged = GitChanges.Sides(repository, new GitChange("README.md", ChangeArea.Changes, GitChangeKind.Modified), temp)!.Value;
        Assert.Equal("staged", File.ReadAllText(notStaged.Left).Trim());
        Assert.Equal(Path.Combine(root, "README.md"), notStaged.Right);
        var staged = GitChanges.Sides(repository, new GitChange("README.md", ChangeArea.Staged, GitChangeKind.Modified), Path.Combine(temp, "2"))!.Value;
        Assert.Equal("hello", File.ReadAllText(staged.Left).Trim());   // HEAD
        Assert.Equal("staged", File.ReadAllText(staged.Right).Trim()); // the index
        Assert.Contains("(HEAD)", staged.LeftTitle);
        Assert.Null(GitChanges.Sides(repository, new GitChange("x.txt", ChangeArea.Untracked, GitChangeKind.New), temp));
    }

    [Fact]
    public void Paths_reach_git_as_literal_files()
    {
        var odd = new GitChange("-n *.cs", ChangeArea.Changes, GitChangeKind.Modified);
        Assert.Equal(new[] { "--literal-pathspecs", "add", "-A", "--", "-n *.cs" }, GitActions.Stage(odd));
        Assert.Equal(new[] { "--literal-pathspecs", "restore", "--worktree", "--", "-n *.cs" }, GitActions.Discard(odd));
        Assert.Null(GitActions.Discard(odd with { Area = ChangeArea.Untracked }));
        Assert.Equal(new[] { "--literal-pathspecs", "rm", "--cached", "-q", "--", "-n *.cs" }, GitActions.Unstage(odd with { Area = ChangeArea.Staged }, noCommitsYet: true));
        Assert.Equal(new[] { "--literal-pathspecs", "restore", "--staged", "--", "new.cs", "old.cs" },
            GitActions.Unstage(new GitChange("new.cs", ChangeArea.Staged, GitChangeKind.Renamed, "old.cs"), noCommitsYet: false));
    }

    [Fact]
    public void Git_is_found_by_its_full_path()
    {
        string? git = GitProgram.Find(null);
        Assert.True(git != null, "git.exe isn't on this machine (GitHub's runners have it)");
        Assert.True(Path.IsPathFullyQualified(git));
        Assert.Equal(git, GitProgram.Find("")); // nothing set: the same
        Assert.Equal(git, GitProgram.Find("relative\\git.exe")); // only a full path counts
    }

    [Fact]
    public async Task Git_stages_unstages_discards_and_commits()
    {
        string git = GitProgram.Find(null)!;
        string root = Repo();
        Environment.SetEnvironmentVariable("GIT_AUTHOR_NAME", "Test");
        Environment.SetEnvironmentVariable("GIT_AUTHOR_EMAIL", "test@example.com");
        Environment.SetEnvironmentVariable("GIT_COMMITTER_NAME", "Test");
        Environment.SetEnvironmentVariable("GIT_COMMITTER_EMAIL", "test@example.com");
        File.AppendAllText(Path.Combine(root, "README.md"), "more\n");
        var change = new GitChange("README.md", ChangeArea.Changes, GitChangeKind.Modified);

        Assert.True((await GitProgram.RunAsync(git, root, GitActions.Stage(change))).Succeeded);
        using (var repo = new Repository(root))
            Assert.Equal(FileStatus.ModifiedInIndex, repo.RetrieveStatus("README.md"));

        Assert.True((await GitProgram.RunAsync(git, root, GitActions.Unstage(change with { Area = ChangeArea.Staged }, noCommitsYet: false))).Succeeded);
        using (var repo = new Repository(root))
            Assert.Equal(FileStatus.ModifiedInWorkdir, repo.RetrieveStatus("README.md"));

        Assert.True((await GitProgram.RunAsync(git, root, GitActions.Discard(change)!)).Succeeded);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(root, "README.md")).Trim());

        Write(root, "c.txt", "c\n");
        await GitProgram.RunAsync(git, root, GitActions.StageAll());
        GitResult commit = await GitProgram.RunAsync(git, root, GitActions.Commit(amend: false), input: "Second: with \"quotes\" and ünïcode\n\nand a body\n");
        Assert.True(commit.Succeeded, commit.Message);
        using (var repo = new Repository(root))
        {
            Assert.Equal("Second: with \"quotes\" and ünïcode", repo.Head.Tip.MessageShort);
            Assert.Equal("and a body", repo.Head.Tip.Message.Split('\n', 3)[2].Trim());
        }

        GitResult failed = await GitProgram.RunAsync(git, root, new[] { "checkout", "no-such-branch" });
        Assert.False(failed.Succeeded);
        Assert.Contains("no-such-branch", failed.Message);
    }

    // ---- history, blame, browse this version -----------------------------------------------------------------

    [Fact]
    public void The_graph_shows_branches_and_merges()
    {
        // m merges b into a: m(a, b); a(base); b(base); base()
        var commits = new (string, IReadOnlyList<string>)[]
        {
            ("m", new[] { "a", "b" }),
            ("a", new[] { "base" }),
            ("b", new[] { "base" }),
            ("base", Array.Empty<string>()),
        };
        Assert.Equal(new[] { "● ╮", "● │", "│ ●", "● ╯" }, GitGraph.Lay(commits));
        // a straight line stays one lane
        Assert.Equal(new[] { "●", "●", "●" }, GitGraph.Lay(new (string, IReadOnlyList<string>)[] { ("c", new[] { "b" }), ("b", new[] { "a" }), ("a", Array.Empty<string>()) }));
    }

    private string RepoWithHistory()
    {
        string root = Repo();
        using var repo = new Repository(root);
        Write(root, "README.md", "hello\nsecond line\n");
        LibGit2Sharp.Commands.Stage(repo, "README.md");
        var other = new Signature("Other", "other@example.com", DateTimeOffset.Now);
        repo.Commit("second", other, other);
        repo.ApplyTag("v1");
        return root;
    }

    [Fact]
    public void History_lists_commits_with_their_branches_and_tags()
    {
        string root = RepoWithHistory();
        using var repo = new Repository(root);
        IReadOnlyList<GitCommitInfo> history = GitHistory.Read(repo, 100);
        Assert.Equal(new[] { "second", "first" }, history.Select(c => c.Message));
        Assert.Contains(repo.Head.FriendlyName, history[0].Labels);
        Assert.Contains("tag v1", history[0].Labels);
        Assert.Equal("Other", history[0].Author);
        Assert.Single(GitHistory.Read(repo, 100, "src/app.cs")); // only the first commit touched it
        Assert.Equal(2, GitHistory.Read(repo, 100, "README.md").Count);
    }

    [Fact]
    public void A_commits_files_compare_with_its_parent()
    {
        string root = RepoWithHistory();
        using var repo = new Repository(root);
        string second = repo.Head.Tip.Sha, first = repo.Head.Tip.Parents.Single().Sha;
        Assert.Equal(new[] { new GitCommitFile("README.md", "Modified", null) }, GitHistory.Files(repo, second));
        Assert.Equal(4, GitHistory.Files(repo, first).Count); // the first commit: everything added
        string temp = Path.Combine(_folder, "versions");
        Assert.Equal("hello", File.ReadAllText(GitHistory.WriteVersion(repo, first, "README.md", temp, "a")!).Trim());
        Assert.Null(GitHistory.WriteVersion(repo, first, "nothing.txt", temp, "b"));
    }

    [Fact]
    public void Blame_says_who_changed_each_line()
    {
        string root = RepoWithHistory();
        using var repo = new Repository(root);
        IReadOnlyList<GitBlameLine> lines = GitHistory.Blame(repo, "README.md")!;
        Assert.Equal(2, lines.Count);
        Assert.Equal(("hello", "Test", 1), (lines[0].Text, lines[0].Author, lines[0].Number));
        Assert.Equal(("second line", "Other", 2), (lines[1].Text, lines[1].Author, lines[1].Number));
        Assert.True(lines[1].FirstOfHunk);
        Assert.Null(GitHistory.Blame(repo, "not-committed.txt"));
    }

    [Fact]
    public async Task An_old_version_is_browsed_and_copied_out()
    {
        string root = RepoWithHistory();
        string first;
        using (var repo = new Repository(root))
            first = repo.Head.Tip.Parents.Single().Sha;
        var plugin = new GitPlugin();
        var fs = new GitVersionFileSystem(plugin);
        string top = GitVersionFileSystem.PathOf(root, first);
        Assert.True(fs.CanHandle(top));
        Assert.Equal((root, first, ""), GitVersionFileSystem.Parse(top));
        Assert.Equal(root, fs.GetParent(top)); // up from the top: the folder on disk
        Assert.Equal($"{root} @ {first[..7]}\\src", fs.GetDisplayPath(fs.Combine(top, "src")));

        IReadOnlyList<RemoteItem> items = await fs.ListAsync(top, CancellationToken.None);
        Assert.Equal(new[] { ".gitignore", "README.md", "src" }, items.Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.True(items.Single(i => i.Name == "src").IsDirectory);
        IReadOnlyList<RemoteItem> lib = await fs.ListAsync(fs.Combine(fs.Combine(top, "src"), "lib"), CancellationToken.None);
        Assert.Equal("util.cs", lib.Single().Name);
        Assert.Equal(fs.Combine(top, "src"), fs.GetParent(fs.Combine(fs.Combine(top, "src"), "lib")));

        string target = Directory.CreateDirectory(Path.Combine(_folder, "out")).FullName;
        OperationResult result = await fs.DownloadAsync(items.Where(i => i.Name is "README.md" or "src").ToList(), target, new TestJob());
        Assert.Equal(3, result.Succeeded); // README.md, src\app.cs, src\lib\util.cs
        Assert.Equal("hello", File.ReadAllText(Path.Combine(target, "README.md")).Trim()); // the first version, not the second
        Assert.True(File.Exists(Path.Combine(target, @"src\lib\util.cs")));
        await Assert.ThrowsAsync<NotSupportedException>(() => fs.CreateDirectoryAsync(fs.Combine(top, "x"), CancellationToken.None));
    }

    // ---- fetch, pull, push, clone -------------------------------------------------------------------------------

    [Fact]
    public void Git_progress_lines_are_read()
    {
        Assert.Equal(new GitProgressStep("Receiving objects", 45, 450, 1000), GitRemoteJobs.ParseProgress("Receiving objects:  45% (450/1000), 1.2 MiB | 3 MiB/s"));
        Assert.Equal(new GitProgressStep("Counting objects", 100, 20, 20), GitRemoteJobs.ParseProgress("remote: Counting objects: 100% (20/20), done."));
        Assert.Equal(new GitProgressStep("Writing objects", 7, 7, 100), GitRemoteJobs.ParseProgress("Writing objects:   7%"));
        Assert.Null(GitRemoteJobs.ParseProgress("Cloning into 'app'..."));
        Assert.Null(GitRemoteJobs.ParseProgress("fatal: repository 'x' does not exist"));
        Assert.Equal("fatal: no", GitRemoteJobs.LastLines("Receiving objects:  45% (450/1000)\nfatal: no\n"));
    }

    [Theory]
    [InlineData("https://github.com/team/app.git", "app")]
    [InlineData("git@github.com:team/app.git", "app")]
    [InlineData("https://host/team/app/", "app")]
    [InlineData(@"C:\repos\tool", "tool")]
    [InlineData("", "repository")]
    public void A_clone_is_named_after_its_address(string url, string name) => Assert.Equal(name, GitRemoteJobs.FolderNameOf(url));

    [Fact]
    public void The_jobs_ask_git_for_the_right_things()
    {
        Assert.Equal(new[] { "clone", "--progress", "--", "-x", "app" }, GitRemoteJobs.CloneArguments("-x", "app")); // an address can't pass for an option
        Assert.Equal(new[] { "push", "--progress" }, GitRemoteJobs.PushArguments(null, null));
        Assert.Equal(new[] { "push", "--progress", "--set-upstream", "origin", "main" }, GitRemoteJobs.PushArguments("main", "origin"));
    }

    [Fact]
    public async Task Clone_push_fetch_and_pull_run_in_git()
    {
        string? git = GitProgram.Find(null);
        if (git == null)
            return; // no Git here (the Windows runners have it)
        string server = Path.Combine(_folder, "server.git");
        Repository.Init(server, isBare: true);
        string source = RepoWithHistory();
        using (var repo = new Repository(source))
        {
            repo.Network.Remotes.Add("origin", server);
            Assert.Equal((repo.Head.FriendlyName, "origin"), GitRemoteJobs.PushTarget(repo)); // no upstream yet
        }
        string branch;
        using (var repo = new Repository(source))
            branch = repo.Head.FriendlyName;

        var progress = new List<OperationProgress>();
        async Task Run(string folder, IReadOnlyList<string> arguments)
        {
            bool done = false;
            var job = new TestJob(progress.Add);
            OperationResult result = await GitRemoteJobs.Job("t", folder, null, git, folder, arguments, () => done = true).Run(job);
            Assert.Equal(1, result.Succeeded);
            Assert.True(done);
        }

        await Run(source, GitRemoteJobs.PushArguments(branch, "origin"));
        using (var repo = new Repository(source))
        {
            Assert.True(repo.Head.IsTracking); // from now on the branch pushes to origin
            Assert.Equal((null, null), GitRemoteJobs.PushTarget(repo));
        }
        await Run(_folder, GitRemoteJobs.CloneArguments(server, "copy"));
        string copy = Path.Combine(_folder, "copy");
        Assert.Equal("hello", File.ReadAllText(Path.Combine(copy, "README.md")).Split('\n')[0].Trim());
        Assert.Contains(progress, p => p.ItemsDone == 100);

        // a new commit pushed from the source: fetched, then pulled into the copy
        using (var repo = new Repository(source))
        {
            Write(source, "new.txt", "new");
            LibGit2Sharp.Commands.Stage(repo, "new.txt");
            repo.Commit("third", Me, Me);
        }
        await Run(source, GitRemoteJobs.PushArguments(null, null));
        await Run(copy, GitRemoteJobs.FetchArguments());
        using (var repo = new Repository(copy))
            Assert.Equal(1, repo.Head.TrackingDetails.BehindBy);
        await Run(copy, GitRemoteJobs.PullArguments());
        Assert.True(File.Exists(Path.Combine(copy, "new.txt")));

        // a failure says git's reason
        var failing = GitRemoteJobs.Job("t", _folder, null, git, _folder, GitRemoteJobs.CloneArguments(Path.Combine(_folder, "nothing-here"), "x"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => failing.Run(new TestJob()));
        Assert.Contains("nothing-here", error.Message);
    }

    [Fact]
    public void Only_a_repositorys_own_folder_gets_the_folder_commands()
    {
        string root = Repo();
        Assert.True(GitPlugin.IsRepositoryFolder(root));
        Assert.True(GitPlugin.IsRepositoryFolder(root + "\\"));
        Assert.False(GitPlugin.IsRepositoryFolder(Path.Combine(root, "src"))); // inside, not the repository's folder
        Assert.False(GitPlugin.IsRepositoryFolder(Path.Combine(root, ".git")));
        Assert.False(GitPlugin.IsRepositoryFolder(_folder));
        var plugin = new GitPlugin();
        Assert.Equal(new[] { "changes", "history", "fetch", "pull", "push" },
            plugin.Commands.Where(c => c.AppliesTo?.Invoke(root) == true).Select(c => c.Id));
        Assert.Equal(new[] { "file-history", "blame" },
            plugin.Commands.Where(c => c.AppliesTo?.Invoke(Path.Combine(root, "README.md")) == true).Select(c => c.Id));
    }

    /// <summary>A job that answers nothing (no conflicts in these tests).</summary>
    private sealed class TestJob(Action<OperationProgress>? report = null) : IArchiveJob
    {
        public CancellationToken Cancellation => CancellationToken.None;
        public void WaitWhilePaused() { }
        public void Report(OperationProgress progress) => report?.Invoke(progress);
        public Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict) => Task.FromResult(ConflictChoice.Overwrite);
        public Task<ErrorChoice> ResolveErrorAsync(OperationError error) => Task.FromResult(ErrorChoice.Cancel);
        public Task<string?> AskPasswordAsync(string archivePath, bool wrongPassword) => Task.FromResult<string?>(null);
    }
}
