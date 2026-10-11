// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;

namespace VibeSalamander.Core.Tools;

/// <summary>One entry of the user menu (F9). A name of "-" is a separator.</summary>
public sealed class UserMenuItem
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Program or document to start (e.g. "cmd.exe", "notepad.exe").</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>Arguments; may contain variables such as $(FullName).</summary>
    public string Arguments { get; set; } = string.Empty;

    /// <summary>Working directory; defaults to $(Path).</summary>
    public string InitialDirectory { get; set; } = "$(Path)";

    public bool IsSeparator => Name == "-";

    public UserMenuItem Clone() => new() { Name = Name, Command = Command, Arguments = Arguments, InitialDirectory = InitialDirectory };
}

/// <summary>Values the user menu variables expand to.</summary>
public sealed class UserMenuContext
{
    /// <summary>Directory of the active panel.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Directory of the other panel.</summary>
    public string OtherPath { get; init; } = string.Empty;

    /// <summary>Full path of the focused item ("" when none).</summary>
    public string FullName { get; init; } = string.Empty;

    /// <summary>Full paths of the selected (marked) items, or the focused item.</summary>
    public IReadOnlyList<string> Selected { get; init; } = Array.Empty<string>();
}

/// <summary>User menu defaults and variable expansion (Salamander style $(Name) variables).</summary>
public static class UserMenu
{
    /// <summary>Variables offered in the editor.</summary>
    public static readonly (string Variable, string Meaning)[] Variables =
    {
        ("$(FullName)", "full path of the focused item"),
        ("$(Name)", "name of the focused item"),
        ("$(NamePart)", "name without extension"),
        ("$(ExtPart)", "extension (without the dot)"),
        ("$(Path)", "directory of the active panel"),
        ("$(OtherPath)", "directory of the other panel"),
        ("$(SelectedFullNames)", "quoted full paths of the selected items"),
        ("$(SelectedNames)", "quoted names of the selected items"),
    };

    public static List<UserMenuItem> CreateDefaults() => new()
    {
        new() { Name = "Command Prompt here", Command = "cmd.exe", Arguments = "", InitialDirectory = "$(Path)" },
        new() { Name = "PowerShell here", Command = "powershell.exe", Arguments = "-NoExit", InitialDirectory = "$(Path)" },
        new() { Name = "-" },
        new() { Name = "Edit in Notepad", Command = "notepad.exe", Arguments = "\"$(FullName)\"", InitialDirectory = "$(Path)" },
        new()
        {
            Name = "SHA-256 checksum",
            Command = "powershell.exe",
            Arguments = "-NoExit -Command \"Get-FileHash -Algorithm SHA256 -LiteralPath '$(FullName)' | Format-List\"",
            InitialDirectory = "$(Path)",
        },
        new() { Name = "Compare with the same name in the other panel", Command = "cmd.exe", Arguments = "/k fc /b \"$(FullName)\" \"$(OtherPath)\\$(Name)\"", InitialDirectory = "$(Path)" },
    };

    /// <summary>Replaces the variables in <paramref name="text"/> (case-insensitive).</summary>
    public static string Expand(string text, UserMenuContext context)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("$(", StringComparison.Ordinal))
            return text;
        string name = System.IO.Path.GetFileName(context.FullName.TrimEnd('\\'));
        int dot = name.LastIndexOf('.');
        string namePart = dot > 0 ? name[..dot] : name;
        string extPart = dot > 0 ? name[(dot + 1)..] : string.Empty;
        string Quote(IEnumerable<string> items) => string.Join(" ", items.Select(i => "\"" + i + "\""));

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["FullName"] = context.FullName,
            ["Name"] = name,
            ["NamePart"] = namePart,
            ["ExtPart"] = extPart,
            ["Path"] = context.Path,
            ["OtherPath"] = context.OtherPath,
            ["SelectedFullNames"] = Quote(context.Selected),
            ["SelectedNames"] = Quote(context.Selected.Select(p => System.IO.Path.GetFileName(p.TrimEnd('\\')))),
        };

        var sb = new StringBuilder(text.Length + 64);
        int i = 0;
        while (i < text.Length)
        {
            int start = text.IndexOf("$(", i, StringComparison.Ordinal);
            if (start < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }
            int end = text.IndexOf(')', start + 2);
            if (end < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }
            sb.Append(text, i, start - i);
            string key = text[(start + 2)..end];
            sb.Append(values.TryGetValue(key, out string? value) ? value : text[start..(end + 1)]);
            i = end + 1;
        }
        return sb.ToString();
    }
}
