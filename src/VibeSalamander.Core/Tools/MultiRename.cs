// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VibeSalamander.Core.Localization;

namespace VibeSalamander.Core.Tools;

public enum RenameCase
{
    Unchanged,
    Lower,
    Upper,
    /// <summary>"Holiday photos" (the first letter).</summary>
    FirstLetter,
    /// <summary>"Holiday Photos" (each word).</summary>
    EachWord,
}

/// <summary>How the multi-rename tool builds the new names (Total Commander's Ctrl+M).</summary>
public sealed class MultiRenameOptions
{
    /// <summary>
    /// The name: [N] the old name, [N2-5] its letters 2 to 5, [N3] its third letter, [E] the extension, [C] the
    /// counter, [P] the folder's name, [YMD] the date and [hms] the time it was changed; any other text as it is.
    /// </summary>
    public string NameMask { get; set; } = "[N]";

    /// <summary>The extension, with the same fields ([E] the old one).</summary>
    public string ExtensionMask { get; set; } = "[E]";

    public string Search { get; set; } = string.Empty;
    public string Replace { get; set; } = string.Empty;
    public bool RegularExpression { get; set; }
    public bool MatchCase { get; set; }

    /// <summary>Search and replace in the extension too (else only in the name).</summary>
    public bool ReplaceInExtension { get; set; }

    public RenameCase Case { get; set; }
    public int CounterStart { get; set; } = 1;
    public int CounterStep { get; set; } = 1;
    public int CounterDigits { get; set; } = 1;
}

/// <summary>An item to rename: its path and when it was changed.</summary>
public readonly record struct RenameSource(string Path, bool IsDirectory, DateTime LastWrite);

/// <summary>One line of the plan: the old and the new name, and why it can't be done (or null).</summary>
public sealed record RenameLine(string Path, string OldName, string NewName, string? Problem)
{
    public bool Changes => Problem == null && !string.Equals(OldName, NewName, StringComparison.Ordinal);
}

/// <summary>Many names at once: masks, search and replace, case, a counter. The plan is shown before anything happens.</summary>
public static partial class MultiRename
{
    [GeneratedRegex(@"\[(N|E|C|P|YMD|hms|Y|M|D|h|m|s)(\d+)?(?:-(\d*))?\]")]
    private static partial Regex Field();

    /// <summary>The new names, in the order given, with the problems (bad characters, two items with one name, a name taken).</summary>
    public static IReadOnlyList<RenameLine> Plan(IReadOnlyList<RenameSource> items, MultiRenameOptions options, Func<string, bool>? exists = null)
    {
        exists ??= path => File.Exists(path) || Directory.Exists(path);
        Regex? search = null;
        string? searchProblem = null;
        if (options.Search.Length > 0)
        {
            try
            {
                search = new Regex(options.RegularExpression ? options.Search : Regex.Escape(options.Search),
                    (options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException ex)
            {
                searchProblem = ex.Message;
            }
        }

        var lines = new List<RenameLine>(items.Count);
        long counter = options.CounterStart;
        foreach (RenameSource item in items)
        {
            string oldName = System.IO.Path.GetFileName(item.Path.TrimEnd('\\'));
            string name = item.IsDirectory ? oldName : System.IO.Path.GetFileNameWithoutExtension(oldName);
            string extension = item.IsDirectory ? string.Empty : System.IO.Path.GetExtension(oldName).TrimStart('.');
            string folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(item.Path.TrimEnd('\\')) ?? string.Empty);
            string counterText = counter.ToString(CultureInfo.InvariantCulture).PadLeft(Math.Clamp(options.CounterDigits, 1, 10), '0');

            string Expand(string mask) => Field().Replace(mask, m =>
            {
                string field = m.Groups[1].Value;
                string source = field switch
                {
                    "N" => name,
                    "E" => extension,
                    "C" => counterText,
                    "P" => folder,
                    "YMD" => item.LastWrite.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    "hms" => item.LastWrite.ToString("HH.mm.ss", CultureInfo.InvariantCulture),
                    "Y" => item.LastWrite.ToString("yyyy", CultureInfo.InvariantCulture),
                    "M" => item.LastWrite.ToString("MM", CultureInfo.InvariantCulture),
                    "D" => item.LastWrite.ToString("dd", CultureInfo.InvariantCulture),
                    "h" => item.LastWrite.ToString("HH", CultureInfo.InvariantCulture),
                    "m" => item.LastWrite.ToString("mm", CultureInfo.InvariantCulture),
                    _ => item.LastWrite.ToString("ss", CultureInfo.InvariantCulture),
                };
                if (field is not ("N" or "E" or "P") || !m.Groups[2].Success)
                    return source;
                // [N3] the third letter, [N2-5] letters 2 to 5, [N2-] from the second on
                int from = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                int to = !m.Groups[3].Success ? from : m.Groups[3].Value.Length == 0 ? source.Length : int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                from = Math.Max(1, from);
                to = Math.Min(source.Length, to);
                return from > to ? string.Empty : source.Substring(from - 1, to - from + 1);
            });

            string newName = Expand(options.NameMask);
            string newExtension = item.IsDirectory ? string.Empty : Expand(options.ExtensionMask);
            string? problem = searchProblem;
            if (search != null)
            {
                try
                {
                    if (options.ReplaceInExtension && newExtension.Length > 0)
                    {
                        string whole = search.Replace(newName + "." + newExtension, options.Replace);
                        int dot = whole.LastIndexOf('.');
                        (newName, newExtension) = dot < 0 ? (whole, string.Empty) : (whole[..dot], whole[(dot + 1)..]);
                    }
                    else
                    {
                        newName = search.Replace(newName, options.Replace);
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    problem = L.T("The search takes too long.");
                }
            }
            newName = ApplyCase(newName, options.Case);
            newExtension = ApplyCase(newExtension, options.Case is RenameCase.FirstLetter or RenameCase.EachWord ? RenameCase.Lower : options.Case);
            string full = (newExtension.Length > 0 ? newName + "." + newExtension : newName).TrimEnd(' ', '.');
            if (problem == null)
            {
                if (full.Length == 0)
                    problem = L.T("The name is empty.");
                else if (full.IndexOfAny(System.IO.Path.GetInvalidFileNameChars().Concat(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }).ToArray()) >= 0)
                    problem = L.T("The name has a character Windows doesn't allow (\\ / : * ? \" < > |).");
            }
            lines.Add(new RenameLine(item.Path, oldName, full, problem));
            counter += options.CounterStep;
        }

        // two items given the same name, or a name another file has already
        var renamed = new HashSet<string>(lines.Select(l => l.Path), StringComparer.OrdinalIgnoreCase);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < lines.Count; i++)
        {
            RenameLine line = lines[i];
            if (line.Problem != null)
                continue;
            string target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(line.Path.TrimEnd('\\')) ?? string.Empty, line.NewName);
            if (seen.TryGetValue(target, out int first))
            {
                lines[i] = line with { Problem = L.F("Two items would be named \"{0}\".", line.NewName) };
                if (lines[first].Problem == null)
                    lines[first] = lines[first] with { Problem = L.F("Two items would be named \"{0}\".", line.NewName) };
                continue;
            }
            seen[target] = i;
            // taken by an item that isn't being renamed (one of the renamed ones moves out of the way)
            if (line.Changes && !string.Equals(target, line.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && !renamed.Contains(target) && exists(target))
                lines[i] = line with { Problem = L.F("\"{0}\" exists already.", line.NewName) };
        }
        return lines;
    }

    private static string ApplyCase(string text, RenameCase mode)
    {
        switch (mode)
        {
            case RenameCase.Lower:
                return text.ToLower(CultureInfo.CurrentCulture);
            case RenameCase.Upper:
                return text.ToUpper(CultureInfo.CurrentCulture);
            case RenameCase.FirstLetter:
                return text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..].ToLower(CultureInfo.CurrentCulture);
            case RenameCase.EachWord:
                var builder = new StringBuilder(text.Length);
                bool start = true;
                foreach (char c in text)
                {
                    builder.Append(start ? char.ToUpper(c, CultureInfo.CurrentCulture) : char.ToLower(c, CultureInfo.CurrentCulture));
                    start = !char.IsLetterOrDigit(c) && c != '\'';
                }
                return builder.ToString();
            default:
                return text;
        }
    }

    /// <summary>
    /// Renames the lines that change, in two steps (each to a temporary name first), so names can be swapped and
    /// shifted ("2.jpg" → "3.jpg" while "3.jpg" → "4.jpg"). What fails is put back. Returns the errors.
    /// </summary>
    public static IReadOnlyList<string> Apply(IReadOnlyList<RenameLine> lines)
    {
        var errors = new List<string>();
        var moved = new List<(string Temp, string Original, string Target, bool IsDirectory)>();
        foreach (RenameLine line in lines.Where(l => l.Changes))
        {
            string original = line.Path.TrimEnd('\\');
            string directory = System.IO.Path.GetDirectoryName(original) ?? string.Empty;
            string target = System.IO.Path.Combine(directory, line.NewName);
            bool isDirectory = Directory.Exists(original);
            string temp = System.IO.Path.Combine(directory, "~vibe-rename-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Move(original, temp, isDirectory);
                moved.Add((temp, original, target, isDirectory));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{line.OldName}: {ex.Message}");
            }
        }
        foreach (var (temp, original, target, isDirectory) in moved)
        {
            try
            {
                Move(temp, target, isDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{System.IO.Path.GetFileName(original)}: {ex.Message}");
                try
                {
                    Move(temp, original, isDirectory); // back to its old name
                }
                catch (Exception back) when (back is IOException or UnauthorizedAccessException)
                {
                    errors.Add(L.F("\"{0}\" is left as \"{1}\".", System.IO.Path.GetFileName(original), System.IO.Path.GetFileName(temp)));
                }
            }
        }
        return errors;
    }

    private static void Move(string from, string to, bool isDirectory)
    {
        if (isDirectory)
            Directory.Move(from, to);
        else
            File.Move(from, to);
    }
}
