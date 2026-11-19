// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.RegularExpressions;
using LibGit2Sharp;
using VibeSalamander.Core.Operations;

namespace VibeSalamander.Plugins.Git;

/// <summary>A step of git's progress: "Receiving objects", 45 (%), 450 of 1000.</summary>
internal sealed record GitProgressStep(string Phase, int Percent, long Done, long Total);

/// <summary>
/// Fetch, pull, push and clone: jobs in the queue (Transfers), run by the user's own git.exe with --progress. A job
/// reports git's steps ("Receiving objects 45%") as its progress, cancels by ending git, and fails with git's own
/// message.
/// </summary>
internal static partial class GitRemoteJobs
{
    [GeneratedRegex(@"^(?:remote:\s*)?(?<phase>[A-Za-z][^:]*?):\s+(?<percent>\d{1,3})%(?:\s+\((?<done>\d+)/(?<total>\d+)\))?")]
    private static partial Regex ProgressLine();

    /// <summary>A progress line of git ("remote: Counting objects: 45% (9/20)"), or null (any other line).</summary>
    public static GitProgressStep? ParseProgress(string line)
    {
        Match m = ProgressLine().Match(line.Trim());
        if (!m.Success)
            return null;
        int percent = Math.Clamp(int.Parse(m.Groups["percent"].Value, CultureInfo.InvariantCulture), 0, 100);
        long done = m.Groups["done"].Success ? long.Parse(m.Groups["done"].Value, CultureInfo.InvariantCulture) : percent;
        long total = m.Groups["total"].Success ? long.Parse(m.Groups["total"].Value, CultureInfo.InvariantCulture) : 100;
        return new GitProgressStep(m.Groups["phase"].Value.Trim(), percent, done, total);
    }

    /// <summary>What git is asked to do for each job (after the "-c core.quotepath=false" every call has).</summary>
    public static IReadOnlyList<string> FetchArguments() => new[] { "fetch", "--all", "--prune", "--progress" };

    public static IReadOnlyList<string> PullArguments() => new[] { "pull", "--progress" };

    /// <summary>A push: to the branch's upstream; a branch without one goes to <paramref name="remote"/> and tracks it from then on.</summary>
    public static IReadOnlyList<string> PushArguments(string? branchWithoutUpstream, string? remote) =>
        branchWithoutUpstream != null && remote != null
            ? new[] { "push", "--progress", "--set-upstream", remote, branchWithoutUpstream }
            : new[] { "push", "--progress" };

    /// <summary>A clone of the address into the folder ("--" first: an address can't pass for an option).</summary>
    public static IReadOnlyList<string> CloneArguments(string url, string folder) => new[] { "clone", "--progress", "--", url, folder };

    /// <summary>The folder a clone of the address gets by default: "https://host/team/app.git" → "app".</summary>
    public static string FolderNameOf(string url)
    {
        string name = url.Trim().TrimEnd('/', '\\');
        int cut = Math.Max(name.LastIndexOf('/'), Math.Max(name.LastIndexOf('\\'), name.LastIndexOf(':')));
        name = name[(cut + 1)..];
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Length > 0 ? name : "repository";
    }

    /// <summary>
    /// For a push: the branch that has no upstream yet and the remote it would go to ("origin", else the only one);
    /// (null, null) when the branch has its upstream, or there is nothing to push to.
    /// </summary>
    public static (string? Branch, string? Remote) PushTarget(Repository repo)
    {
        Branch head = repo.Head;
        if (head.IsTracking || repo.Info.IsHeadDetached || repo.Info.IsHeadUnborn)
            return (null, null);
        List<string> remotes = repo.Network.Remotes.Select(r => r.Name).ToList();
        string? remote = remotes.Contains("origin") ? "origin" : remotes.Count == 1 ? remotes[0] : null;
        return remote == null ? (null, null) : (head.FriendlyName, remote);
    }

    /// <summary>The job: runs git in the folder, reports its steps, and throws with git's message when it fails.</summary>
    public static PluginJobRequest Job(string title, string location, string? glyph, string git, string folder,
        IReadOnlyList<string> arguments, Action? done = null) => new()
    {
        Title = title,
        Location = location,
        Glyph = glyph,
        Run = async job =>
        {
            job.Report(new OperationProgress { Kind = OperationKind.Task, Analyzing = true, CurrentSource = title, ItemsTotal = 100 });
            GitResult result;
            try
            {
                result = await GitProgram.RunAsync(git, folder, arguments, job.Cancellation, progress: line =>
                {
                    if (ParseProgress(line) is GitProgressStep step)
                        job.Report(new OperationProgress
                        {
                            Kind = OperationKind.Task,
                            CurrentSource = $"{step.Phase} {step.Percent}%",
                            CurrentBytesDone = step.Done,
                            CurrentBytesTotal = step.Total,
                            ItemsDone = step.Percent,
                            ItemsTotal = 100,
                        });
                });
            }
            finally
            {
                done?.Invoke();
            }
            if (!result.Succeeded)
                throw new InvalidOperationException(LastLines(result.Message));
            job.Report(new OperationProgress { Kind = OperationKind.Task, CurrentSource = title, ItemsDone = 100, ItemsTotal = 100 });
            return new OperationResult { Succeeded = 1 };
        },
    };

    /// <summary>The end of git's message (the reason is last; progress lines before it say nothing).</summary>
    internal static string LastLines(string message)
    {
        string[] lines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => ParseProgress(l) == null).ToArray();
        return string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Length - 4)));
    }
}
