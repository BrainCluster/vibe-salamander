// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using VibeSalamander.Core.Operations;

namespace VibeSalamander.Core.Tests;

/// <summary>Change Case (Ctrl+F7) and Change Attributes (Ctrl+F2): only what was selected changes.</summary>
public sealed class NameOperationEdgeCaseTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vibe-names").FullName;

    public void Dispose()
    {
        try
        {
            DeleteTree(_root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

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

    private string Make(string relative)
    {
        string path = P(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    private static void Junction(string link, string target)
    {
        using Process process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        process.WaitForExit();
        Assert.True(Directory.Exists(link));
    }

    private sealed class Answers : IOperationCallbacks
    {
        public List<OperationError> Errors { get; } = new();
        public Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict) => Task.FromResult(ConflictChoice.Skip);

        public Task<ErrorChoice> ResolveErrorAsync(OperationError error)
        {
            Errors.Add(error);
            return Task.FromResult(ErrorChoice.Skip);
        }
    }

    private static string NameOnDisk(string path) =>
        Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(path)!)
            .Select(Path.GetFileName)
            .Single(n => string.Equals(n, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))!;

    [Fact]
    public async Task Change_case_recursively_renames_the_tree_but_not_through_a_junction()
    {
        Make(@"outside\Keep Me.TXT");
        Make(@"Sel\Sub Dir\Some File.TXT");
        Junction(P(@"Sel\Link"), P("outside"));

        OperationResult result = await NameAndAttributeOperations.ChangeCaseAsync(new[] { P("Sel") }, CaseMode.Lower, CasePart.WholeName,
            includeSubdirectories: true, new Answers());

        Assert.Equal(0, result.Failed);
        Assert.Equal("sel", NameOnDisk(P("sel")));
        Assert.Equal("sub dir", NameOnDisk(P(@"sel\sub dir")));
        Assert.Equal("some file.txt", NameOnDisk(P(@"sel\sub dir\some file.txt")));
        Assert.Equal("link", NameOnDisk(P(@"sel\link"))); // the link itself was selected (inside the folder)
        Assert.Equal("Keep Me.TXT", NameOnDisk(P(@"outside\keep me.txt"))); // but not what it points to
    }

    [Fact]
    public async Task Change_attributes_recursively_doesnt_reach_through_a_junction()
    {
        string outside = Make(@"outside\file.txt");
        string inside = Make(@"sel\file.txt");
        Junction(P(@"sel\link"), P("outside"));

        await NameAndAttributeOperations.ChangeAttributesAsync(new[] { P("sel") },
            new AttributeChange { Set = FileAttributes.Hidden, IncludeSubdirectories = true }, new Answers());

        Assert.True((File.GetAttributes(inside) & FileAttributes.Hidden) != 0);
        Assert.True((File.GetAttributes(outside) & FileAttributes.Hidden) == 0);
    }

    [Fact]
    public async Task A_time_that_cant_be_set_is_one_error_and_keeps_read_only()
    {
        string file = Make("ro.txt");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        var answers = new Answers();

        OperationResult result = await NameAndAttributeOperations.ChangeAttributesAsync(new[] { file },
            new AttributeChange { LastWriteTime = new DateTime(1500, 1, 1) }, answers); // before 1601: Windows can't store it

        Assert.Single(answers.Errors);
        Assert.Equal(1, result.Failed);
        Assert.True((File.GetAttributes(file) & FileAttributes.ReadOnly) != 0);
    }

    [Fact]
    public async Task Change_case_of_extension_only_and_of_a_locked_file()
    {
        string doc = Make("Report.Final.DOCX");
        string locked = Make("Locked.TXT");
        var answers = new Answers();
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            OperationResult result = await NameAndAttributeOperations.ChangeCaseAsync(new[] { doc, locked }, CaseMode.Lower, CasePart.ExtensionOnly,
                includeSubdirectories: false, answers);
            Assert.Equal(1, result.Succeeded);
            Assert.Equal(1, result.Failed);
        }
        Assert.Equal("Report.Final.docx", NameOnDisk(doc));
        Assert.Equal("Locked.TXT", NameOnDisk(locked)); // unchanged, nothing lost
        Assert.Single(answers.Errors);
    }

    [Theory]
    [InlineData("my holiday PHOTOS.JPG", CaseMode.CapitalizeWords, CasePart.WholeName, "My Holiday Photos.Jpg")]
    [InlineData("my holiday PHOTOS.JPG", CaseMode.Capitalize, CasePart.WholeName, "My holiday photos.jpg")]
    [InlineData("2026 trip.TXT", CaseMode.Capitalize, CasePart.NameOnly, "2026 Trip.TXT")]
    [InlineData(".gitignore", CaseMode.Upper, CasePart.NameOnly, ".GITIGNORE")]
    [InlineData("archive.tar.GZ", CaseMode.Upper, CasePart.ExtensionOnly, "archive.tar.GZ")]
    [InlineData("ŽUTI ćup.TXT", CaseMode.Lower, CasePart.WholeName, "žuti ćup.txt")]
    public void Case_rules(string name, CaseMode mode, CasePart part, string expected) =>
        Assert.Equal(expected, NameAndAttributeOperations.ApplyCase(name, mode, part, isDirectory: false));
}
