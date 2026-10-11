// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Core.FileSystem;

/// <summary>
/// Salamander file masks: patterns separated by ';' (or ','), '*' and '?' wildcards, and an optional
/// exclusion part after '|'. Examples: "*.txt;*.doc", "*.*|*.bak;*.tmp", "report??.xls".
/// "*.*" and "*" match every name, including names without an extension (as in Salamander/Windows).
/// Matching is case-insensitive.
/// </summary>
public sealed class WildcardMask
{
    private readonly string[] _include;
    private readonly string[] _exclude;

    public WildcardMask(string mask)
    {
        Text = mask?.Trim() ?? string.Empty;
        string includePart = Text, excludePart = string.Empty;
        int bar = Text.IndexOf('|');
        if (bar >= 0)
        {
            includePart = Text[..bar];
            excludePart = Text[(bar + 1)..];
        }
        _include = Split(includePart);
        _exclude = Split(excludePart);
        if (_include.Length == 0)
            _include = new[] { "*" }; // "|*.bak" = everything except backups
    }

    public string Text { get; }

    /// <summary>True when the mask accepts every name (no filtering).</summary>
    public bool MatchesEverything => _exclude.Length == 0 && _include.Any(p => p is "*" or "*.*");

    private static string[] Split(string part) =>
        part.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool IsMatch(string name)
    {
        bool included = false;
        foreach (string p in _include)
        {
            if (MatchPattern(p, name))
            {
                included = true;
                break;
            }
        }
        if (!included)
            return false;
        foreach (string p in _exclude)
            if (MatchPattern(p, name))
                return false;
        return true;
    }

    public static bool MatchPattern(string pattern, string name)
    {
        if (pattern is "*" or "*.*")
            return true;
        // "name.*" also matches "name" (Windows semantics)
        if (pattern.EndsWith(".*", StringComparison.Ordinal) && !name.Contains('.'))
            return Match(pattern.AsSpan(0, pattern.Length - 2), name.AsSpan());
        return Match(pattern.AsSpan(), name.AsSpan());
    }

    /// <summary>Iterative '*'/'?' matcher with backtracking to the last star.</summary>
    private static bool Match(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
    {
        int p = 0, t = 0, starP = -1, starT = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starT = t;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                t = ++starT;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }

    public override string ToString() => Text;
}
