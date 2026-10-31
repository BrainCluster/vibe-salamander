// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

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

    /// <summary>Security (Ctrl+P): the Properties window opened on its Security page.</summary>
    public static void ShowSecurity(IntPtr owner, string path) =>
        NativeMethods.SHObjectProperties(owner, NativeMethods.SHOP_FILEPATH, path, "Security");

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
    public static void RunCommandLine(string directory, string commandLine, bool closeWhenDone = false)
    {
        Process.Start(new ProcessStartInfo("cmd.exe", (closeWhenDone ? "/c " : "/k ") + commandLine)
        {
            UseShellExecute = true,
            WorkingDirectory = directory,
        });
    }

    /// <summary>Programs, installers, scripts, consoles and shortcuts: what "Run as administrator" makes sense for.</summary>
    public static bool CanRunAsAdministrator(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".exe" or ".com" or ".bat" or ".cmd" or ".ps1" or ".msi" or ".msc" or ".lnk" or ".vbs" or ".cpl";

    /// <summary>
    /// Starts the file with administrator rights (Windows asks first, as for Explorer's "Run as administrator").
    /// False when the question was answered No. Scripts run in a window that stays open, in their own folder.
    /// </summary>
    public static bool RunAsAdministrator(string path)
    {
        string directory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
        ProcessStartInfo info = Path.GetExtension(path).ToLowerInvariant() switch
        {
            // an elevated console starts in System32: the script's folder first
            ".bat" or ".cmd" => new ProcessStartInfo("cmd.exe", $"/k \"cd /d \"{directory}\" && \"{path}\"\""),
            ".ps1" => new ProcessStartInfo("powershell.exe", $"-NoExit -NoLogo -ExecutionPolicy Bypass -File \"{path}\""),
            ".msi" => new ProcessStartInfo("msiexec.exe", $"/i \"{path}\""),
            ".vbs" => new ProcessStartInfo("wscript.exe", $"\"{path}\""),
            _ => new ProcessStartInfo(path),
        };
        info.UseShellExecute = true;
        info.Verb = "runas";
        info.WorkingDirectory = directory;
        try
        {
            Process.Start(info)?.Dispose();
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false; // ERROR_CANCELLED: No at Windows' question
        }
    }

    /// <summary>Windows "Open with" chooser (Alt+F3 / Ctrl+Shift+F3; Vibe Salamander has no internal viewer).</summary>
    public static void OpenWith(IntPtr owner, string path)
    {
        var info = new NativeMethods.OPENASINFO
        {
            pcszFile = path,
            pcszClass = null,
            oaifInFlags = NativeMethods.OAIF_ALLOW_REGISTRATION | NativeMethods.OAIF_EXEC,
        };
        NativeMethods.SHOpenWithDialog(owner, ref info);
    }

    public static void OpenInExplorer(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
}
