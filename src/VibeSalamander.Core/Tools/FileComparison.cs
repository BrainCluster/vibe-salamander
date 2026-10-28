// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using VibeSalamander.Core.Viewing;

namespace VibeSalamander.Core.Tools;

/// <summary>What Compare Files shows: two texts side by side, or the bytes of two binary files.</summary>
public enum ComparisonKind
{
    Text,
    Binary,

    /// <summary>The files are the same, byte for byte.</summary>
    Identical,
}

/// <summary>Two binary files as hex rows: only the rows that differ, with a few rows around them.</summary>
/// <param name="Left">The left file's rows ("00000010  4D 5A ...  MZ..."), "···" between parts that are far apart.</param>
/// <param name="Differences">Ranges of bytes that differ (offset, length), at most <see cref="FileComparison.MaxRanges"/>.</param>
/// <param name="DifferentBytes">How many bytes differ in the part both files have.</param>
/// <param name="Truncated">More differences than are shown.</param>
public sealed record BinaryComparison(string Left, string Right, IReadOnlyList<(long Offset, long Length)> Differences,
    long DifferentBytes, long LeftLength, long RightLength, bool Truncated);

/// <summary>Compare Files (Ctrl+Shift+C): which two files, and how to show them.</summary>
public static class FileComparison
{
    public const int MaxRanges = 2000;
    private const int Context = 2; // rows of 16 bytes shown around a difference
    private const int MaxRows = 20_000;

    /// <summary>Text files are compared as text (up to <see cref="TextDocuments.MaxEditableBytes"/>), others as bytes.</summary>
    public static ComparisonKind KindOf(string left, string right)
    {
        if (SameContent(left, right))
            return ComparisonKind.Identical;
        return IsText(left) && IsText(right) ? ComparisonKind.Text : ComparisonKind.Binary;
    }

    private static bool IsText(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > TextDocuments.MaxEditableBytes)
            return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var head = new byte[8192];
        int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        TextEncoding encoding = TextEncodings.Detect(head.AsSpan(0, read));
        return !TextEncodings.LooksBinary(head.AsSpan(0, read), encoding);
    }

    private static bool SameContent(string a, string b) => DirectoryComparer.ContentEqual(a, b);

    /// <summary>
    /// Which two files Compare Files takes: two marked in the active panel; one marked (or the caret's) and the file
    /// of the same name in the other panel, or the other panel's caret file. Null when it can't tell.
    /// </summary>
    public static (string Left, string Right)? Pick(IReadOnlyList<string> activeMarked, string? activeCaret, bool activeIsLeft,
        IReadOnlyList<string> otherMarked, string? otherCaret, string? otherDirectory)
    {
        static bool IsFile(string? p) => p != null && File.Exists(p);
        (string, string) Ordered(string active, string other) => activeIsLeft ? (active, other) : (other, active);

        var marked = activeMarked.Where(IsFile).ToList();
        if (marked.Count == 2)
            return (marked[0], marked[1]);
        string? one = marked.Count == 1 ? marked[0] : IsFile(activeCaret) ? activeCaret : null;
        if (one == null)
            return null;
        var otherFiles = otherMarked.Where(IsFile).ToList();
        if (otherFiles.Count == 1)
            return Ordered(one, otherFiles[0]);
        if (otherDirectory != null)
        {
            string sameName = Path.Combine(otherDirectory, Path.GetFileName(one));
            if (IsFile(sameName) && !string.Equals(sameName, one, StringComparison.OrdinalIgnoreCase))
                return Ordered(one, sameName);
        }
        if (IsFile(otherCaret) && !string.Equals(otherCaret, one, StringComparison.OrdinalIgnoreCase))
            return Ordered(one, otherCaret!);
        return null;
    }

    /// <summary>The bytes that differ, as hex rows for both sides (the same rows on both, so they line up).</summary>
    public static BinaryComparison CompareBinary(string left, string right, CancellationToken ct = default)
    {
        using var a = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        using var b = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        long common = Math.Min(a.Length, b.Length);
        var ranges = new List<(long Offset, long Length)>();
        long different = 0;
        bool truncated = false;
        var ba = new byte[1 << 16];
        var bb = new byte[1 << 16];
        long position = 0;
        long start = -1;
        while (position < common)
        {
            ct.ThrowIfCancellationRequested();
            int want = (int)Math.Min(ba.Length, common - position);
            int na = a.ReadAtLeast(ba.AsSpan(0, want), want, throwOnEndOfStream: false);
            int nb = b.ReadAtLeast(bb.AsSpan(0, want), want, throwOnEndOfStream: false);
            int n = Math.Min(na, nb);
            if (n <= 0)
                break;
            for (int i = 0; i < n; i++)
            {
                bool differs = ba[i] != bb[i];
                if (differs)
                {
                    different++;
                    if (start < 0)
                        start = position + i;
                }
                else if (start >= 0)
                {
                    Add(ranges, start, position + i - start, ref truncated);
                    start = -1;
                }
            }
            position += n;
        }
        if (start >= 0)
            Add(ranges, start, position - start, ref truncated);
        if (a.Length != b.Length)
            Add(ranges, common, Math.Abs(a.Length - b.Length), ref truncated); // the longer one's tail

        // the rows to show: each difference with a little around it, the same rows on both sides
        var rows = new SortedSet<long>();
        foreach (var (offset, length) in ranges)
        {
            long first = offset / HexFormat.BytesPerRow - Context, last = (offset + length - 1) / HexFormat.BytesPerRow + Context;
            for (long r = Math.Max(0, first); r <= last && rows.Count < MaxRows; r++)
                rows.Add(r);
        }
        long longest = Math.Max(a.Length, b.Length);
        int digits = HexFormat.OffsetDigits(longest);
        return new BinaryComparison(Dump(a, rows, digits), Dump(b, rows, digits), ranges, different, a.Length, b.Length,
            truncated || rows.Count >= MaxRows);
    }

    private static void Add(List<(long, long)> ranges, long offset, long length, ref bool truncated)
    {
        if (ranges.Count < MaxRanges)
            ranges.Add((offset, length));
        else
            truncated = true;
    }

    private static string Dump(FileStream stream, SortedSet<long> rows, int digits)
    {
        var text = new StringBuilder();
        var row = new byte[HexFormat.BytesPerRow];
        long previous = -2;
        foreach (long r in rows)
        {
            if (r != previous + 1 && previous >= 0)
                text.Append("···\n");
            previous = r;
            long offset = r * HexFormat.BytesPerRow;
            if (offset >= stream.Length)
            {
                text.Append(offset.ToString("X" + digits, System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                continue; // past this file's end: the row is there on the other side
            }
            stream.Seek(offset, SeekOrigin.Begin);
            int n = stream.ReadAtLeast(row, row.Length, throwOnEndOfStream: false);
            text.Append(HexFormat.Row(row.AsSpan(0, n), offset, digits)).Append('\n');
        }
        return text.ToString();
    }
}
