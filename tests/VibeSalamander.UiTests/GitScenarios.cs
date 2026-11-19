// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;

namespace VibeSalamander.UiTests;

/// <summary>
/// The Git plugin (a separate download, installed into the test's Plugins folder) in a repository the test makes
/// with git.exe.
/// </summary>
public sealed class GitScenarios : IDisposable
{
    private readonly DummyFiles _files = new();
    private readonly string _repo;

    public GitScenarios()
    {
        _repo = Path.Combine(_files.Documents, "Project");
        // who commits (the app's git.exe gets it from here; GitHub's runners have no user.name)
        foreach ((string name, string value) in new[]
                 {
                     ("GIT_AUTHOR_NAME", "Test"), ("GIT_COMMITTER_NAME", "Test"),
                     ("GIT_AUTHOR_EMAIL", "test@example.com"), ("GIT_COMMITTER_EMAIL", "test@example.com"),
                 })
            Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        foreach (string file in Directory.Exists(_repo) ? Directory.GetFiles(_repo, "*", SearchOption.AllDirectories) : Array.Empty<string>())
            File.SetAttributes(file, FileAttributes.Normal);
        _files.Dispose();
    }

    private static string? PluginFolder() =>
        Environment.GetEnvironmentVariable("VIBE_GIT_PLUGIN") is { Length: > 0 } folder && Directory.Exists(folder) ? folder : null;

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (string sub in Directory.GetDirectories(from))
            CopyFolder(sub, Path.Combine(to, Path.GetFileName(sub)));
    }

    /// <summary>Runs git in the repository (git.exe on the PATH: GitHub's runners have it).</summary>
    internal static string Git(string folder, string arguments)
    {
        var start = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.Environment["GIT_AUTHOR_NAME"] = start.Environment["GIT_COMMITTER_NAME"] = "Test";
        start.Environment["GIT_AUTHOR_EMAIL"] = start.Environment["GIT_COMMITTER_EMAIL"] = "test@example.com";
        using Process git = Process.Start(start)!;
        string output = git.StandardOutput.ReadToEnd() + git.StandardError.ReadToEnd();
        git.WaitForExit();
        Assert.True(git.ExitCode == 0, $"git {arguments}: {output}");
        return output;
    }

    /// <summary>A repository with a commit, then: README.md changed, notes.txt new, debug.log ignored, src\ changed inside.</summary>
    private void MakeRepository()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "src"));
        File.WriteAllText(Path.Combine(_repo, "README.md"), "hello\n");
        File.WriteAllText(Path.Combine(_repo, @"src\app.cs"), "class App {}\n");
        File.WriteAllText(Path.Combine(_repo, "keep.txt"), "unchanged\n");
        File.WriteAllText(Path.Combine(_repo, ".gitignore"), "*.log\n");
        Git(_repo, "init -b main");
        Git(_repo, "add -A");
        Git(_repo, "commit -m first");
        File.AppendAllText(Path.Combine(_repo, "README.md"), "more\n");
        File.AppendAllText(Path.Combine(_repo, @"src\app.cs"), "// more\n");
        File.WriteAllText(Path.Combine(_repo, "notes.txt"), "new\n");
        File.WriteAllText(Path.Combine(_repo, "debug.log"), "ignored\n");
    }

    private AppSession? Start()
    {
        if (PluginFolder() is not string plugin)
            return null; // not built on this machine
        MakeRepository();
        var app = new AppSession(s =>
        {
            s.Left.Path = _repo;
            s.Right.Path = _files.Target;
        }, prepareHome: home => CopyFolder(plugin, Path.Combine(home, "Plugins", "Git")));
        AppSession.WaitUntil(() => app.Items().Contains("README.md"), "the repository");
        return app;
    }

    private static void NoErrors(AppSession app)
    {
        string log = app.ErrorLogText();
        Assert.True(log.Length == 0, "The error log has entries:\n" + log);
    }

    /// <summary>The Git column's text in the row of an item (left panel).</summary>
    private static string GitText(AppSession app, string name)
    {
        AutomationElement row = app.Row(name);
        return string.Join(" ", row.FindAllDescendants().Select(e => e.Properties.Name.ValueOrDefault ?? string.Empty)
            .Where(t => t is "Modified" or "New" or "Ignored" or "Changes" or "Staged" or "Added" or "Conflict"));
    }

    [UiFact]
    public void The_panel_shows_the_branch_and_each_files_git_state()
    {
        using AppSession? app = Start();
        if (app == null)
            return;
        AutomationElement status = AppSession.Retry(() => app.Window.FindFirstDescendant(cf => cf.ByAutomationId("DecorationStatus")), "the branch on the path line");
        AppSession.WaitUntil(() => status.Name == "main", $"the branch \"main\" (it says \"{status.Name}\")");
        AppSession.WaitUntil(() => GitText(app, "README.md") == "Modified", "README.md modified");
        Assert.Equal("New", GitText(app, "notes.txt"));
        Assert.Equal("Ignored", GitText(app, "debug.log"));
        Assert.Equal("Changes", GitText(app, "src"));
        Assert.Equal(string.Empty, GitText(app, "keep.txt"));
        AppSession.Screenshot("88 git in the panel");

        // a commit made elsewhere (here: git itself) shows without a refresh
        Git(_repo, "add -A");
        Git(_repo, "commit -m second");
        AppSession.WaitUntil(() => GitText(app, "README.md") == string.Empty, "README.md clean after the commit");
        Assert.Equal("Ignored", GitText(app, "debug.log"));
        NoErrors(app);
    }

    [UiFact]
    public void Outside_a_repository_there_is_no_git_column()
    {
        using AppSession? app = Start();
        if (app == null)
            return;
        app.Press(VirtualKeyShort.BACK); // up to Documents: not a repository
        AppSession.WaitUntil(() => app.Items().Contains("report.txt"), "Documents");
        AppSession.WaitUntil(() => app.Window.FindFirstDescendant(cf => cf.ByAutomationId("DecorationStatus")) is null or { IsOffscreen: true },
            "no branch on the path line");
        Assert.Null(app.Window.FindFirstDescendant(cf => cf.ByName("Git")));
        NoErrors(app);
    }

    /// <summary>A row of a plugin window by the text of one of its cells.</summary>
    private static AutomationElement Cell(Window window, string text) =>
        AppSession.Retry(() => window.FindAllDescendants(cf => cf.ByName(text)).FirstOrDefault(), "\"" + text + "\" in " + window.Title);

    private static void Press(Window window, string button) =>
        AppSession.Retry(() => window.FindFirstDescendant(cf => cf.ByName(button).And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button))), "the button " + button)
            .AsButton().Invoke();

    [UiFact]
    public void Changes_are_staged_compared_and_committed()
    {
        using AppSession? app = Start();
        if (app == null)
            return;
        app.Menu("Plugins", "Git", "Changes...");
        Window changes = app.OtherWindow("Git Changes - Project");
        Cell(changes, "notes.txt");
        Cell(changes, @"src\app.cs");
        AppSession.Screenshot("89 git changes");

        // stage README.md: it moves to Staged
        Cell(changes, "README.md").Click();
        Press(changes, "Stage");
        AppSession.WaitUntil(() => Cell(changes, "Staged") != null, "README.md staged");
        AppSession.WaitUntil(() => GitText(app, "README.md") == "Staged", "the panel says README.md is staged");

        // compare the staged change: the comparator opens on HEAD's version against the staged one
        Cell(changes, "README.md").Click();
        Press(changes, "Compare");
        Window compare = app.OtherWindow("README.md (HEAD)");
        app.Calm(400);
        AppSession.Screenshot("90 git compare");
        compare.Close();

        // commit (only the staged README.md)
        Press(changes, "Commit...");
        AutomationElement message = AppSession.Retry(() => changes.FindFirstDescendant(cf =>
            cf.ByName("Message").And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit))), "the commit message");
        message.AsTextBox().Text = "Update the readme";
        Press(changes, "OK");
        AppSession.WaitUntil(() => Git(_repo, "log -1 --format=%s").Trim() == "Update the readme", "the commit");
        AppSession.WaitUntil(() => GitText(app, "README.md") == string.Empty, "README.md clean in the panel");
        Assert.Equal("New", GitText(app, "notes.txt")); // not staged: not committed
        changes.Close();
        NoErrors(app);
    }

    [UiFact]
    public void History_blame_and_an_old_version_copied_out()
    {
        using AppSession? app = Start();
        if (app == null)
            return;
        Git(_repo, "add -A");
        Git(_repo, "commit -m second");

        // blame of the focused file (also in its right-click menu)
        app.GoTo("README.md");
        app.Menu("Plugins", "Git", "Git Blame...");
        Window blame = app.OtherWindow("Git Blame - README.md");
        Cell(blame, "hello");
        Cell(blame, "more");
        AppSession.Screenshot("91 git blame");
        blame.Close();

        // the history: both commits, the graph
        app.Menu("Plugins", "Git", "History...");
        Window history = app.OtherWindow("Git History - Project");
        Cell(history, "second");
        AppSession.Screenshot("92 git history");

        // browse the first commit and copy its README.md out (the version before "more")
        Cell(history, "first").Click();
        Press(history, "Browse This Version");
        AppSession.WaitUntil(() => app.Items().Contains("README.md") && !app.Items().Contains("notes.txt"), "the first commit's files in the panel");
        AppSession.Screenshot("93 git browse this version");
        history.Close();
        app.GoTo("README.md");
        app.Press(VirtualKeyShort.F5);
        app.Press(VirtualKeyShort.ENTER);
        string copied = Path.Combine(_files.Target, "README.md");
        AppSession.WaitUntil(() => File.Exists(copied) && File.ReadAllText(copied).Trim() == "hello", "README.md of the first commit copied out");
        NoErrors(app);
    }
}
