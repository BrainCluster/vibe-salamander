// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using VibeSalamander.Core.Viewing;

namespace VibeSalamander.Views.Viewer;

/// <summary>
/// The text view's rows: the file's lines, or, wrapped, each line in rows as wide as the view (wrapping needs every
/// line's length, which is read once in the background; very large files aren't wrapped).
/// </summary>
internal sealed class TextRowSource : IRowSource
{
    /// <summary>Files up to this many lines can be wrapped.</summary>
    public const long MaxWrapLines = 4_000_000;

    private readonly TextFile _file;
    private int[]? _lengths;
    private long[]? _firstRow; // the first row of each line, and the row count at the end
    private int _columns = 80;

    public TextRowSource(TextFile file) => _file = file;

    public TextFile File => _file;

    public bool Wrapped => _firstRow != null;

    /// <summary>The line lengths for wrapping (read after the scan for lines has finished).</summary>
    public Task MeasureAsync(CancellationToken cancellation) => Task.Run(() =>
    {
        if (_lengths != null || !_file.IsComplete || _file.LineCount > MaxWrapLines)
            return;
        var lengths = new int[_file.LineCount];
        foreach (var (line, text) in _file.ReadForward(0))
        {
            cancellation.ThrowIfCancellationRequested();
            if (line < lengths.Length)
                lengths[line] = text.Length;
        }
        _lengths = lengths;
    }, cancellation);

    public bool CanWrap => _lengths != null;

    /// <summary>Wraps at <paramref name="columns"/> characters, or stops wrapping (false or no lengths yet).</summary>
    public void SetWrap(bool wrap, int columns)
    {
        if (!wrap || _lengths == null)
        {
            _firstRow = null;
            return;
        }
        _columns = Math.Max(10, columns);
        var first = new long[_lengths.Length + 1];
        long row = 0;
        for (int i = 0; i < _lengths.Length; i++)
        {
            first[i] = row;
            row += Math.Max(1, (_lengths[i] + _columns - 1) / _columns);
        }
        first[^1] = row;
        _firstRow = first;
    }

    public long RowCount => _firstRow is { } first ? first[^1] : _file.LineCount;

    /// <summary>The line a row belongs to, and where the row starts in it.</summary>
    public (long Line, int Column) LineOf(long row)
    {
        if (_firstRow is not { } first)
            return (row, 0);
        int index = Array.BinarySearch(first, 0, first.Length - 1, row);
        int line = index >= 0 ? index : ~index - 1;
        // a line of several rows: binary search finds its first row exactly or the line before the next one
        return (line, (int)(row - first[line]) * _columns);
    }

    /// <summary>The row that shows a column of a line.</summary>
    public (long Row, int Column) RowOf(long line, int column)
    {
        if (_firstRow is not { } first || line >= first.Length - 1)
            return (line, column);
        return (first[line] + column / _columns, column % _columns);
    }

    public IReadOnlyList<string> Rows(long firstRow, int count)
    {
        if (_firstRow == null)
            return _file.GetLines(firstRow, count);
        var rows = new List<string>(count);
        var (line, column) = LineOf(firstRow);
        while (rows.Count < count && line < _file.LineCount)
        {
            IReadOnlyList<string> lines = _file.GetLines(line, Math.Max(8, count - rows.Count));
            if (lines.Count == 0)
                break;
            foreach (string text in lines)
            {
                if (text.Length == 0 && column == 0)
                {
                    rows.Add(string.Empty);
                }
                for (int at = column; at < text.Length && rows.Count < count; at += _columns)
                    rows.Add(text.Substring(at, Math.Min(_columns, text.Length - at)));
                column = 0;
                line++;
                if (rows.Count >= count)
                    break;
            }
        }
        return rows;
    }

    public bool ContinuesNext(long row)
    {
        if (_firstRow is { } first)
        {
            var (line, _) = LineOf(row);
            return line + 1 < first.Length && row + 1 < first[line + 1];
        }
        return _file.IsPiece(row);
    }

    public string? Label(long row)
    {
        if (_firstRow is { } first)
        {
            var (line, column) = LineOf(row);
            return column == 0 ? (line + 1).ToString(CultureInfo.CurrentCulture) : string.Empty;
        }
        return (row + 1).ToString(CultureInfo.CurrentCulture);
    }
}

/// <summary>The hex view's rows: 16 bytes each, with their offset and the bytes as characters.</summary>
internal sealed class HexRowSource : IRowSource
{
    private readonly TextFile _file;
    private readonly int _digits;

    public HexRowSource(TextFile file)
    {
        _file = file;
        _digits = HexFormat.OffsetDigits(file.Length);
    }

    public long RowCount => (_file.Length + HexFormat.BytesPerRow - 1) / HexFormat.BytesPerRow;

    public int OffsetDigits => _digits;

    public IReadOnlyList<string> Rows(long first, int count)
    {
        byte[] bytes = _file.ReadBytes(first * HexFormat.BytesPerRow, count * HexFormat.BytesPerRow);
        var rows = new List<string>(count);
        for (int i = 0; i * HexFormat.BytesPerRow < bytes.Length; i++)
        {
            int start = i * HexFormat.BytesPerRow;
            int length = Math.Min(HexFormat.BytesPerRow, bytes.Length - start);
            rows.Add(HexFormat.Row(bytes.AsSpan(start, length), (first + i) * HexFormat.BytesPerRow, _digits));
        }
        return rows;
    }

    public bool ContinuesNext(long row) => false;

    public string? Label(long row) => null;
}
