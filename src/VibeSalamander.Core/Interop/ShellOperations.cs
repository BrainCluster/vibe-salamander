using System.Diagnostics;

namespace VibeSalamander.Core.Interop;

/// <summary>
/// Phase-1 file operations through the Windows shell (system progress dialog, conflict prompts,
/// Recycle Bin, undo). Phase 2 replaces copy/move with an own queued engine.
/// </summary>
public static class ShellOperations
{
    public enum Result { Success, Cancelled, Failed }

    public static Task<Result> CopyAsync(IntPtr owner, IReadOnlyList<string> sources, string targetDirectory) =>
        RunAsync(owner, NativeMethods.FO_COPY, sources, targetDirectory,
                 (ushort)(NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_NOCONFIRMMKDIR));

    public static Task<Result> MoveAsync(IntPtr owner, IReadOnlyList<string> sources, string targetDirectory) =>
        RunAsync(owner, NativeMethods.FO_MOVE, sources, targetDirectory,
                 (ushort)(NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_NOCONFIRMMKDIR));

    /// <summary>Deletes to the Recycle Bin, or permanently when <paramref name="permanent"/> is set.</summary>
    public static Task<Result> DeleteAsync(IntPtr owner, IReadOnlyList<string> paths, bool permanent)
    {
        // We ask for confirmation ourselves (Salamander style), so the shell must not ask again.
        ushort flags = (ushort)(NativeMethods.FOF_NOCONFIRMATION | NativeMethods.FOF_WANTNUKEWARNING);
        if (!permanent)
            flags |= NativeMethods.FOF_ALLOWUNDO;
        return RunAsync(owner, NativeMethods.FO_DELETE, paths, null, flags);
    }

    public static Task<Result> RenameAsync(IntPtr owner, string path, string newFullPath) =>
        RunAsync(owner, NativeMethods.FO_RENAME, new[] { path }, newFullPath, NativeMethods.FOF_ALLOWUNDO);

    private static Task<Result> RunAsync(IntPtr owner, uint func, IReadOnlyList<string> from, string? to, ushort flags)
    {
        var tcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        // SHFileOperation shows modal UI and pumps messages; run it on its own STA thread so the
        // main window keeps painting while the system progress dialog is up.
        var thread = new Thread(() =>
        {
            try
            {
                var op = new NativeMethods.SHFILEOPSTRUCT
                {
                    hwnd = owner,
                    wFunc = func,
                    pFrom = string.Join('\0', from) + '\0', // the marshaller adds the final terminator
                    pTo = to == null ? null : to + '\0',
                    fFlags = flags,
                };
                int rc = NativeMethods.SHFileOperation(ref op);
                tcs.SetResult(op.fAnyOperationsAborted ? Result.Cancelled : rc == 0 ? Result.Success : Result.Failed);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    public static void ShowProperties(IntPtr owner, string path) =>
        NativeMethods.SHObjectProperties(owner, NativeMethods.SHOP_FILEPATH, path, null);

    /// <summary>Opens a file or folder with its associated program.</summary>
    public static void Open(string path, string? workingDirectory = null)
    {
        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(path) ?? string.Empty,
        });
    }

    /// <summary>Opens the file in the "edit" verb handler, falling back to Notepad.</summary>
    public static void Edit(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "edit" });
        }
        catch
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
    }

    /// <summary>Command shell in a directory (Windows Terminal when available).</summary>
    public static void OpenCommandShell(string directory)
    {
        try
        {
            Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{directory}\"") { UseShellExecute = true });
        }
        catch
        {
            Process.Start(new ProcessStartInfo("cmd.exe") { UseShellExecute = true, WorkingDirectory = directory });
        }
    }

    /// <summary>Runs a command-line string in a directory (Salamander command line).</summary>
    public static void RunCommandLine(string directory, string commandLine)
    {
        Process.Start(new ProcessStartInfo("cmd.exe", "/k " + commandLine)
        {
            UseShellExecute = true,
            WorkingDirectory = directory,
        });
    }

    public static void OpenInExplorer(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
}
