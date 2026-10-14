// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;

namespace VibeSalamander.Core.Interop;

/// <summary>How a script file can be run from the context menu.</summary>
public enum ScriptHost
{
    None,

    /// <summary>.ps1: Windows PowerShell.</summary>
    PowerShell,

    /// <summary>.bat, .cmd: the command prompt.</summary>
    Cmd,

    /// <summary>.vbs, .vbe, .js, .jse, .wsf: Windows Script Host (cscript) in the command prompt.</summary>
    ScriptHost,
}

/// <summary>
/// "Run with PowerShell" and "Run in cmd" (Vibe Salamander context menu): the script runs in a new
/// console window, in its own directory, and the window stays open so its output can be read.
/// </summary>
public static class ScriptRunner
{
    public static ScriptHost HostOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".ps1" => ScriptHost.PowerShell,
        ".bat" or ".cmd" => ScriptHost.Cmd,
        ".vbs" or ".vbe" or ".js" or ".jse" or ".wsf" => ScriptHost.ScriptHost,
        _ => ScriptHost.None,
    };

    /// <summary>Starts the script; throws Win32Exception / InvalidOperationException when it can't be started.</summary>
    public static void Run(string path)
    {
        string directory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
        ProcessStartInfo info = HostOf(path) switch
        {
            // -ExecutionPolicy Bypass applies to this run only (like Explorer's "Run with PowerShell")
            ScriptHost.PowerShell => new ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoExit", "-NoLogo", "-ExecutionPolicy", "Bypass", "-File", path },
            },
            // cmd /k ""C:\some dir\script.bat"": the outer quotes are removed by cmd, the inner ones keep the path whole
            ScriptHost.Cmd => new ProcessStartInfo("cmd.exe", $"/k \"\"{path}\"\""),
            ScriptHost.ScriptHost => new ProcessStartInfo("cmd.exe", $"/k cscript.exe //nologo \"{path}\""),
            _ => throw new InvalidOperationException("Not a script that Vibe Salamander can run."),
        };
        info.UseShellExecute = false;
        info.WorkingDirectory = directory;
        Process.Start(info)?.Dispose();
    }
}
