// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using VibeSalamander.Core.Operations;

namespace VibeSalamander.Core.Tests;

/// <summary>Runs only as administrator (making a virtual disk needs it): the build machines are.</summary>
public sealed class AdministratorFactAttribute : FactAttribute
{
    public AdministratorFactAttribute()
    {
        if (!Environment.IsPrivilegedProcess)
            Skip = "needs administrator rights (makes a small virtual disk)";
    }
}

/// <summary>
/// The last case of the copy engine's safety matrix (roadmap T2): a target disk that fills up. A 16 MB virtual disk
/// with a drive letter of its own; a file too large for it fails with "not enough space", leaves no partial copy, and
/// a move keeps its source. The question before the copy ("not enough space: copy anyway?") comes first.
/// </summary>
public sealed class FullDiskTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vibe-fulldisk").FullName;
    private string? _vhd;
    private string? _drive;

    public void Dispose()
    {
        if (_vhd != null)
        {
            DiskPart($"select vdisk file=\"{_vhd}\"", "detach vdisk");
            try
            {
                File.Delete(_vhd);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        try
        {
            TestFiles.DeleteTree(_root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string P(string relative) => Path.Combine(_root, relative);

    private string Make(string relative, int megabytes, byte fill)
    {
        string path = P(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] data = new byte[megabytes * 1024 * 1024];
        Array.Fill(data, fill);
        File.WriteAllBytes(path, data);
        return path;
    }

    /// <summary>A new, empty 16 MB NTFS disk; returns its root ("Q:\").</summary>
    private string SmallDisk()
    {
        char letter = "QRSTUVWXYZPONMLKJIHG".First(c => !Directory.Exists(c + @":\") && !DriveInfo.GetDrives().Any(d => d.Name[0] == c));
        _vhd = P("small.vhdx");
        DiskPart(
            $"create vdisk file=\"{_vhd}\" maximum=16 type=expandable",
            $"select vdisk file=\"{_vhd}\"",
            "attach vdisk",
            "create partition primary",
            "format fs=ntfs quick label=VibeFull",
            $"assign letter={letter}");
        _drive = letter + @":\";
        for (int i = 0; i < 50 && !Directory.Exists(_drive); i++)
            Thread.Sleep(100);
        Assert.True(Directory.Exists(_drive), "the virtual disk didn't appear as " + _drive);
        return _drive;
    }

    private static void DiskPart(params string[] commands)
    {
        string script = Path.GetTempFileName();
        File.WriteAllLines(script, commands);
        try
        {
            using Process? run = Process.Start(new ProcessStartInfo("diskpart.exe", $"/s \"{script}\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            });
            run!.StandardOutput.ReadToEnd();
            run.WaitForExit(120000);
        }
        finally
        {
            File.Delete(script);
        }
    }

    [AdministratorFact]
    public async Task A_file_too_large_for_the_disk_fails_without_a_partial_copy_and_the_rest_arrives()
    {
        string disk = SmallDisk();
        string a = Make(@"src\a.bin", 1, 1);
        string big = Make(@"src\big.bin", 30, 2);
        string b = Make(@"src\b.bin", 1, 3);
        var answers = new OperationAnswers(); // copy anyway; Skip the error

        OperationResult result = await new FileOperationEngine(answers).CopyAsync(new[] { a, big, b }, disk);

        Assert.Single(answers.Confirmations); // asked before: not enough space
        Assert.Equal(2, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.Equal(big, Assert.Single(answers.Errors).Path);
        Assert.False(File.Exists(Path.Combine(disk, "big.bin")), "a partial copy of big.bin was left on the full disk");
        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(Path.Combine(disk, "a.bin")));
        Assert.Equal(File.ReadAllBytes(b), File.ReadAllBytes(Path.Combine(disk, "b.bin")));
    }

    [AdministratorFact]
    public async Task A_move_onto_a_full_disk_keeps_the_source_that_did_not_fit()
    {
        string disk = SmallDisk();
        string a = Make(@"src\a.bin", 1, 1);
        string big = Make(@"src\big.bin", 30, 2);
        byte[] bigContent = File.ReadAllBytes(big);
        var answers = new OperationAnswers();

        OperationResult result = await new FileOperationEngine(answers).MoveAsync(new[] { P("src") }, disk);

        Assert.Equal(1, result.Failed);
        Assert.False(File.Exists(a)); // moved
        Assert.True(File.Exists(Path.Combine(disk, @"src\a.bin")));
        Assert.Equal(bigContent, File.ReadAllBytes(big)); // didn't fit: still where it was, whole
        Assert.False(File.Exists(Path.Combine(disk, @"src\big.bin")));
    }

    [AdministratorFact]
    public async Task Saying_no_to_the_space_question_copies_nothing()
    {
        string disk = SmallDisk();
        string big = Make(@"src\big.bin", 30, 2);
        var answers = new OperationAnswers { Confirm = _ => false };

        OperationResult result = await new FileOperationEngine(answers).CopyAsync(new[] { big }, disk);

        Assert.Single(answers.Confirmations);
        Assert.True(result.Cancelled);
        Assert.Empty(Directory.EnumerateFileSystemEntries(disk).Where(e => !Path.GetFileName(e).StartsWith("System Volume", StringComparison.OrdinalIgnoreCase)
            && !Path.GetFileName(e).StartsWith("$", StringComparison.Ordinal)));
    }
}
