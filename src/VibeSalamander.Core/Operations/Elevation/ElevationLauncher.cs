// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using VibeSalamander.Core.Localization;
using Microsoft.Win32.SafeHandles;

namespace VibeSalamander.Core.Operations.Elevation;

/// <summary>
/// Starts VibeSalamander.Elevated.exe as administrator (Windows asks the user) and connects to it.
/// <para>
/// Who can talk to whom: this process makes a pipe with a random name that admits one connection, starts the helper
/// with that name and its own process id, and accepts the connection only from the process it started. The helper
/// accepts the pipe only when it belongs to the process id it was given. Nothing else can give the helper work.
/// </para>
/// </summary>
public sealed class ElevationLauncher : IElevation
{
    public const string HelperFileName = "VibeSalamander.Elevated.exe";
    private const string PipePrefix = "VibeSalamander.Elevated.";

    private readonly string _helper;
    private readonly Func<IntPtr> _owner;

    /// <param name="helper">The helper's full path.</param>
    /// <param name="owner">The window Windows' prompt belongs to.</param>
    public ElevationLauncher(string helper, Func<IntPtr> owner)
    {
        _helper = helper;
        _owner = owner;
    }

    /// <summary>The helper beside the program, or null when it isn't there (a build without it).</summary>
    public static string? FindHelper()
    {
        string path = Path.Combine(AppContext.BaseDirectory, HelperFileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Start without Windows' prompt (the tests: the helper runs with this process's rights).</summary>
    internal bool AsAdministrator { get; init; } = true;

    public async Task<IElevatedSession> StartAsync(CancellationToken cancellationToken)
    {
        string name = PipePrefix + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        HelperProcess? process = null;
        Session? session = null;
        try
        {
            string arguments = $"{name} {Environment.ProcessId}";
            process = AsAdministrator
                ? await RunAsAdministratorAsync(_helper, arguments, _owner())
                : HelperProcess.Start(_helper, arguments);
            if (process == null)
                throw new IOException(L.T("The administrator helper didn't start."));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            Task connected = pipe.WaitForConnectionAsync(timeout.Token);
            Task exited = process.WaitForExitAsync(timeout.Token);
            await Task.WhenAny(connected, exited);
            Observe(connected);
            Observe(exited);
            cancellationToken.ThrowIfCancellationRequested();
            if (!connected.IsCompletedSuccessfully)
                throw new IOException(L.T("The administrator helper didn't start."));
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint client) || client != process.Id)
                throw new IOException(L.T("Another program connected instead of the administrator helper."));

            session = new Session(pipe, process);
            await Task.Run(session.WaitForHello, cancellationToken).WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            Session started = session;
            session = null;
            process = null;
            return started;
        }
        catch (TimeoutException ex)
        {
            throw new IOException(L.T("The administrator helper didn't start."), ex);
        }
        finally
        {
            if (session != null)
            {
                session.Dispose(); // the pipe and the helper with it
            }
            else if (process != null || !pipe.IsConnected)
            {
                pipe.Dispose();
                process?.Kill();
                process?.Dispose();
            }
        }
    }

    /// <summary>A task that may fail after nobody waits for it any more: its failure is seen (not a background error).</summary>
    private static void Observe(Task task) =>
        task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    /// <summary>The helper's connection; disposing it ends the helper.</summary>
    private sealed class Session(NamedPipeServerStream pipe, HelperProcess process) : ElevatedClient(pipe)
    {
        public override void Dispose()
        {
            base.Dispose();
            if (!process.WaitForExit(3000))
                process.Kill();
            process.Dispose();
        }
    }

    /// <summary>
    /// The started helper, through the handle its start gave: a program that isn't administrator may not open an
    /// administrator's process afterwards (looking it up by id, waiting for it and ending it fail with "access
    /// denied"), but the handle from the start has every right.
    /// </summary>
    private sealed class HelperProcess : IDisposable
    {
        private readonly SafeProcessHandle _handle;
        private readonly IDisposable? _owner;

        private HelperProcess(SafeProcessHandle handle, IDisposable? owner)
        {
            _handle = handle;
            _owner = owner;
            Id = GetProcessId(handle);
        }

        public int Id { get; }

        /// <summary>Takes over a handle from ShellExecuteEx.</summary>
        public static HelperProcess FromHandle(IntPtr handle) => new(new SafeProcessHandle(handle, ownsHandle: true), null);

        /// <summary>Starts it with this process's rights (the tests).</summary>
        public static HelperProcess? Start(string file, string arguments)
        {
            Process? process = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false, CreateNoWindow = true });
            return process == null ? null : new HelperProcess(process.SafeHandle, process);
        }

        public bool WaitForExit(int milliseconds) => WaitForSingleObject(_handle, (uint)milliseconds) == 0;

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var wait = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(_handle.DangerousGetHandle(), ownsHandle: false) };
            RegisteredWaitHandle registered = ThreadPool.RegisterWaitForSingleObject(wait, (_, _) => exited.TrySetResult(), null, -1, executeOnlyOnce: true);
            CancellationTokenRegistration cancel = cancellationToken.Register(() => exited.TrySetCanceled(cancellationToken));
            exited.Task.ContinueWith(_ =>
            {
                registered.Unregister(null);
                cancel.Dispose();
                wait.Dispose();
            }, TaskScheduler.Default);
            return exited.Task;
        }

        public void Kill()
        {
            if (!WaitForExit(0))
                TerminateProcess(_handle, 1);
        }

        public void Dispose()
        {
            if (_owner != null)
                _owner.Dispose(); // the Process owns the handle
            else
                _handle.Dispose();
        }
    }

    // ---- the helper's side ---------------------------------------------------------------------------

    /// <summary>
    /// The helper program: connects to the pipe it was given (only when it belongs to the process id it was given),
    /// does the changes it is asked for and ends with the connection. Returns the exit code.
    /// </summary>
    public static int RunHelper(string[] args)
    {
        if (args.Length != 2 || !args[0].StartsWith(PipePrefix, StringComparison.Ordinal) || args[0].Length != PipePrefix.Length + 32 ||
            !args[0][PipePrefix.Length..].All(char.IsAsciiHexDigitLower) || !uint.TryParse(args[1], out uint parent))
            return 2; // not started by Vibe Salamander
        using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            pipe.Connect(10000);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            return 3;
        }
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint server) || server != parent)
            return 4; // someone else's pipe
        ElevatedServer.Run(pipe);
        return 0;
    }

    // ---- Windows ---------------------------------------------------------------------------------------

    /// <summary>ShellExecuteEx with "runas" on an STA thread of its own, so Windows' prompt belongs to the window.</summary>
    private static Task<HelperProcess?> RunAsAdministratorAsync(string file, string arguments, IntPtr owner)
    {
        var done = new TaskCompletionSource<HelperProcess?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var info = new SHELLEXECUTEINFO
                {
                    cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                    fMask = SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI,
                    hwnd = owner,
                    lpVerb = "runas",
                    lpFile = file,
                    lpParameters = arguments,
                    lpDirectory = Path.GetDirectoryName(file),
                    nShow = SW_HIDE,
                };
                if (!ShellExecuteEx(ref info))
                {
                    int error = Marshal.GetLastWin32Error();
                    done.SetException(error == ERROR_CANCELLED ? new ElevationRefusedException() : new Win32Exception(error));
                    return;
                }
                done.SetResult(info.hProcess == IntPtr.Zero ? null : HelperProcess.FromHandle(info.hProcess));
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        })
        { IsBackground = true, Name = "Administrator prompt" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private const uint SEE_MASK_NOCLOSEPROCESS = 0x00000040;
    private const uint SEE_MASK_NOASYNC = 0x00000100;
    private const uint SEE_MASK_FLAG_NO_UI = 0x00000400;
    private const int SW_HIDE = 0;
    private const int ERROR_CANCELLED = 1223;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIconOrMonitor;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ShellExecuteExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int GetProcessId(SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
