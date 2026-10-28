// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;

namespace VibeSalamander.Core.Settings;

/// <summary>
/// The command line, as Salamander's: <c>-L folder</c> and <c>-R folder</c> for the panels, <c>-A folder</c> for the
/// active one, <c>-P 1</c> / <c>-P 2</c> to make the left or right panel active. A folder given alone goes to the
/// active panel; a file given alone opens its folder with the caret on it. A second start hands these to the
/// running window (with "Allow only one instance").
/// </summary>
public sealed record StartupArguments(string? Left, string? Right, string? Active, int Panel)
{
    public static readonly StartupArguments None = new(null, null, null, 0);

    public bool IsEmpty => Left == null && Right == null && Active == null && Panel == 0;

    public static StartupArguments Parse(IReadOnlyList<string> args)
    {
        string? left = null, right = null, active = null;
        int panel = 0;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i].Trim();
            if (arg.Length == 0 || arg.StartsWith("--", StringComparison.Ordinal))
                continue; // --restart and the like are the app's own
            string? Next() => i + 1 < args.Count ? Clean(args[++i]) : null;
            switch (arg.ToUpperInvariant())
            {
                case "-L" or "/L":
                    left = Next();
                    break;
                case "-R" or "/R":
                    right = Next();
                    break;
                case "-A" or "/A":
                    active = Next();
                    break;
                case "-P" or "/P":
                    panel = Next() switch { "1" or "L" or "l" => 1, "2" or "R" or "r" => 2, _ => 0 };
                    break;
                default:
                    if (!arg.StartsWith('-'))
                        active ??= Clean(arg);
                    break;
            }
        }
        return new StartupArguments(left, right, active, panel);
    }

    /// <summary>Splits a command line as Windows does (quotes group, a backslash before a quote escapes it).</summary>
    public static IReadOnlyList<string> Split(string commandLine)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];
            if (c == '\\' && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
            {
                current.Append('"');
                i++;
                any = true;
            }
            else if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any)
                    parts.Add(current.ToString());
                current.Clear();
                any = false;
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }
        if (any)
            parts.Add(current.ToString());
        return parts;
    }

    private static string Clean(string path)
    {
        path = path.Trim().Trim('"');
        // "C:" alone means the drive's root here, not "the current folder on C:"
        return path.Length == 2 && path[1] == ':' ? path + "\\" : path;
    }
}
