// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Core.Viewing;

/// <summary>
/// Finds where lines start in a stream of bytes, in any encoding: a line ends after "\n" (in the encoding's code unit),
/// and a line longer than <see cref="TextFile.MaxLineBytes"/> is cut into pieces (between characters for UTF-8 and
/// UTF-16), so a huge one-line file still shows and scrolls.
/// </summary>
internal sealed class LineScanner
{
    private readonly int _unit;
    private readonly bool _bigEndian;
    private readonly bool _utf8;
    private readonly int _maxLine;
    private long _lineStart;
    private long _pos;

    /// <param name="start">Where the first line starts (after the byte order mark); bytes are fed from there on.</param>
    public LineScanner(TextEncoding encoding, long start, int maxLineBytes)
    {
        _unit = encoding.UnitSize;
        _bigEndian = encoding.IsBigEndian;
        _utf8 = encoding.Encoding.CodePage == 65001;
        _maxLine = maxLineBytes - maxLineBytes % _unit;
        _lineStart = _pos = start;
    }

    /// <summary>Where the next bytes go (the file position after the bytes fed so far).</summary>
    public long Position => _pos;

    /// <summary>
    /// Takes the next bytes (a whole number of code units) and reports the start of every new line in them. A start at
    /// the very end of the file (after a final "\n") is reported too; the caller drops it.
    /// </summary>
    public void Feed(ReadOnlySpan<byte> data, Action<long> lineStart)
    {
        int count = data.Length - data.Length % _unit;
        for (int i = 0; i < count; i += _unit)
        {
            long abs = _pos + i;
            int value = _unit switch
            {
                1 => data[i],
                2 => _bigEndian ? data[i] << 8 | data[i + 1] : data[i] | data[i + 1] << 8,
                _ => _bigEndian ? data[i + 3] | data[i + 2] << 8 : data[i] | data[i + 1] << 8 | data[i + 2] << 16,
            };
            if (abs - _lineStart >= _maxLine && !IsContinuation(value))
            {
                _lineStart = abs; // a long line goes on in a new piece here
                lineStart(abs);
            }
            if (value == '\n')
            {
                _lineStart = abs + _unit;
                lineStart(_lineStart);
            }
        }
        _pos += count;
    }

    /// <summary>A code unit that can't start a character (a line piece must not begin with it).</summary>
    private bool IsContinuation(int value) =>
        _utf8 ? (value & 0xC0) == 0x80 :
        _unit == 2 && value is >= 0xDC00 and <= 0xDFFF;
}

/// <summary>
/// A text file of any size for the viewer: lines are found by a scan in the background, but only every 64th line's
/// start is kept, so even a file with a billion lines takes little memory; a line is read when it's shown (from the
/// nearest kept start). The file stays readable and deletable by others while it's open.
/// </summary>
public sealed class TextFile : IDisposable
{
    /// <summary>Lines longer than this (in bytes) are shown in pieces.</summary>
    public const int MaxLineBytes = 8192;

    private const int Every = 64;
    private const int BufferSize = 1 << 20;

    private readonly FileStream _stream;
    private readonly object _sync = new();
    private readonly List<long> _checkpoints = new(); // the start of line 0, 64, 128, ...
    private long _lineCount;
    private long _indexedBytes;
    private volatile bool _complete;

    private TextFile(string path, FileStream stream, TextEncoding encoding)
    {
        Path = path;
        _stream = stream;
        Length = stream.Length;
        Encoding = encoding;
        Start = Math.Min(encoding.BomLength, Length);
        if (Length > Start)
            _checkpoints.Add(Start);
    }

    public string Path { get; }

    /// <summary>The file's size when it was opened.</summary>
    public long Length { get; }

    public TextEncoding Encoding { get; }

    /// <summary>Where the text starts (after the byte order mark).</summary>
    public long Start { get; }

    /// <summary>Lines found so far (all of them once <see cref="IsComplete"/>).</summary>
    public long LineCount => Interlocked.Read(ref _lineCount);

    public bool IsComplete => _complete;

    /// <summary>How far the scan for lines has got (0 to <see cref="Length"/>).</summary>
    public long IndexedBytes => Interlocked.Read(ref _indexedBytes);

    /// <summary>Opens the file; the encoding is recognized from its first bytes unless one is given.</summary>
    public static TextFile Open(string path, TextEncoding? encoding = null)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        try
        {
            if (encoding == null)
            {
                byte[] head = new byte[Math.Min(64 * 1024, stream.Length)];
                stream.ReadExactly(head);
                encoding = TextEncodings.Detect(head);
            }
            return new TextFile(path, stream, encoding);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>The first bytes of a file (to recognize its encoding and whether it's text).</summary>
    public static byte[] ReadHead(string path, int count = 64 * 1024)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] head = new byte[Math.Min(count, stream.Length)];
        stream.ReadExactly(head);
        return head;
    }

    /// <summary>Finds the lines (on a background thread); <see cref="LineCount"/> grows while it runs.</summary>
    public Task IndexAsync(CancellationToken cancellation) => Task.Run(() =>
    {
        if (_complete)
            return;
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        stream.Position = Start;
        var scanner = new LineScanner(Encoding, Start, MaxLineBytes);
        byte[] buffer = new byte[BufferSize];
        long lines = Length > Start ? 1 : 0;
        Interlocked.Exchange(ref _lineCount, lines);
        long end = Length;
        var pending = new List<long>();
        void LineStart(long offset)
        {
            if (offset >= end)
                return;
            if (lines % Every == 0)
                pending.Add(offset);
            lines++;
        }
        while (scanner.Position < end)
        {
            cancellation.ThrowIfCancellationRequested();
            int want = (int)Math.Min(buffer.Length, end - scanner.Position);
            int read = stream.ReadAtLeast(buffer.AsSpan(0, want), want, throwOnEndOfStream: false);
            if (read <= 0)
                break; // the file got shorter
            scanner.Feed(buffer.AsSpan(0, read), LineStart);
            lock (_sync)
                _checkpoints.AddRange(pending);
            pending.Clear();
            Interlocked.Exchange(ref _lineCount, lines);
            Interlocked.Exchange(ref _indexedBytes, scanner.Position);
            if (read < want)
                break;
        }
        Interlocked.Exchange(ref _indexedBytes, end);
        _complete = true;
    }, cancellation);

    /// <summary>The lines from <paramref name="first"/> on (fewer at the end), without their line breaks.</summary>
    public IReadOnlyList<string> GetLines(long first, int count)
    {
        var result = new List<string>(count);
        foreach (var (_, start, end) in LineRanges(first, count))
            result.Add(Decode(start, end));
        return result;
    }

    public string GetLine(long line) => GetLines(line, 1) is [var text] ? text : string.Empty;

    /// <summary>Where a line's bytes start (for the hex view's "go to the line").</summary>
    public long LineOffset(long line) => LineRanges(line, 1).Select(r => r.Start).DefaultIfEmpty(Length).First();

    /// <summary>The line that contains a byte offset (within the lines found so far).</summary>
    public long LineAt(long offset)
    {
        int block;
        lock (_sync)
        {
            int index = _checkpoints.BinarySearch(offset);
            block = index >= 0 ? index : Math.Max(0, ~index - 1);
        }
        long line = (long)block * Every;
        foreach (var (number, start, end) in LineRanges(line, Every))
            if (offset < end || number == LineCount - 1)
                return number;
        return Math.Max(0, LineCount - 1);
    }

    /// <summary>The byte range of each line (with its line break) from <paramref name="first"/> on.</summary>
    private List<(long Line, long Start, long End)> LineRanges(long first, int count)
    {
        var ranges = new List<(long, long, long)>();
        long total = LineCount;
        if (first < 0 || first >= total || count <= 0)
            return ranges;
        long from;
        lock (_sync)
        {
            int block = (int)Math.Min(first / Every, _checkpoints.Count - 1);
            from = _checkpoints[block];
            long skip = first - (long)block * Every;
            return Scan(from, (long)block * Every, skip, Math.Min(count, total - first), ranges);
        }
    }

    /// <summary>Reads from a known line start and collects the ranges of the wanted lines (call under the lock).</summary>
    private List<(long, long, long)> Scan(long from, long lineOfFrom, long skip, long want,
        List<(long, long, long)> ranges)
    {
        var scanner = new LineScanner(Encoding, from, MaxLineBytes);
        long line = lineOfFrom, lineStart = from;
        byte[] buffer = new byte[64 * 1024];
        _stream.Position = from;
        bool done = false;
        void LineStart(long offset)
        {
            if (done)
                return;
            if (line - lineOfFrom >= skip)
                ranges.Add((line, lineStart, Math.Min(offset, Length)));
            line++;
            lineStart = offset;
            if (ranges.Count >= want || offset >= Length)
                done = true;
        }
        while (!done && scanner.Position < Length)
        {
            int wanted = (int)Math.Min(buffer.Length, Length - scanner.Position);
            int read = _stream.ReadAtLeast(buffer.AsSpan(0, wanted), wanted, throwOnEndOfStream: false);
            if (read <= 0)
                break;
            scanner.Feed(buffer.AsSpan(0, read), LineStart);
            if (read < wanted)
                break;
        }
        // the last line ends at the end of the file
        if (!done && lineStart < Length && line - lineOfFrom >= skip && ranges.Count < want)
            ranges.Add((line, lineStart, Length));
        return ranges;
    }

    private string Decode(long start, long end)
    {
        int length = (int)Math.Min(end - start, MaxLineBytes * 2);
        if (length <= 0)
            return string.Empty;
        byte[] bytes = new byte[length];
        lock (_sync)
        {
            _stream.Position = start;
            length = _stream.ReadAtLeast(bytes, length, throwOnEndOfStream: false);
        }
        string text = Encoding.Encoding.GetString(bytes, 0, length);
        return text.TrimEnd('\n', '\r');
    }

    /// <summary>Raw bytes (the hex view); fewer at the end of the file.</summary>
    public byte[] ReadBytes(long offset, int count)
    {
        if (offset >= Length || count <= 0)
            return Array.Empty<byte>();
        byte[] bytes = new byte[(int)Math.Min(count, Length - offset)];
        lock (_sync)
        {
            _stream.Position = offset;
            int read = _stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            return read == bytes.Length ? bytes : bytes[..read];
        }
    }

    /// <summary>
    /// The lines from <paramref name="first"/> to the end, read in large blocks (for searching); a separate reader, so
    /// the view keeps working meanwhile.
    /// </summary>
    public IEnumerable<(long Line, string Text)> ReadForward(long first)
    {
        long from, fromLine;
        lock (_sync)
        {
            if (_checkpoints.Count == 0)
                return Array.Empty<(long, string)>();
            int block = (int)Math.Min(Math.Max(0, first) / Every, _checkpoints.Count - 1);
            from = _checkpoints[block];
            fromLine = (long)block * Every;
        }
        return ReadForward(Math.Max(0, first), from, fromLine);
    }

    private IEnumerable<(long Line, string Text)> ReadForward(long first, long from, long fromLine)
    {
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        stream.Position = from;
        var scanner = new LineScanner(Encoding, from, MaxLineBytes);
        byte[] buffer = new byte[BufferSize];
        var pending = new byte[BufferSize + MaxLineBytes * 2];
        int pendingLength = 0;        // bytes of the current (unfinished) line
        long line = fromLine;
        var finished = new List<(long, string)>();
        long lineStart = from;
        long chunkStart;              // file position of buffer[0] in the chunk being fed
        int chunkOffset;              // how much of the chunk is already in pending

        while (scanner.Position < Length)
        {
            int wanted = (int)Math.Min(buffer.Length, Length - scanner.Position);
            int read = Fill(stream, buffer, wanted);
            read -= read % Encoding.UnitSize;
            if (read <= 0)
                break;
            chunkStart = scanner.Position;
            chunkOffset = 0;
            // the line starts in this chunk: each finished line is the pending bytes plus the chunk up to its end
            List<long> starts = Feed(scanner, buffer, read);
            foreach (long start in starts)
            {
                int upTo = (int)(start - chunkStart);
                int take = upTo - chunkOffset;
                if (line >= first)
                {
                    EnsureRoom(ref pending, pendingLength + take);
                    Buffer.BlockCopy(buffer, chunkOffset, pending, pendingLength, take);
                    finished.Add((line, Encoding.Encoding.GetString(pending, 0, pendingLength + take).TrimEnd('\n', '\r')));
                }
                pendingLength = 0;
                chunkOffset = upTo;
                lineStart = start;
                line++;
            }
            if (line >= first)
            {
                int rest = read - chunkOffset;
                EnsureRoom(ref pending, pendingLength + rest);
                Buffer.BlockCopy(buffer, chunkOffset, pending, pendingLength, rest);
                pendingLength += rest;
            }
            foreach (var item in finished)
                yield return item;
            finished.Clear();
            if (read < wanted)
                break;
        }
        if (pendingLength > 0 && line >= first && lineStart < Length)
            yield return (line, Encoding.Encoding.GetString(pending, 0, pendingLength).TrimEnd('\n', '\r'));
    }

    private static int Fill(Stream stream, byte[] buffer, int count) =>
        stream.ReadAtLeast(buffer.AsSpan(0, count), count, throwOnEndOfStream: false);

    private static List<long> Feed(LineScanner scanner, byte[] buffer, int count)
    {
        var starts = new List<long>();
        scanner.Feed(buffer.AsSpan(0, count), starts.Add);
        return starts;
    }

    private static void EnsureRoom(ref byte[] array, int size)
    {
        if (array.Length < size)
            Array.Resize(ref array, Math.Max(size, array.Length * 2));
    }

    /// <summary>The lines from <paramref name="last"/> back to the first (for searching backwards).</summary>
    public IEnumerable<(long Line, string Text)> ReadBackward(long last)
    {
        last = Math.Min(last, LineCount - 1);
        if (last < 0)
            yield break;
        for (long blockStart = last / Every * Every; blockStart >= 0; blockStart -= Every)
        {
            int count = (int)Math.Min(Every, last - blockStart + 1);
            IReadOnlyList<string> block = GetLines(blockStart, count);
            for (int i = block.Count - 1; i >= 0; i--)
                yield return (blockStart + i, block[i]);
            last = blockStart - 1;
        }
    }

    public void Dispose() => _stream.Dispose();
}
