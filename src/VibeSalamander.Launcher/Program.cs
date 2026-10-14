// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VibeSalamander.Launcher;

/// <summary>Starts lib\VibeSalamander.exe with the same command line, then exits.</summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string folder = AppDomain.CurrentDomain.BaseDirectory;
        string app = Path.Combine(folder, "lib", "VibeSalamander.exe");
        if (!File.Exists(app))
        {
            MessageBoxW(IntPtr.Zero, "lib\\VibeSalamander.exe is missing. Unzip the whole Vibe Salamander folder again.",
                "Vibe Salamander", 0x10 /* MB_ICONERROR */);
            return 1;
        }

        try
        {
            AllowSetForegroundWindow(-1); // ASFW_ANY: the program's window may come to the front
            Process.Start(new ProcessStartInfo(app, JoinArguments(args))
            {
                UseShellExecute = false,
                WorkingDirectory = Environment.CurrentDirectory,
            });
            return 0;
        }
        catch (Exception ex)
        {
            MessageBoxW(IntPtr.Zero, "Vibe Salamander could not be started: " + ex.Message, "Vibe Salamander", 0x10);
            return 1;
        }
    }

    /// <summary>The arguments as one command line, quoted the way Windows programs read them back.</summary>
    private static string JoinArguments(string[] args)
    {
        var line = new StringBuilder();
        foreach (string arg in args)
        {
            if (line.Length > 0)
                line.Append(' ');
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            {
                line.Append(arg);
                continue;
            }
            line.Append('"');
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                {
                    line.Append('\\', backslashes * 2 + 1);
                    line.Append('"');
                }
                else
                {
                    line.Append('\\', backslashes);
                    line.Append(c);
                }
                backslashes = 0;
            }
            line.Append('\\', backslashes * 2); // before the closing quote
            line.Append('"');
        }
        return line.ToString();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
