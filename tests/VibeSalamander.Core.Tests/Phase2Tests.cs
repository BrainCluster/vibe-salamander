// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Operations;
using VibeSalamander.Core.Search;

namespace VibeSalamander.Core.Tests;

public class WildcardMaskTests
{
    [Theory]
    [InlineData("*.*", "readme", true)]
    [InlineData("*.*", "a.txt", true)]
    [InlineData("*.txt", "A.TXT", true)]
    [InlineData("*.txt", "a.txt.bak", false)]
    [InlineData("*.txt;*.doc", "x.doc", true)]
    [InlineData("*.*|*.bak", "x.bak", false)]
    [InlineData("*.*|*.bak", "x.txt", true)]
    [InlineData("|*.tmp", "x.txt", true)]
    [InlineData("report??.xls", "report01.xls", true)]
    [InlineData("report??.xls", "report1.xls", false)]
    [InlineData("name.*", "name", true)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    public void Matches(string mask, string name, bool expected) =>
        Assert.Equal(expected, new WildcardMask(mask).IsMatch(name));
}

public class CaseTests
{
    [Theory]
    [InlineData("My File.TXT", CaseMode.Lower, CasePart.WholeName, "my file.txt")]
    [InlineData("my file.txt", CaseMode.Upper, CasePart.NameOnly, "MY FILE.txt")]
    [InlineData("MY FILE.TXT", CaseMode.Capitalize, CasePart.WholeName, "My file.txt")]
    [InlineData("my holiday photos.JPG", CaseMode.CapitalizeWords, CasePart.NameOnly, "My Holiday Photos.JPG")]
    [InlineData("readme.TXT", CaseMode.Lower, CasePart.ExtensionOnly, "readme.txt")]
    public void ApplyCase(string name, CaseMode mode, CasePart part, string expected) =>
        Assert.Equal(expected, NameAndAttributeOperations.ApplyCase(name, mode, part, isDirectory: false));
}

/// <summary>Answers every prompt with fixed choices.</summary>
internal sealed class FixedCallbacks : IOperationCallbacks
{
    public ConflictChoice Conflict { get; init; } = ConflictChoice.Overwrite;
    public ErrorChoice Error { get; init; } = ErrorChoice.Skip;
    public int Conflicts { get; private set; }

    public Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict)
    {
        Conflicts++;
        return Task.FromResult(Conflict);
    }

    public Task<ErrorChoice> ResolveErrorAsync(OperationError error) => Task.FromResult(Error);
}

public sealed class EngineTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vibe-ops").FullName;

    public void Dispose()
    {
        foreach (string f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    private string Make(string relative, string content = "x")
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Copy_DirectoryTree_IntoExistingDirectory()
    {
        Make(@"src\a.txt", "hello");
        Make(@"src\sub\b.txt", "world");
        string target = Directory.CreateDirectory(Path.Combine(_root, "dst")).FullName;

        var result = await new FileOperationEngine(new FixedCallbacks()).CopyAsync(new[] { Path.Combine(_root, "src") }, target);

        Assert.False(result.Cancelled);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(target, @"src\a.txt")));
        Assert.Equal("world", File.ReadAllText(Path.Combine(target, @"src\sub\b.txt")));
        Assert.True(File.Exists(Path.Combine(_root, @"src\a.txt"))); // copy keeps the source
    }

    [Fact]
    public async Task Copy_SingleFile_ToNewName()
    {
        string a = Make("a.txt", "data");
        string b = Path.Combine(_root, "b.txt");
        await new FileOperationEngine(new FixedCallbacks()).CopyAsync(new[] { a }, b);
        Assert.Equal("data", File.ReadAllText(b));
    }

    [Fact]
    public async Task Copy_Conflict_SkipKeepsTarget()
    {
        string a = Make(@"src\a.txt", "new");
        Make(@"dst\a.txt", "old");
        var callbacks = new FixedCallbacks { Conflict = ConflictChoice.Skip };
        var result = await new FileOperationEngine(callbacks).CopyAsync(new[] { a }, Path.Combine(_root, "dst"));
        Assert.Equal(1, callbacks.Conflicts);
        Assert.Equal(1, result.Skipped);
        Assert.Equal("old", File.ReadAllText(Path.Combine(_root, @"dst\a.txt")));
    }

    [Fact]
    public async Task Copy_Conflict_RenameKeepsBoth()
    {
        string a = Make(@"src\a.txt", "new");
        Make(@"dst\a.txt", "old");
        await new FileOperationEngine(new FixedCallbacks { Conflict = ConflictChoice.Rename }).CopyAsync(new[] { a }, Path.Combine(_root, "dst"));
        Assert.Equal("new", File.ReadAllText(Path.Combine(_root, @"dst\a (2).txt")));
    }

    [Fact]
    public async Task Copy_Conflict_OverwriteAllAsksOnce()
    {
        string a = Make(@"src\a.txt", "new a");
        string b = Make(@"src\b.txt", "new b");
        Make(@"dst\a.txt", "old");
        string ro = Make(@"dst\b.txt", "old");
        File.SetAttributes(ro, FileAttributes.ReadOnly);
        var callbacks = new FixedCallbacks { Conflict = ConflictChoice.OverwriteAll };
        await new FileOperationEngine(callbacks).CopyAsync(new[] { a, b }, Path.Combine(_root, "dst"));
        Assert.Equal(1, callbacks.Conflicts);
        Assert.Equal("new b", File.ReadAllText(ro));
    }

    [Fact]
    public async Task Move_SameVolume_Renames()
    {
        Make(@"src\dir\f.txt", "m");
        string target = Directory.CreateDirectory(Path.Combine(_root, "dst")).FullName;
        var result = await new FileOperationEngine(new FixedCallbacks()).MoveAsync(new[] { Path.Combine(_root, @"src\dir") }, target);
        Assert.Equal(1, result.Succeeded);
        Assert.False(Directory.Exists(Path.Combine(_root, @"src\dir")));
        Assert.Equal("m", File.ReadAllText(Path.Combine(target, @"dir\f.txt")));
    }

    [Fact]
    public async Task Copy_DirectoryIntoItself_IsRefused()
    {
        Make(@"src\a.txt");
        string src = Path.Combine(_root, "src");
        var result = await new FileOperationEngine(new FixedCallbacks()).CopyAsync(new[] { src }, Path.Combine(src, "inner"));
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public async Task DeletePermanently_RemovesTreeWithReadOnlyFiles()
    {
        string f = Make(@"del\sub\r.txt");
        File.SetAttributes(f, FileAttributes.ReadOnly);
        var result = await new FileOperationEngine(new FixedCallbacks()).DeleteAsync(new[] { Path.Combine(_root, "del") });
        Assert.False(result.Cancelled);
        Assert.False(Directory.Exists(Path.Combine(_root, "del")));
    }

    [Fact]
    public async Task Search_ByMaskAndContent()
    {
        Make(@"s\one.txt", "the quick brown fox");
        Make(@"s\deep\two.txt", "LAZY dog");
        Make(@"s\three.log", "lazy");
        var found = new List<string>();

        await FileSearcher.SearchAsync(new SearchCriteria
        {
            Directories = new[] { Path.Combine(_root, "s") },
            Mask = new WildcardMask("*.txt"),
            Texts = new[] { new TextCondition("lazy") },
        }, h => { lock (found) found.Add(h.Entry.Name); }, null, CancellationToken.None);

        Assert.Equal(new[] { "two.txt" }, found);
    }
}
