// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using VibeSalamander.Core.Operations;

namespace VibeSalamander.Core.Tests;

/// <summary>
/// The awkward cases of copy / move / delete: links, locked files, hidden targets, long and Unicode names,
/// cancelling in the middle of a file. What matters most: nothing outside the operation is ever deleted, and a
/// move never removes a source that wasn't copied.
/// </summary>
public sealed class FileOperationEdgeCaseTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vibe-edge").FullName;

    public void Dispose()
    {
        try
        {
            DeleteTree(_root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a temp folder left behind is not a test failure
        }
    }

    /// <summary>Deletes a test folder: links are removed without going into them, read-only files are deleted too.</summary>
    private static void DeleteTree(string directory)
    {
        var all = new EnumerationOptions { AttributesToSkip = 0 };
        foreach (string dir in Directory.GetDirectories(directory, "*", all))
        {
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(dir);
            else
                DeleteTree(dir);
        }
        foreach (string file in Directory.GetFiles(directory, "*", all))
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }
        Directory.Delete(directory);
    }

    private string P(string relative) => Path.Combine(_root, relative);

    private string Make(string relative, string content = "x")
    {
        string path = P(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>A directory junction (mklink /J needs no administrator rights, unlike symbolic links).</summary>
    private static void Junction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process process = Process.Start(start)!;
        process.WaitForExit();
        Assert.True(Directory.Exists(link), "the junction could not be created: " + process.StandardError.ReadToEnd());
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
    }

    /// <summary>Answers prompts through functions and counts them.</summary>
    private sealed class Callbacks : IOperationCallbacks
    {
        public Func<ConflictInfo, ConflictChoice> Conflict { get; init; } = _ => ConflictChoice.Overwrite;
        public Func<OperationError, ErrorChoice> Error { get; init; } = _ => ErrorChoice.Skip;
        public List<OperationError> Errors { get; } = new();
        public int Conflicts { get; private set; }

        public Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict)
        {
            Conflicts++;
            return Task.FromResult(Conflict(conflict));
        }

        public Task<ErrorChoice> ResolveErrorAsync(OperationError error)
        {
            Errors.Add(error);
            return Task.FromResult(Error(error));
        }
    }

    /// <summary>Reports synchronously (Progress&lt;T&gt; would post to a thread pool and arrive late).</summary>
    private sealed class SyncProgress(Action<OperationProgress> report) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => report(value);
    }

    // ---- links: nothing outside the operation is deleted ----------------------------------------------

    [Fact]
    public async Task Move_that_copies_a_folder_with_a_junction_keeps_the_linked_folder()
    {
        Make(@"outside\keep.txt", "precious");
        Make(@"src\dir\own.txt", "own");
        Junction(P(@"src\dir\link"), P("outside"));
        Directory.CreateDirectory(P(@"dst\dir")); // an existing folder of the same name: the move copies (merge)

        OperationResult result = await new FileOperationEngine(new Callbacks()).MoveAsync(new[] { P(@"src\dir") }, P("dst"));

        Assert.False(result.Cancelled);
        Assert.Equal("precious", File.ReadAllText(P(@"outside\keep.txt"))); // the folder the link pointed to is untouched
        Assert.Equal("precious", File.ReadAllText(P(@"dst\dir\link\keep.txt"))); // its content was copied
        Assert.Equal("own", File.ReadAllText(P(@"dst\dir\own.txt")));
        Assert.False(Directory.Exists(P(@"src\dir\link"))); // the link itself was moved away
        Assert.False(Directory.Exists(P(@"src\dir")));
    }

    [Fact]
    public async Task Copy_of_a_junction_pointing_back_up_ends_and_reports_it()
    {
        Make(@"src\a.txt", "a");
        Junction(P(@"src\loop"), P("src"));

        var callbacks = new Callbacks();
        OperationResult result = await new FileOperationEngine(callbacks).CopyAsync(new[] { P("src") }, P("dst") + "\\");

        Assert.False(result.Cancelled);
        Assert.Equal("a", File.ReadAllText(P(@"dst\src\a.txt")));
        Assert.Single(callbacks.Errors);
        Assert.False(Directory.Exists(P(@"dst\src\loop\loop")));
    }

    [Fact]
    public async Task Delete_of_a_folder_with_a_junction_keeps_the_linked_folder()
    {
        Make(@"outside\keep.txt", "precious");
        Make(@"del\own.txt");
        Junction(P(@"del\link"), P("outside"));

        OperationResult result = await new FileOperationEngine(new Callbacks()).DeleteAsync(new[] { P("del") });

        Assert.Equal(0, result.Failed);
        Assert.False(Directory.Exists(P("del")));
        Assert.Equal("precious", File.ReadAllText(P(@"outside\keep.txt")));
    }

    // ---- a move never loses its source ------------------------------------------------------------------

    [Fact]
    public async Task Move_retried_after_the_source_was_locked_finishes()
    {
        Make(@"src\a.txt", "new");
        Make(@"dst\a.txt", "old"); // the move has to copy (and then delete the source)
        var locked = new FileStream(P(@"src\a.txt"), FileMode.Open, FileAccess.Read, FileShare.Read); // readable, not deletable
        var callbacks = new Callbacks
        {
            Error = _ =>
            {
                locked.Dispose(); // the program holding it closes it, the user presses Retry
                return ErrorChoice.Retry;
            },
        };
        try
        {
            OperationResult result = await new FileOperationEngine(callbacks).MoveAsync(new[] { P(@"src\a.txt") }, P("dst"));

            Assert.Single(callbacks.Errors);
            Assert.Equal(0, result.Failed);
            Assert.Equal("new", File.ReadAllText(P(@"dst\a.txt")));
            Assert.False(File.Exists(P(@"src\a.txt")));
        }
        finally
        {
            locked.Dispose();
        }
    }

    [Fact]
    public async Task Move_with_skipped_conflicts_keeps_those_sources()
    {
        Make(@"src\dir\a.txt", "new a");
        Make(@"src\dir\b.txt", "new b");
        Make(@"dst\dir\a.txt", "old a");

        OperationResult result = await new FileOperationEngine(new Callbacks { Conflict = _ => ConflictChoice.Skip })
            .MoveAsync(new[] { P(@"src\dir") }, P("dst"));

        Assert.Equal(1, result.Skipped);
        Assert.Equal("old a", File.ReadAllText(P(@"dst\dir\a.txt")));
        Assert.Equal("new a", File.ReadAllText(P(@"src\dir\a.txt"))); // not moved, so not deleted
        Assert.Equal("new b", File.ReadAllText(P(@"dst\dir\b.txt")));
        Assert.False(File.Exists(P(@"src\dir\b.txt")));
    }

    [Fact]
    public async Task Cancel_in_the_middle_of_a_file_leaves_no_partial_copy_and_keeps_the_source()
    {
        string big = P(@"src\dir\big.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(big)!);
        File.WriteAllBytes(big, new byte[48 * 1024 * 1024]);
        Directory.CreateDirectory(P(@"dst\dir")); // the move copies
        using var cancel = new CancellationTokenSource();
        var progress = new SyncProgress(p =>
        {
            if (!p.Analyzing && p.CurrentSource.EndsWith("big.bin", StringComparison.OrdinalIgnoreCase))
                cancel.Cancel(); // as the copy of the big file starts
        });

        OperationResult result = await new FileOperationEngine(new Callbacks(), progress).MoveAsync(new[] { P(@"src\dir") }, P("dst"), cancel.Token);

        Assert.True(result.Cancelled);
        Assert.False(File.Exists(P(@"dst\dir\big.bin")));
        Assert.Equal(48L * 1024 * 1024, new FileInfo(big).Length);
    }

    // ---- targets in the way ------------------------------------------------------------------------------

    [Fact]
    public async Task Overwrite_replaces_hidden_system_and_read_only_targets()
    {
        string a = Make(@"src\a.txt", "new");
        string target = Make(@"dst\a.txt", "old");
        File.SetAttributes(target, FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly);

        var callbacks = new Callbacks();
        OperationResult result = await new FileOperationEngine(callbacks).CopyAsync(new[] { a }, P("dst"));

        Assert.Empty(callbacks.Errors);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal("new", File.ReadAllText(target));
    }

    [Fact]
    public async Task A_file_in_the_way_of_a_folder_is_one_error_and_a_move_keeps_the_folder()
    {
        Make(@"src\dir\a.txt");
        Make(@"src\dir\b.txt");
        Make(@"src\dir\sub\c.txt");
        Make(@"dst\dir", "a file named like the folder");

        var callbacks = new Callbacks();
        OperationResult result = await new FileOperationEngine(callbacks).MoveAsync(new[] { P(@"src\dir") }, P("dst"));

        Assert.Single(callbacks.Errors);
        Assert.True(File.Exists(P(@"src\dir\a.txt")));
        Assert.True(File.Exists(P(@"src\dir\sub\c.txt")));
        Assert.Equal("a file named like the folder", File.ReadAllText(P(@"dst\dir")));
        Assert.Equal(0, result.Succeeded);
    }

    [Fact]
    public async Task Delete_skips_a_locked_file_and_deletes_the_rest()
    {
        Make(@"del\locked.txt");
        Make(@"del\free.txt");
        using (new FileStream(P(@"del\locked.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            OperationResult result = await new FileOperationEngine(new Callbacks()).DeleteAsync(new[] { P("del") });
            Assert.True(result.Failed >= 1);
        }
        Assert.True(File.Exists(P(@"del\locked.txt")));
        Assert.False(File.Exists(P(@"del\free.txt")));
    }

    // ---- names and what the copy keeps ------------------------------------------------------------------

    [Fact]
    public async Task Move_can_change_only_the_capitals_of_a_name()
    {
        string file = Make(@"names\report.txt", "r");
        Directory.CreateDirectory(P(@"names\folder"));
        var engine = new FileOperationEngine(new Callbacks());

        OperationResult a = await engine.MoveAsync(new[] { file }, P(@"names\Report.TXT"));
        OperationResult b = await engine.MoveAsync(new[] { P(@"names\folder") }, P(@"names\FOLDER"));

        Assert.Equal(1, a.Succeeded);
        Assert.Equal(1, b.Succeeded);
        string[] names = Directory.EnumerateFileSystemEntries(P("names")).Select(Path.GetFileName).Order().ToArray()!;
        Assert.Equal(new[] { "FOLDER", "Report.TXT" }, names);
        Assert.Equal("r", File.ReadAllText(P(@"names\Report.TXT")));
    }

    [Fact]
    public async Task Long_paths_are_copied_moved_and_deleted()
    {
        string deep = Path.Combine(Enumerable.Range(1, 7).Select(i => $"level {i} " + new string('x', 40)).ToArray());
        string file = Make(Path.Combine("long", deep, "file.txt"), "deep");
        Assert.True(file.Length > 300);
        var engine = new FileOperationEngine(new Callbacks());

        OperationResult copied = await engine.CopyAsync(new[] { P("long") }, P("copy") + "\\");
        Assert.Equal(0, copied.Failed);
        Assert.Equal("deep", File.ReadAllText(P(Path.Combine("copy", "long", deep, "file.txt"))));

        Directory.CreateDirectory(P(@"moved\long")); // the move copies and deletes
        OperationResult moved = await engine.MoveAsync(new[] { P("long") }, P("moved"));
        Assert.Equal(0, moved.Failed);
        Assert.Equal("deep", File.ReadAllText(P(Path.Combine("moved", "long", deep, "file.txt"))));
        Assert.False(Directory.Exists(P("long")));

        OperationResult deleted = await engine.DeleteAsync(new[] { P("copy") });
        Assert.Equal(0, deleted.Failed);
        Assert.False(Directory.Exists(P("copy")));
    }

    [Fact]
    public async Task Unicode_names_are_copied()
    {
        string name = "zażółć gęślą jaźń – 日本語 😀.txt";
        string file = Make(Path.Combine("ünï", name), "ü");
        await new FileOperationEngine(new Callbacks()).CopyAsync(new[] { P("ünï") }, P("dst") + "\\");
        Assert.Equal("ü", File.ReadAllText(P(Path.Combine("dst", "ünï", name))));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Copies_keep_times_and_attributes()
    {
        string file = Make(@"src\dir\old.txt", "o");
        var time = new DateTime(2020, 5, 17, 10, 30, 0, DateTimeKind.Local);
        File.SetLastWriteTime(file, time);
        File.SetAttributes(file, FileAttributes.ReadOnly | FileAttributes.Hidden);
        Directory.SetLastWriteTime(P(@"src\dir"), time);

        await new FileOperationEngine(new Callbacks()).CopyAsync(new[] { P(@"src\dir") }, P("dst") + "\\");

        string copy = P(@"dst\dir\old.txt");
        Assert.Equal(time, File.GetLastWriteTime(copy));
        Assert.True((File.GetAttributes(copy) & FileAttributes.ReadOnly) != 0);
        Assert.True((File.GetAttributes(copy) & FileAttributes.Hidden) != 0);
        Assert.Equal(time, Directory.GetLastWriteTime(P(@"dst\dir")));
    }
}
