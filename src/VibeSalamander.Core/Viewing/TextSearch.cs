// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VibeSalamander.Core.Viewing;

/// <param name="Text">What to find: text, a regular expression, or hex bytes ("4D 5A", "4d5a").</param>
/// <param name="WholeWord">Only where the text isn't part of a longer word.</param>
/// <param name="Regex">The text is a .NET regular expression (one line at a time).</param>
public sealed record FindOptions(string Text, bool MatchCase = false, bool WholeWord = false, bool Regex = false);

/// <summary>A match in a text: its line, where it starts in the line and how long it is (in characters).</summary>
public readonly record struct TextMatch(long Line, int Column, int Length);

/// <summary>Finding text in a <see cref="TextFile"/> of any size, line by line, forward or backward, wrapping around.</summary>
public static class TextSearch
{
    /// <summary>Why the options can't be used (an invalid regular expression), or null.</summary>
    public static string? Problem(FindOptions options)
    {
        if (options.Text.Length == 0)
            return "Type what to find.";
        if (!options.Regex)
            return null;
        try
        {
            _ = new Regex(options.Text);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>A function that finds the first (or, backwards, the last) match in a line before / after a column.</summary>
    public static Func<string, int, bool, (int Index, int Length)?> Matcher(FindOptions options)
    {
        if (options.Regex)
        {
            var regex = new Regex(options.Text, (options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant);
            return (line, from, backwards) =>
            {
                if (!backwards)
                {
                    Match m = regex.Match(line, Math.Clamp(from, 0, line.Length));
                    return m.Success ? (m.Index, m.Length) : null;
                }
                (int, int)? found = null;
                foreach (Match m in regex.Matches(line))
                {
                    if (m.Index >= from)
                        break;
                    found = (m.Index, m.Length);
                }
                return found;
            };
        }
        StringComparison comparison = options.MatchCase ? StringComparison.CurrentCulture : StringComparison.CurrentCultureIgnoreCase;
        string text = options.Text;
        return (line, from, backwards) =>
        {
            // backwards: a match that starts before "from" (LastIndexOf wants where such a match would end)
            int Before(int start) => start <= 0 || line.Length == 0 ? -1
                : line.LastIndexOf(text, Math.Min(start - 1 + text.Length - 1, line.Length - 1), comparison);
            int index = backwards ? Before(from) : (from > line.Length ? -1 : line.IndexOf(text, from, comparison));
            while (index >= 0)
            {
                if (!options.WholeWord || IsWholeWord(line, index, text.Length))
                    return (index, text.Length);
                index = backwards ? Before(index) : (index + 1 > line.Length ? -1 : line.IndexOf(text, index + 1, comparison));
            }
            return null;
        };
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsWholeWord(string line, int index, int length) =>
        (index == 0 || !IsWordChar(line[index - 1])) && (index + length >= line.Length || !IsWordChar(line[index + length]));

    /// <summary>
    /// The next match after (line, column), or before it when <paramref name="backwards"/>; when the end (start) of the
    /// file is reached, the search goes on from the other end up to where it began (<paramref name="wrapped"/> says so).
    /// </summary>
    public static TextMatch? Find(TextFile file, FindOptions options, long line, int column, bool backwards,
        out bool wrapped, CancellationToken cancellation = default)
    {
        wrapped = false;
        var match = Matcher(options);
        long total = file.LineCount;
        if (total == 0)
            return null;
        line = Math.Clamp(line, 0, total - 1);
        if (!backwards)
        {
            foreach (var (number, text) in file.ReadForward(line))
            {
                cancellation.ThrowIfCancellationRequested();
                if (match(text, number == line ? column : 0, false) is { } m)
                    return new TextMatch(number, m.Index, m.Length);
            }
            wrapped = true;
            foreach (var (number, text) in file.ReadForward(0))
            {
                cancellation.ThrowIfCancellationRequested();
                if (number > line)
                    break;
                if (match(text, 0, false) is { } m && (number < line || m.Index < column))
                    return new TextMatch(number, m.Index, m.Length);
            }
            return null;
        }
        foreach (var (number, text) in file.ReadBackward(line))
        {
            cancellation.ThrowIfCancellationRequested();
            if (match(text, number == line ? column : text.Length + 1, true) is { } m)
                return new TextMatch(number, m.Index, m.Length);
        }
        wrapped = true;
        foreach (var (number, text) in file.ReadBackward(file.LineCount - 1))
        {
            cancellation.ThrowIfCancellationRequested();
            if (number < line)
                break;
            if (match(text, text.Length + 1, true) is { } m && (number > line || m.Index > column))
                return new TextMatch(number, m.Index, m.Length);
        }
        return null;
    }
}

/// <summary>Finding bytes in a file (the hex view).</summary>
public static class ByteSearch
{
    /// <summary>
    /// The bytes to find: hex digits ("4D 5A 90", "4d5a90", "0x4D 0x5A") when <paramref name="asHex"/>, else the text
    /// in the file's encoding. Null when hex digits are expected and something else was typed.
    /// </summary>
    public static byte[]? Pattern(string text, bool asHex, Encoding encoding)
    {
        if (!asHex)
            return text.Length == 0 ? null : encoding.GetBytes(text);
        var digits = new StringBuilder();
        foreach (string part in text.Split(new[] { ' ', ',', '-', ':' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string p = part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? part[2..] : part;
            if (p.Length % 2 == 1)
                p = "0" + p;
            digits.Append(p);
        }
        string all = digits.ToString();
        if (all.Length == 0)
            return null;
        var bytes = new byte[all.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            if (!byte.TryParse(all.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                return null;
        return bytes;
    }

    /// <summary>The offset of the next match from <paramref name="from"/> (or the last one before it), or −1.</summary>
    public static long Find(Func<long, int, byte[]> read, long length, byte[] pattern, long from, bool backwards,
        CancellationToken cancellation = default)
    {
        if (pattern.Length == 0 || pattern.Length > length)
            return -1;
        const int block = 1 << 20;
        if (!backwards)
        {
            for (long start = Math.Max(0, from); start < length; start += block)
            {
                cancellation.ThrowIfCancellationRequested();
                byte[] bytes = read(start, block + pattern.Length - 1);
                int index = bytes.AsSpan().IndexOf(pattern);
                if (index >= 0)
                    return start + index;
            }
            return -1;
        }
        for (long end = Math.Min(from, length); end > 0; end -= block)
        {
            cancellation.ThrowIfCancellationRequested();
            long start = Math.Max(0, end - block);
            byte[] bytes = read(start, (int)(end - start) + pattern.Length - 1);
            // only matches that start before "end"
            int limit = (int)(end - start);
            int index = bytes.AsSpan(0, Math.Min(bytes.Length, limit + pattern.Length - 1)).LastIndexOf(pattern);
            while (index >= limit)
                index = index == 0 ? -1 : bytes.AsSpan(0, index + pattern.Length - 1).LastIndexOf(pattern);
            if (index >= 0)
                return start + index;
        }
        return -1;
    }
}

/// <summary>The rows of the hex view: offset, 16 bytes in hex, the same bytes as characters.</summary>
public static class HexFormat
{
    public const int BytesPerRow = 16;

    /// <summary>Hex digits for the offsets: 8, or more for files over 4 GB.</summary>
    public static int OffsetDigits(long length) => length > 0xFFFF_FFFFL ? 12 : 8;

    /// <summary>"00000010  48 65 6C 6C 6F 20 57 6F  72 6C 64 0D 0A 00 FF 7E  Hello World....~"</summary>
    public static string Row(ReadOnlySpan<byte> bytes, long offset, int offsetDigits)
    {
        var sb = new StringBuilder(offsetDigits + 2 + BytesPerRow * 3 + 2 + BytesPerRow);
        sb.Append(offset.ToString("X" + offsetDigits, CultureInfo.InvariantCulture)).Append("  ");
        for (int i = 0; i < BytesPerRow; i++)
        {
            if (i == 8)
                sb.Append(' ');
            sb.Append(i < bytes.Length ? bytes[i].ToString("X2", CultureInfo.InvariantCulture) : "  ").Append(' ');
        }
        sb.Append(' ');
        for (int i = 0; i < bytes.Length && i < BytesPerRow; i++)
            sb.Append(bytes[i] is >= 0x20 and < 0x7F ? (char)bytes[i] : '.');
        return sb.ToString();
    }

    /// <summary>Where a byte's hex digits start in a row (for highlighting a match).</summary>
    public static int HexColumn(int byteInRow, int offsetDigits) => offsetDigits + 2 + byteInRow * 3 + (byteInRow >= 8 ? 1 : 0);

    /// <summary>Where a byte's character is in a row.</summary>
    public static int CharColumn(int byteInRow, int offsetDigits) => offsetDigits + 2 + BytesPerRow * 3 + 1 + 1 + byteInRow;
}
