// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using VibeSalamander.Core.Interop;
using VibeSalamander.Core.Operations;

namespace VibeSalamander.Core.Tests;

/// <summary>
/// The data-safety matrix of the copy engine (roadmap T2): the combinations in which a file manager can lose files.
/// Whatever the user answers and whatever goes wrong, two things hold: a move never deletes a source that didn't
/// arrive complete, and nothing is overwritten or deleted that the user didn't choose to overwrite or delete.
/// (FileOperationEdgeCaseTests has the single awkward cases; this file has the combinations.)
/// A full target disk is in FullDiskTests (it needs a small volume of its own).
/// </summary>
public sealed class FileOperationSafetyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vibe-safety").FullName;

    public void Dispose()
    {
        try
        {
            TestFiles.DeleteTree(_root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a temp folder left behind is not a test failure
        }
    }

    // ---- the same file through another path --------------------------------------------------------------

    [Fact]
    public async Task Copy_onto_the_same_file_through_a_junction_changes_nothing()
    {
        string file = Make(@"real\a.txt", "precious");
        TestFiles.Junction(P("alias"), P("real"));
        var callbacks = new OperationAnswers(); // would answer Overwrite

        OperationResult result = await new FileOperationEngine(callbacks).CopyAsync(new[] { file }, P("alias"));

        Assert.Single(callbacks.Errors);
        Assert.Equal(0, callbacks.Conflicts); // not offered as an ordinary "overwrite?"
        Assert.Equal(0, result.Succeeded);
        Assert.Equal("precious", File.ReadAllText(file));
    }

    [Fact]
    public async Task Move_onto_the_same_file_through_a_junction_keeps_it()
    {
        string file = Make(@"real\a.txt", "precious");
        TestFiles.Junction(P("alias"), P("real"));
        var callbacks = new OperationAnswers();

        OperationResult result = await new FileOperationEngine(callbacks).MoveAsync(new[] { file }, P("alias"));

        Assert.Single(callbacks.Errors);
        Assert.Equal(0, callbacks.Conflicts);
        Assert.Equal(0, result.Succeeded);
        Assert.Equal("precious", File.ReadAllText(file));
    }

    [Fact]
    public async Task Move_of_a_folder_onto_itself_through_a_junction_keeps_everything()
    {
        Make(@"real\dir\a.txt", "a");
        Make(@"real\dir\sub\b.txt", "b");
        TestFiles.Junction(P("alias"), P("real"));
        var callbacks = new OperationAnswers();

        // "alias\dir" exists and is "real\dir": a merge would copy every file onto itself and then delete it
        OperationResult result = await new FileOperationEngine(callbacks).MoveAsync(new[] { P(@"real\dir") }, P("alias"));

        Assert.NotEmpty(callbacks.Errors);
        Assert.Equal(0, result.Succeeded);
        Assert.Equal("a", File.ReadAllText(P(@"real\dir\a.txt")));
        Assert.Equal("b", File.ReadAllText(P(@"real\dir\sub\b.txt")));
    }

    [Fact]
    public async Task Files_reached_through_a_link_inside_a_merged_folder_are_never_copied_onto_themselves()
    {
        // dst\dir\link leads to src\dir\data: merging src\dir into dst\dir meets the same files under two names
        Make(@"src\dir\data\keep.txt", "precious");
        Directory.CreateDirectory(P(@"dst\dir"));
        TestFiles.Junction(P(@"dst\dir\data"), P(@"src\dir\data"));
        var callbacks = new OperationAnswers();

        OperationResult result = await new FileOperationEngine(callbacks).MoveAsync(new[] { P(@"src\dir") }, P("dst"));

        Assert.NotEmpty(callbacks.Errors);
        Assert.Equal(0, callbacks.Conflicts); // the files under the link are the sources themselves, not targets to overwrite
        Assert.Equal("precious", File.ReadAllText(P(@"src\dir\data\keep.txt")));
        Assert.False(result.Cancelled);
    }

    [Fact]
    public async Task Move_onto_a_hard_link_of_the_same_file_keeps_the_data()
    {
        string file = Make(@"src\a.txt", "precious");
        Directory.CreateDirectory(P("dst"));
        TestFiles.HardLink(P(@"dst\a.txt"), file);
        var callbacks = new OperationAnswers();

        OperationResult result = await new FileOperationEngine(callbacks).MoveAsync(new[] { file }, P("dst"));

        Assert.Single(callbacks.Errors);
        Assert.Equal(0, result.Succeeded);
        Assert.Equal("precious", File.ReadAllText(file));
        Assert.Equal("precious", File.ReadAllText(P(@"dst\a.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_folder_is_never_copied_or_moved_into_itself_through_a_link(bool move)
    {
        Make(@"src\dir\a.txt", "a");
        Directory.CreateDirectory(P(@"src\dir\sub"));
        TestFiles.Junction(P("into"), P(@"src\dir\sub")); // "into" is a folder inside the source
        var callbacks = new OperationAnswers();
        var engine = new FileOperationEngine(callbacks);

        OperationResult result = move
            ? await engine.MoveAsync(new[] { P(@"src\dir") }, P("into"))
            : await engine.CopyAsync(new[] { P(@"src\dir") }, P("into"));

        Assert.Single(callbacks.Errors);
        Assert.Equal(0, result.Succeeded);
        Assert.False(Directory.Exists(P(@"src\dir\sub\dir")));
        Assert.Equal("a", File.ReadAllText(P(@"src\dir\a.txt")));
    }

    [Fact]
    public async Task A_folder_is_never_moved_into_itself()
    {
        Make(@"src\dir\a.txt", "a");
        var callbacks = new OperationAnswers();

        OperationResult result = await new FileOperationEngine(callbacks).MoveAsync(new[] { P(@"src\dir") }, P(@"src\dir\inner"));

        Assert.Single(callbacks.Errors);
        Assert.Equal(0, result.Succeeded);
        Assert.Equal("a", File.ReadAllText(P(@"src\dir\a.txt")));
    }

    [Fact]
    public void Identity_tells_links_and_hard_links_from_other_files()
    {
        string a = Make(@"id\a.txt", "a");
        string b = Make(@"id\b.txt", "a");
        TestFiles.HardLink(P(@"id\a-link.txt"), a);
        TestFiles.Junction(P("id-alias"), P("id"));

        Assert.True(FileIdentity.IsSame(a, P(@"id\a-link.txt")));
        Assert.True(FileIdentity.IsSame(a, P(@"id-alias\a.txt")));
        Assert.True(FileIdentity.IsSame(P("id"), P("id-alias")));
        Assert.False(FileIdentity.IsSame(a, b)); // the same content is not the same file
        Assert.False(FileIdentity.IsSame(a, P(@"id\missing.txt")));
        Assert.True(FileIdentity.IsSameOrInside(P(@"id-alias\new\deeper"), P("id")));
        Assert.False(FileIdentity.IsSameOrInside(P(@"other\new"), P("id")));
    }

    // ---- every answer to "the target exists" ----------------------------------------------------------

    public static IEnumerable<object[]> ConflictAnswers() =>
        from choice in Enum.GetValues<ConflictChoice>()
        from move in new[] { false, true }
        select new object[] { choice, move };

    /// <summary>
    /// src\dir has a.txt, b.txt and c.txt; dst\dir already has an older a.txt and a newer b.txt. Every answer, for a copy
    /// and for a move (which copies here: the folder exists), keeps every source's content somewhere, and replaces an
    /// old target only when the answer overwrites.
    /// </summary>
    [Theory]
    [MemberData(nameof(ConflictAnswers))]
    public async Task Every_conflict_answer_keeps_every_file_it_should(ConflictChoice answer, bool move)
    {
        var older = new DateTime(2020, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var source = new DateTime(2022, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        foreach (string name in new[] { "a", "b", "c" })
            File.SetLastWriteTimeUtc(Make($@"src\dir\{name}.txt", "new " + name), source);
        File.SetLastWriteTimeUtc(Make(@"dst\dir\a.txt", "old a"), older);
        File.SetLastWriteTimeUtc(Make(@"dst\dir\b.txt", "old b"), newer);
        var callbacks = new OperationAnswers { Conflict = _ => answer };
        var engine = new FileOperationEngine(callbacks);

        OperationResult result = move
            ? await engine.MoveAsync(new[] { P(@"src\dir") }, P("dst"))
            : await engine.CopyAsync(new[] { P(@"src\dir") }, P("dst"));

        Dictionary<string, string> after = Contents();
        // Resume (offered only by plugins for servers) overwrites here
        bool overwritesA = answer is ConflictChoice.Overwrite or ConflictChoice.OverwriteAll or ConflictChoice.OverwriteAllOlder
            or ConflictChoice.Resume or ConflictChoice.ResumeAll;
        bool overwritesB = answer is ConflictChoice.Overwrite or ConflictChoice.OverwriteAll // b's target is newer
            or ConflictChoice.Resume or ConflictChoice.ResumeAll;
        bool cancelled = answer == ConflictChoice.Cancel;

        // 1. no source content is lost
        foreach (string name in new[] { "a", "b", "c" })
            Assert.True(after.ContainsValue("new " + name), $"\"new {name}\" is gone: {Describe(after)}");
        // 2. an old target is replaced only when the answer overwrites it
        Assert.Equal(!overwritesA || cancelled, after.ContainsValue("old a"));
        Assert.Equal(!overwritesB || cancelled, after.ContainsValue("old b"));
        // 3. a copy never touches its sources
        if (!move)
            foreach (string name in new[] { "a", "b", "c" })
                Assert.Equal("new " + name, after[$@"src\dir\{name}.txt"]);
        // 4. a move removes exactly the sources that arrived
        if (move)
        {
            Assert.Equal(cancelled || !overwritesA && answer is ConflictChoice.Skip or ConflictChoice.SkipAll, after.ContainsKey(@"src\dir\a.txt"));
            Assert.Equal(cancelled || !overwritesB && answer is not (ConflictChoice.Rename or ConflictChoice.RenameAll), after.ContainsKey(@"src\dir\b.txt"));
            Assert.Equal(cancelled, after.ContainsKey(@"src\dir\c.txt"));
            Assert.Equal(!cancelled && answer is not (ConflictChoice.Skip or ConflictChoice.SkipAll or ConflictChoice.OverwriteAllOlder),
                !Directory.Exists(P(@"src\dir")));
        }
        Assert.Equal(cancelled, result.Cancelled);
        Assert.Empty(callbacks.Errors);
    }

    // ---- what goes wrong in the middle ------------------------------------------------------------------

    [Fact]
    public async Task A_copy_that_fails_verification_is_not_left_behind_and_retry_copies_again()
    {
        string file = Make(@"src\a.txt", "content");
        int checks = 0;
        var callbacks = new OperationAnswers { Error = _ => ErrorChoice.Retry };
        var engine = new FileOperationEngine(callbacks)
        {
            Options = new EngineOptions { VerifyCopies = true },
            ContentCheck = (_, _, _) => ++checks > 1, // the first copy "differs"
        };

        OperationResult result = await engine.CopyAsync(new[] { file }, P("dst") + "\\");

        Assert.Single(callbacks.Errors);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(0, result.Failed);
        Assert.Equal("content", File.ReadAllText(P(@"dst\a.txt")));
    }

    [Fact]
    public async Task A_move_whose_copy_fails_verification_keeps_the_source_and_leaves_no_bad_copy()
    {
        string file = Make(@"src\dir\a.txt", "content");
        File.SetAttributes(file, FileAttributes.ReadOnly); // the bad copy is read-only too, and still goes
        Directory.CreateDirectory(P(@"dst\dir")); // the move copies
        var callbacks = new OperationAnswers { Error = _ => ErrorChoice.Skip };
        var engine = new FileOperationEngine(callbacks)
        {
            Options = new EngineOptions { VerifyCopies = true },
            ContentCheck = (_, _, _) => false,
        };

        OperationResult result = await engine.MoveAsync(new[] { P(@"src\dir") }, P("dst"));

        Assert.Single(callbacks.Errors);
        Assert.Equal(1, result.Failed);
        Assert.Equal("content", File.ReadAllText(file));
        Assert.False(File.Exists(P(@"dst\dir\a.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_source_that_disappears_during_the_operation_is_one_error_and_the_rest_arrives(bool move)
    {
        Make(@"src\dir\a.txt", "a");
        Make(@"src\dir\b.txt", "b");
        Make(@"src\dir\c.txt", "c");
        Directory.CreateDirectory(P(@"dst\dir")); // a move copies
        bool deleted = false;
        var progress = new ImmediateProgress(p =>
        {
            if (!deleted && !p.Analyzing && p.CurrentSource.EndsWith("a.txt", StringComparison.OrdinalIgnoreCase))
            {
                deleted = true;
                File.Delete(P(@"src\dir\b.txt")); // another program deletes it after the plan was made
            }
        });
        var callbacks = new OperationAnswers();
        var engine = new FileOperationEngine(callbacks, progress);

        OperationResult result = move
            ? await engine.MoveAsync(new[] { P(@"src\dir") }, P("dst"))
            : await engine.CopyAsync(new[] { P(@"src\dir") }, P("dst"));

        Assert.Single(callbacks.Errors);
        Assert.Equal(1, result.Failed);
        Assert.Equal(2, result.Succeeded);
        Assert.Equal("a", File.ReadAllText(P(@"dst\dir\a.txt")));
        Assert.Equal("c", File.ReadAllText(P(@"dst\dir\c.txt")));
        Assert.False(File.Exists(P(@"dst\dir\b.txt")));
    }

    [Fact]
    public async Task A_locked_target_keeps_its_old_content_and_a_move_keeps_the_source()
    {
        string file = Make(@"src\a.txt", "new");
        string target = Make(@"dst\a.txt", "old");
        var callbacks = new OperationAnswers(); // Overwrite, then Skip the error
        OperationResult result;
        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read)) // open in another program
            result = await new FileOperationEngine(callbacks).MoveAsync(new[] { file }, P("dst"));

        Assert.Single(callbacks.Errors);
        Assert.Equal(1, result.Failed);
        Assert.Equal("new", File.ReadAllText(file));
        Assert.Equal("old", File.ReadAllText(target));
    }

    [Fact]
    public async Task A_skipped_merge_keeps_the_source_folder_of_a_move()
    {
        Make(@"src\dir\a.txt", "a");
        Make(@"dst\dir\b.txt", "b");
        var callbacks = new OperationAnswers { Conflict = c => c.IsMerge ? ConflictChoice.Skip : ConflictChoice.Overwrite };
        var engine = new FileOperationEngine(callbacks) { Options = new EngineOptions { ConfirmDirectoryMerge = true } };

        OperationResult result = await engine.MoveAsync(new[] { P(@"src\dir") }, P("dst"));

        Assert.Equal(1, callbacks.Conflicts);
        Assert.Equal(1, result.Skipped);
        Assert.Equal("a", File.ReadAllText(P(@"src\dir\a.txt")));
        Assert.False(File.Exists(P(@"dst\dir\a.txt")));
        Assert.Equal("b", File.ReadAllText(P(@"dst\dir\b.txt")));
    }

    [Fact]
    public async Task Pause_inside_a_file_and_resume_finishes_the_copy_intact()
    {
        byte[] data = new byte[16 * 1024 * 1024];
        new Random(42).NextBytes(data);
        Directory.CreateDirectory(P("src"));
        File.WriteAllBytes(P(@"src\big.bin"), data);
        FileOperationEngine? engine = null;
        bool paused = false;
        var progress = new ImmediateProgress(p =>
        {
            if (paused || p.Analyzing || !p.CurrentSource.EndsWith("big.bin", StringComparison.OrdinalIgnoreCase))
                return;
            paused = true;
            FileOperationEngine running = engine!;
            running.Paused = true; // as the copy of the file starts: it waits inside the copy
            _ = Task.Run(async () =>
            {
                await Task.Delay(400);
                running.Paused = false;
            });
        });
        engine = new FileOperationEngine(new OperationAnswers(), progress);
        var clock = Stopwatch.StartNew();

        OperationResult result = await engine.CopyAsync(new[] { P(@"src\big.bin") }, P("dst") + "\\");

        Assert.True(paused);
        Assert.True(clock.ElapsedMilliseconds >= 350, $"the copy didn't wait: {clock.ElapsedMilliseconds} ms");
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(data, File.ReadAllBytes(P(@"dst\big.bin")));
    }

    // ---- what the copy keeps ------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Alternate_data_streams_arrive_with_the_file(bool move)
    {
        string file = Make(@"src\dir\a.txt", "main");
        File.WriteAllText(file + ":meta", "stream"); // as the Zone.Identifier of a download
        Directory.CreateDirectory(P(@"dst\dir")); // a move copies
        var engine = new FileOperationEngine(new OperationAnswers());

        if (move)
            await engine.MoveAsync(new[] { P(@"src\dir") }, P("dst"));
        else
            await engine.CopyAsync(new[] { P(@"src\dir") }, P("dst"));

        Assert.Equal("main", File.ReadAllText(P(@"dst\dir\a.txt")));
        Assert.Equal("stream", File.ReadAllText(P(@"dst\dir\a.txt") + ":meta"));
        Assert.Equal(!move, File.Exists(file));
    }

    [Fact]
    public async Task A_read_only_source_is_moved_and_its_copy_stays_read_only()
    {
        string file = Make(@"src\dir\ro.txt", "r");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        Directory.CreateDirectory(P(@"dst\dir")); // the move copies, then deletes the read-only source
        var callbacks = new OperationAnswers();

        OperationResult result = await new FileOperationEngine(callbacks).MoveAsync(new[] { P(@"src\dir") }, P("dst"));

        Assert.Empty(callbacks.Errors);
        Assert.Equal(1, result.Succeeded);
        Assert.False(File.Exists(file));
        Assert.Equal("r", File.ReadAllText(P(@"dst\dir\ro.txt")));
        Assert.True((File.GetAttributes(P(@"dst\dir\ro.txt")) & FileAttributes.ReadOnly) != 0);
    }

    [Fact]
    public async Task A_folder_named_in_other_capitals_merges_into_the_existing_one()
    {
        Make(@"src\Photos\a.jpg", "a");
        Make(@"dst\photos\b.jpg", "b");
        var callbacks = new OperationAnswers();

        OperationResult result = await new FileOperationEngine(callbacks).MoveAsync(new[] { P(@"src\Photos") }, P("dst"));

        Assert.Empty(callbacks.Errors);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal("a", File.ReadAllText(P(@"dst\photos\a.jpg")));
        Assert.Equal("b", File.ReadAllText(P(@"dst\photos\b.jpg")));
        Assert.False(Directory.Exists(P(@"src\Photos")));
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private string P(string relative) => Path.Combine(_root, relative);

    private string Make(string relative, string content = "x")
    {
        string path = P(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Every file under the test folder (links not followed) with its content, by relative path.</summary>
    private Dictionary<string, string> Contents()
    {
        var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Walk(string dir)
        {
            foreach (string sub in Directory.GetDirectories(dir))
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0)
                    Walk(sub);
            foreach (string file in Directory.GetFiles(dir))
                all[Path.GetRelativePath(_root, file)] = File.ReadAllText(file);
        }
        Walk(_root);
        return all;
    }

    private static string Describe(Dictionary<string, string> files) =>
        string.Join("; ", files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).Select(f => $"{f.Key} = {f.Value}"));
}
