// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using VibeSalamander.Core.Operations;
using VibeSalamander.Core.Operations.Elevation;

namespace VibeSalamander.Core.Tests;

/// <summary>
/// Continuing as administrator where access is denied (roadmap T3). The build machine runs as administrator and can't
/// show Windows' prompt, so: the engine is tested with a "denied" folder and a stand-in for the elevation; the helper's
/// protocol in this process; and the real helper program started without administrator rights.
/// </summary>
public sealed class ElevationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vibe-elevation").FullName;

    public void Dispose()
    {
        try
        {
            AllowAgain(_root);
            TestFiles.DeleteTree(_root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string P(string relative) => Path.Combine(_root, relative);

    private string Make(string relative, string content = "x")
    {
        string path = P(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // ---- which failures ---------------------------------------------------------------------------------

    [Fact]
    public void Only_access_denied_is_for_an_administrator()
    {
        Assert.True(ElevationRules.IsAccessDenied(new UnauthorizedAccessException()));
        Assert.True(ElevationRules.IsAccessDenied(new Win32Exception(5)));
        Assert.True(ElevationRules.IsAccessDenied(new Win32Exception(1314)));
        Assert.True(ElevationRules.IsAccessDenied(new IOException("denied", unchecked((int)0x80070005))));
        Assert.False(ElevationRules.IsAccessDenied(new IOException("in use", unchecked((int)0x80070020)))); // sharing violation
        Assert.False(ElevationRules.IsAccessDenied(new FileNotFoundException()));
        Assert.False(ElevationRules.IsAccessDenied(new Win32Exception(32)));
        Assert.False(ElevationRules.IsAccessDenied(new IOException("disk full", unchecked((int)0x80070070))));
    }

    [Theory]
    [InlineData(@"C:\Program Files\App\a.txt", true)]
    [InlineData(@"\\server\share\a.txt", true)]
    [InlineData(@"\\?\C:\a\very\long\path.txt", true)]
    [InlineData(@"\\?\UNC\server\share\a.txt", true)]
    [InlineData(@"a.txt", false)]
    [InlineData(@"..\a.txt", false)]
    [InlineData(@"\a.txt", false)]
    [InlineData(@"C:a.txt", false)]
    [InlineData(@"\\.\PhysicalDrive0", false)]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1\a.txt", false)]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\a.txt", false)]
    [InlineData("", false)]
    public void The_helper_takes_only_full_paths_to_a_drive_or_a_share(string path, bool accepted)
    {
        if (accepted)
            Assert.Equal(path, ElevatedServer.CheckPath(path));
        else
            Assert.Throws<ArgumentException>(() => ElevatedServer.CheckPath(path));
    }

    // ---- the engine -------------------------------------------------------------------------------------

    [Fact]
    public async Task One_yes_lets_every_denied_file_of_the_transfer_through()
    {
        foreach (string name in new[] { "a", "b", "c" })
            Make($@"src\{name}.txt", name);
        Directory.CreateDirectory(P("protected"));
        var elevation = new StandIn();
        var answers = new OperationAnswers { Error = e => e.CanElevate ? ErrorChoice.Elevate : ErrorChoice.Skip };
        var engine = Engine(answers, elevation);

        OperationResult result = await engine.CopyAsync(new[] { P(@"src\a.txt"), P(@"src\b.txt"), P(@"src\c.txt") }, P("protected"));

        Assert.Equal(3, result.Succeeded);
        Assert.True(Assert.Single(answers.Errors).CanElevate); // asked once, offering administrator rights
        Assert.Equal(1, elevation.Started); // one Windows prompt for the transfer
        Assert.Equal(3, elevation.Session!.Copies);
        Assert.True(elevation.Session.Disposed); // the helper ends with the transfer
        Assert.Equal("b", File.ReadAllText(P(@"protected\b.txt")));
    }

    [Fact]
    public async Task Saying_no_to_Windows_leaves_ordinary_errors_and_isnt_offered_again()
    {
        Make(@"src\a.txt");
        Make(@"src\b.txt");
        Directory.CreateDirectory(P("protected"));
        var elevation = new StandIn { Refuse = true };
        var answers = new OperationAnswers { Error = e => e.CanElevate ? ErrorChoice.Elevate : ErrorChoice.Skip };

        OperationResult result = await Engine(answers, elevation).CopyAsync(new[] { P(@"src\a.txt"), P(@"src\b.txt") }, P("protected"));

        Assert.Equal(2, result.Failed);
        Assert.Equal(new[] { true, false, false }, answers.Errors.Select(e => e.CanElevate)); // a: offered, then asked again; b: not offered
        Assert.Equal(1, elevation.Started);
        Assert.False(File.Exists(P(@"protected\a.txt")));
    }

    [Fact]
    public async Task Denied_as_administrator_too_is_an_ordinary_error_not_a_loop()
    {
        Make(@"src\a.txt");
        Directory.CreateDirectory(P("protected"));
        var elevation = new StandIn { DeniedToo = true }; // a file of the system: an administrator may not touch it either
        var answers = new OperationAnswers { Error = e => e.CanElevate ? ErrorChoice.Elevate : ErrorChoice.Skip };

        OperationResult result = await Engine(answers, elevation).CopyAsync(new[] { P(@"src\a.txt") }, P("protected"));

        Assert.Equal(1, result.Failed);
        Assert.Equal(new[] { true, false }, answers.Errors.Select(e => e.CanElevate));
    }

    [Fact]
    public async Task Not_offered_when_already_administrator_or_without_the_helper()
    {
        Make(@"src\a.txt");
        Directory.CreateDirectory(P("protected"));
        var asAdmin = new OperationAnswers();
        var engine = Engine(asAdmin, new StandIn());
        engine.IsElevated = () => true;
        await engine.CopyAsync(new[] { P(@"src\a.txt") }, P("protected"));

        var noHelper = new OperationAnswers();
        await Engine(noHelper, null).CopyAsync(new[] { P(@"src\a.txt") }, P("protected"));

        Assert.False(Assert.Single(asAdmin.Errors).CanElevate);
        Assert.False(Assert.Single(noHelper.Errors).CanElevate);
    }

    [Fact]
    public async Task Folders_moves_and_deletes_go_through_the_helper_too()
    {
        Make(@"src\dir\a.txt", "a");
        Make(@"src\dir\sub\b.txt", "b");
        Make(@"protected\old\c.txt", "c");
        Make(@"protected\moved.txt", "m");
        var elevation = new StandIn();
        var answers = new OperationAnswers { Error = e => e.CanElevate ? ErrorChoice.Elevate : ErrorChoice.Skip };
        var engine = Engine(answers, elevation);

        OperationResult copied = await engine.CopyAsync(new[] { P(@"src\dir") }, P("protected"));
        OperationResult moved = await Engine(answers, elevation).MoveAsync(new[] { P(@"protected\moved.txt") }, P("src"));
        OperationResult deleted = await Engine(answers, elevation).DeleteAsync(new[] { P(@"protected\old") });

        Assert.Equal(2, copied.Succeeded);
        Assert.Equal("b", File.ReadAllText(P(@"protected\dir\sub\b.txt")));
        Assert.Equal(1, moved.Succeeded);
        Assert.Equal("m", File.ReadAllText(P(@"src\moved.txt")));
        Assert.False(File.Exists(P(@"protected\moved.txt"))); // the source of the move, deleted as administrator
        Assert.Equal(2, deleted.Succeeded);
        Assert.False(Directory.Exists(P(@"protected\old")));
        Assert.Equal(3, elevation.Started); // one prompt per transfer
    }

    private FileOperationEngine Engine(OperationAnswers answers, StandIn? elevation) =>
        new(answers)
        {
            Elevation = elevation,
            Local = new Denying(P("protected")),
            IsElevated = () => false,
        };

    /// <summary>This process, denied access to everything in one folder (as Program Files is to a user).</summary>
    private sealed class Denying(string folder) : IFileOps
    {
        private readonly string _folder = folder.TrimEnd('\\') + "\\";

        public bool Denied(string path) =>
            (path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path).StartsWith(_folder, StringComparison.OrdinalIgnoreCase);

        private void Check(params string[] paths)
        {
            if (paths.Any(Denied))
                throw new UnauthorizedAccessException("Access to the path is denied.");
        }

        public void CopyFile(string source, string target, bool overwrite, Func<long, long, bool> progress)
        {
            Check(target);
            LocalFileOps.Instance.CopyFile(source, target, overwrite, progress);
        }

        public void CreateDirectory(string path)
        {
            Check(path);
            LocalFileOps.Instance.CreateDirectory(path);
        }

        public void DeleteFile(string path)
        {
            Check(path);
            LocalFileOps.Instance.DeleteFile(path);
        }

        public void DeleteDirectory(string path)
        {
            Check(path);
            LocalFileOps.Instance.DeleteDirectory(path);
        }

        public void Move(string source, string target, bool isDirectory)
        {
            Check(source, target);
            LocalFileOps.Instance.Move(source, target, isDirectory);
        }

        public void SetAttributes(string path, FileAttributes attributes)
        {
            Check(path);
            LocalFileOps.Instance.SetAttributes(path, attributes);
        }

        public void SetLastWriteTime(string path, DateTime time, bool isDirectory)
        {
            Check(path);
            LocalFileOps.Instance.SetLastWriteTime(path, time, isDirectory);
        }
    }

    /// <summary>Stands in for Windows' prompt and the helper: its session does the changes with this process's rights.</summary>
    private sealed class StandIn : IElevation
    {
        public bool Refuse { get; init; }
        public bool DeniedToo { get; init; }
        public int Started { get; private set; }
        public Session? Session { get; private set; }

        public Task<IElevatedSession> StartAsync(CancellationToken cancellationToken)
        {
            Started++;
            if (Refuse)
                throw new ElevationRefusedException();
            Session = new Session(DeniedToo);
            return Task.FromResult<IElevatedSession>(Session);
        }
    }

    private sealed class Session(bool deniedToo) : IElevatedSession
    {
        public int Copies { get; private set; }
        public bool Disposed { get; private set; }

        private static IFileOps Ops => LocalFileOps.Instance;

        private void Check()
        {
            if (deniedToo)
                throw new UnauthorizedAccessException("Access to the path is denied (as administrator too).");
        }

        public void CopyFile(string source, string target, bool overwrite, Func<long, long, bool> progress)
        {
            Check();
            Copies++;
            Ops.CopyFile(source, target, overwrite, progress);
        }

        public void CreateDirectory(string path)
        {
            Check();
            Ops.CreateDirectory(path);
        }

        public void DeleteFile(string path)
        {
            Check();
            Ops.DeleteFile(path);
        }

        public void DeleteDirectory(string path)
        {
            Check();
            Ops.DeleteDirectory(path);
        }

        public void Move(string source, string target, bool isDirectory)
        {
            Check();
            Ops.Move(source, target, isDirectory);
        }

        public void SetAttributes(string path, FileAttributes attributes)
        {
            Check();
            Ops.SetAttributes(path, attributes);
        }

        public void SetLastWriteTime(string path, DateTime time, bool isDirectory)
        {
            Check();
            Ops.SetLastWriteTime(path, time, isDirectory);
        }

        public void Dispose() => Disposed = true;
    }

    // ---- the helper's protocol, in this process ----------------------------------------------------------

    [Fact]
    public void The_helper_does_each_change_and_reports_copy_progress()
    {
        byte[] data = new byte[3 * 1024 * 1024 + 17];
        new Random(7).NextBytes(data);
        Directory.CreateDirectory(P("src"));
        File.WriteAllBytes(P(@"src\big.bin"), data);
        var progress = new List<long>();

        Task served;
        using (var helper = Connected(out served))
        {
            helper.CreateDirectory(P(@"dst\sub"));
            helper.CopyFile(P(@"src\big.bin"), P(@"dst\sub\big.bin"), overwrite: false, (total, done) =>
            {
                progress.Add(done);
                return true;
            });
            helper.SetAttributes(P(@"dst\sub\big.bin"), FileAttributes.ReadOnly | FileAttributes.Compressed); // Compressed isn't set: ignored
            var time = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Local);
            helper.SetLastWriteTime(P(@"dst\sub\big.bin"), time, isDirectory: false);
            helper.SetAttributes(P(@"dst\sub\big.bin"), FileAttributes.Normal);
            helper.Move(P(@"dst\sub\big.bin"), P(@"dst\moved.bin"), isDirectory: false);
            helper.DeleteDirectory(P(@"dst\sub"));

            Assert.Equal(data, File.ReadAllBytes(P(@"dst\moved.bin")));
            Assert.Equal(time, File.GetLastWriteTime(P(@"dst\moved.bin")));
            Assert.False(Directory.Exists(P(@"dst\sub")));
            Assert.Equal(data.Length, progress[^1]);

            helper.DeleteFile(P(@"dst\moved.bin"));
            Assert.False(File.Exists(P(@"dst\moved.bin")));
            Assert.False(served.IsCompleted);
        }
        Assert.True(Wait(served), "the helper didn't end when the program closed the connection");
    }

    [Fact]
    public void The_helper_reports_failures_as_they_are()
    {
        Directory.CreateDirectory(P("denied"));
        Deny(P("denied"));
        using var helper = Connected(out _);

        var missing = Assert.Throws<IOException>(() => helper.DeleteDirectory(P("not there")));
        Assert.IsNotType<UnauthorizedAccessException>(missing);
        Assert.Throws<UnauthorizedAccessException>(() => helper.CreateDirectory(P(@"denied\new")));
        Assert.Throws<IOException>(() => helper.CreateDirectory("relative")); // refused by the helper
        helper.CreateDirectory(P("still works")); // the helper goes on after a failure
        Assert.True(Directory.Exists(P("still works")));
    }

    [Fact]
    public void A_copy_in_the_helper_can_be_cancelled()
    {
        Directory.CreateDirectory(P("src"));
        File.WriteAllBytes(P(@"src\big.bin"), new byte[32 * 1024 * 1024]);
        using var helper = Connected(out _);

        Assert.Throws<OperationCanceledException>(() =>
            helper.CopyFile(P(@"src\big.bin"), P("copy.bin"), overwrite: false, (_, done) => done < 2 * 1024 * 1024));
        helper.CreateDirectory(P("after")); // and it takes the next request
        Assert.True(Directory.Exists(P("after")));
    }

    private static ElevatedClient Connected(out Task served)
    {
        string name = "VibeSalamander.Tests." + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        Task connecting = server.WaitForConnectionAsync();
        client.Connect(5000);
        connecting.Wait(5000);
        Task helper = Task.Run(() =>
        {
            using (client)
                ElevatedServer.Run(client); // the helper's side
        });
        served = helper;
        var program = new ElevatedClient(server);
        program.WaitForHello();
        return program;
    }

    private static bool Wait(Task task) => task.Wait(TimeSpan.FromSeconds(10));

    // ---- the real helper program ---------------------------------------------------------------------------

    [Fact]
    public async Task The_helper_program_starts_connects_and_ends_with_the_transfer()
    {
        string helper = Path.Combine(AppContext.BaseDirectory, ElevationLauncher.HelperFileName);
        Assert.True(File.Exists(helper), "the helper isn't built beside the tests: " + helper);
        Make(@"src\a.txt", "helper");
        var launcher = new ElevationLauncher(helper, () => IntPtr.Zero) { AsAdministrator = false };

        using (IElevatedSession session = await launcher.StartAsync(CancellationToken.None))
        {
            session.CreateDirectory(P("dst"));
            session.CopyFile(P(@"src\a.txt"), P(@"dst\a.txt"), overwrite: false, (_, _) => true);
        }

        Assert.Equal("helper", File.ReadAllText(P(@"dst\a.txt")));
        Assert.Empty(System.Diagnostics.Process.GetProcessesByName("VibeSalamander.Elevated")); // it ended with the session
    }

    [Fact]
    public void The_helper_refuses_a_pipe_of_another_process_and_strange_arguments()
    {
        string name = "VibeSalamander.Elevated." + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task<int> helper = Task.Run(() => ElevationLauncher.RunHelper(new[] { name, "4" })); // "started by" process 4, not by us
        pipe.WaitForConnection();

        Assert.True(helper.Wait(10000));
        Assert.Equal(4, helper.Result); // not its program's pipe: it does nothing
        Assert.Equal(2, ElevationLauncher.RunHelper(new[] { "something", "1" }));
        Assert.Equal(2, ElevationLauncher.RunHelper(new[] { name.ToUpperInvariant(), "1" }));
        Assert.Equal(2, ElevationLauncher.RunHelper(Array.Empty<string>()));
    }

    // ---- a folder this process may not write to ------------------------------------------------------------

    private static FileSystemAccessRule DenyRule() => new(
        WindowsIdentity.GetCurrent().User!,
        FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories | FileSystemRights.WriteData,
        InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);

    private static void Deny(string folder)
    {
        var info = new DirectoryInfo(folder);
        DirectorySecurity security = info.GetAccessControl();
        security.AddAccessRule(DenyRule());
        info.SetAccessControl(security);
    }

    private static void AllowAgain(string root)
    {
        foreach (string folder in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Prepend(root))
        {
            var info = new DirectoryInfo(folder);
            DirectorySecurity security = info.GetAccessControl();
            if (security.RemoveAccessRule(DenyRule()))
                info.SetAccessControl(security);
        }
    }
}
