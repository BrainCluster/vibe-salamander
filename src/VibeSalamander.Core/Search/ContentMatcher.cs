// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.Localization;
using System.Text;
using System.Text.RegularExpressions;
using VibeSalamander.Core.Viewing;

namespace VibeSalamander.Core.Search;

/// <summary>How a text row of the search is read.</summary>
public enum TextMode
{
    /// <summary>Plain text (Match case and Whole words apply).</summary>
    Text,

    /// <summary>A .NET regular expression, one line at a time.</summary>
    Regex,

    /// <summary>Bytes in hex ("4D 5A 90").</summary>
    Hex,
}

/// <summary>One text row of the search: what the file must contain, or must not contain.</summary>
public sealed record TextCondition(string Text, TextMode Mode = TextMode.Text, bool Negate = false);

/// <summary>Where a file's content matched: the line (1-based) and the line's text around the match.</summary>
/// <param name="Line">1-based line, or 0 for a match in a binary file or a hex match.</param>
/// <param name="Snippet">The line (shortened around the match), or the bytes around a binary match as text.</param>
/// <param name="Column">Where the match starts in <see cref="Snippet"/>.</param>
/// <param name="Offset">The byte offset of a hex / binary match, else −1.</param>
public sealed record ContentMatch(long Line, string Snippet, int Column, int Length, long Offset = -1);

/// <summary>
/// Checks a file's content against the text rows (all or any of them). Text files are read line by line in their
/// own encoding (detected: BOM, UTF-16, UTF-8 or ANSI), so the match comes with its line; binary files are searched
/// as bytes (the text in UTF-8, UTF-16 and ANSI). Thread-safe: one matcher serves all the search's workers.
/// </summary>
public sealed class ContentMatcher
{
    /// <summary>A line longer than this is cut into pieces (minified files: one line of megabytes).</summary>
    private const int MaxLine = 64 * 1024;
    private const int SnippetLength = 160;

    private readonly Condition[] _conditions;
    private readonly bool _all;

    private sealed class Condition
    {
        public required TextCondition Source { get; init; }
        public Func<string, (int Index, int Length)?>? Line { get; init; }
        public Regex? Binary { get; init; }
        public byte[][] Needles { get; init; } = Array.Empty<byte[]>();
        public bool FoldCase { get; init; }
    }

    /// <param name="all">Every row must hold (else any one is enough).</param>
    public ContentMatcher(IReadOnlyList<TextCondition> conditions, bool all, bool matchCase, bool wholeWord)
    {
        _all = all;
        _conditions = conditions.Where(c => c.Text.Length > 0).Select(c => Build(c, matchCase, wholeWord)).ToArray();
    }

    /// <summary>True when there is nothing to look for in the content.</summary>
    public bool IsEmpty => _conditions.Length == 0;

    /// <summary>Why the rows can't be used (an invalid regular expression or hex), or null.</summary>
    public static string? Problem(IEnumerable<TextCondition> conditions)
    {
        foreach (TextCondition c in conditions.Where(c => c.Text.Length > 0))
        {
            if (c.Mode == TextMode.Hex && ByteSearch.Pattern(c.Text, asHex: true, Encoding.UTF8) == null)
                return L.F("\"{0}\" isn't hex bytes (like 4D 5A 90).", c.Text);
            if (c.Mode == TextMode.Regex && TextSearch.Problem(new FindOptions(c.Text, Regex: true)) is { } problem)
                return problem;
        }
        return null;
    }

    private static Condition Build(TextCondition c, bool matchCase, bool wholeWord)
    {
        switch (c.Mode)
        {
            case TextMode.Hex:
                return new Condition { Source = c, Needles = new[] { ByteSearch.Pattern(c.Text, asHex: true, Encoding.UTF8)! } };
            case TextMode.Regex:
            {
                var options = new FindOptions(c.Text, matchCase, false, Regex: true);
                var match = TextSearch.Matcher(options);
                var regex = new Regex(c.Text, (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant);
                return new Condition { Source = c, Line = line => match(line, 0, false), Binary = regex };
            }
            default:
            {
                var match = TextSearch.Matcher(new FindOptions(c.Text, matchCase, wholeWord));
                string folded = matchCase ? c.Text : FoldAscii(c.Text);
                var needles = new[] { Encoding.UTF8.GetBytes(folded), Encoding.Unicode.GetBytes(folded), TextEncodings.Ansi.Encoding.GetBytes(folded) }
                    .Where(n => n.Length > 0).DistinctBy(Convert.ToHexString).ToArray();
                return new Condition { Source = c, Line = line => match(line, 0, false), Needles = needles, FoldCase = !matchCase };
            }
        }
    }

    private static string FoldAscii(string s)
    {
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (chars[i] is >= 'A' and <= 'Z')
                chars[i] = (char)(chars[i] + 32);
        return new string(chars);
    }

    /// <summary>Checks a file on disk; null when the rows don't hold (or it can't be read).</summary>
    public ContentMatch? Check(string path, CancellationToken ct)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            return Check(stream, () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Checks a stream (a file in an archive too). <paramref name="reopen"/> gives the content again from the start
    /// when a second pass is needed (hex rows in a text file).
    /// </summary>
    public ContentMatch? Check(Stream stream, Func<Stream> reopen, CancellationToken ct)
    {
        if (_conditions.Length == 0)
            return new ContentMatch(0, string.Empty, 0, 0);
        var head = new byte[64 * 1024];
        int headLength = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        TextEncoding encoding = TextEncodings.Detect(head.AsSpan(0, headLength));
        bool binary = TextEncodings.LooksBinary(head.AsSpan(0, Math.Min(headLength, 8192)), encoding);
        var state = new State(_conditions.Length);
        var prefixed = new PrefixedStream(head, headLength, stream);

        if (binary)
            ScanBytes(prefixed, state, ct, textToo: true);
        else
        {
            ScanLines(prefixed, encoding, state, ct);
            if (_conditions.Any(c => c.Source.Mode == TextMode.Hex) && !state.Decided(_conditions, _all))
            {
                using Stream again = reopen();
                ScanBytes(again, state, ct, textToo: false);
            }
        }
        return state.Result(_conditions, _all);
    }

    /// <summary>What has been found so far in a file.</summary>
    private sealed class State
    {
        public readonly bool[] Found;
        public ContentMatch? First;

        public State(int count) => Found = new bool[count];

        private static bool Holds(Condition c, bool found) => c.Source.Negate ? !found : found;

        /// <summary>The answer can't change any more: the rest of the file needn't be read.</summary>
        public bool Decided(Condition[] conditions, bool all)
        {
            if (all)
            {
                // a "doesn't contain" row was found: fails; with such rows the end must be reached to hold
                bool negative = false;
                for (int i = 0; i < conditions.Length; i++)
                {
                    if (!conditions[i].Source.Negate)
                        continue;
                    negative = true;
                    if (Found[i])
                        return true;
                }
                if (negative)
                    return false;
                for (int i = 0; i < conditions.Length; i++)
                    if (!Found[i])
                        return false;
                return true;
            }
            for (int i = 0; i < conditions.Length; i++)
                if (!conditions[i].Source.Negate && Found[i])
                    return true;
            return false;
        }

        public ContentMatch? Result(Condition[] conditions, bool all)
        {
            bool ok = all
                ? conditions.Select((c, i) => Holds(c, Found[i])).All(x => x)
                : conditions.Select((c, i) => Holds(c, Found[i])).Any(x => x);
            if (!ok)
                return null;
            return First ?? new ContentMatch(0, string.Empty, 0, 0);
        }
    }

    private void ScanLines(Stream stream, TextEncoding encoding, State state, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, encoding.Encoding, detectEncodingFromByteOrderMarks: encoding.HasBom, bufferSize: 64 * 1024);
        var buffer = new char[64 * 1024];
        var line = new StringBuilder();
        long number = 1;
        bool lastWasCr = false;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            for (int i = 0; i < read; i++)
            {
                char ch = buffer[i];
                if (ch == '\n' && lastWasCr)
                {
                    lastWasCr = false;
                    continue; // the second half of \r\n
                }
                lastWasCr = ch == '\r';
                if (ch is '\n' or '\r')
                {
                    if (CheckLine(line.ToString(), number, state))
                        return;
                    line.Clear();
                    number++;
                    continue;
                }
                line.Append(ch);
                if (line.Length >= MaxLine)
                {
                    if (CheckLine(line.ToString(), number, state))
                        return;
                    line.Clear(); // the same line goes on
                }
            }
        }
        if (line.Length > 0)
            CheckLine(line.ToString(), number, state);
    }

    /// <summary>Checks one line against the text rows; true when the file is decided.</summary>
    private bool CheckLine(string line, long number, State state)
    {
        for (int i = 0; i < _conditions.Length; i++)
        {
            Condition c = _conditions[i];
            if (state.Found[i] || c.Line == null)
                continue;
            if (c.Line(line) is not { } m)
                continue;
            state.Found[i] = true;
            if (!c.Source.Negate && state.First == null)
                state.First = Snippet(line, number, m.Index, m.Length);
        }
        return state.Decided(_conditions, _all);
    }

    /// <summary>The line shortened to about <see cref="SnippetLength"/> characters around the match.</summary>
    private static ContentMatch Snippet(string line, long number, int index, int length)
    {
        int start = 0;
        if (line.Length > SnippetLength && index > 40)
            start = Math.Min(index - 40, Math.Max(0, line.Length - SnippetLength));
        // leading spaces say nothing
        while (start < index && start < line.Length && char.IsWhiteSpace(line[start]))
            start++;
        int end = Math.Min(line.Length, start + SnippetLength);
        string text = line[start..end].Replace('\t', ' ');
        string prefix = start > 0 && !char.IsWhiteSpace(line[start - 1]) ? "…" : string.Empty;
        string suffix = end < line.Length ? "…" : string.Empty;
        return new ContentMatch(number, prefix + text + suffix, prefix.Length + index - start, Math.Min(length, Math.Max(0, end - index)));
    }

    /// <summary>Byte search: hex rows always; text rows in binary files (UTF-8, UTF-16, ANSI; ASCII case folded).</summary>
    private void ScanBytes(Stream stream, State state, CancellationToken ct, bool textToo)
    {
        var active = new List<int>();
        for (int i = 0; i < _conditions.Length; i++)
        {
            Condition c = _conditions[i];
            if (state.Found[i])
                continue;
            if (c.Source.Mode == TextMode.Hex || (textToo && c.Needles.Length > 0) || (textToo && c.Binary != null))
                active.Add(i);
        }
        if (active.Count == 0)
            return;
        int longest = active.Max(i => _conditions[i].Binary != null ? 4096 : _conditions[i].Needles.Max(n => n.Length));
        const int chunk = 256 * 1024;
        var buffer = new byte[chunk + longest];
        var folded = new byte[chunk + longest];
        int carry = 0;
        long position = 0; // offset of buffer[0] in the file
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int read = stream.ReadAtLeast(buffer.AsSpan(carry, chunk), chunk, throwOnEndOfStream: false);
            if (read <= 0)
                return;
            int length = carry + read;
            bool foldedReady = false;
            foreach (int i in active)
            {
                if (state.Found[i])
                    continue;
                Condition c = _conditions[i];
                int at = -1, matchLength = 0;
                if (c.Binary != null)
                {
                    // a regular expression on the bytes read as Latin-1 (one character per byte)
                    string text = Encoding.Latin1.GetString(buffer, 0, length);
                    Match m = c.Binary.Match(text);
                    if (m.Success)
                    {
                        at = m.Index;
                        matchLength = m.Length;
                    }
                }
                else
                {
                    ReadOnlySpan<byte> span = buffer.AsSpan(0, length);
                    if (c.FoldCase)
                    {
                        if (!foldedReady)
                        {
                            for (int k = 0; k < length; k++)
                                folded[k] = buffer[k] is >= (byte)'A' and <= (byte)'Z' ? (byte)(buffer[k] + 32) : buffer[k];
                            foldedReady = true;
                        }
                        span = folded.AsSpan(0, length);
                    }
                    foreach (byte[] needle in c.Needles)
                    {
                        int index = span.IndexOf(needle);
                        if (index >= 0 && (at < 0 || index < at))
                        {
                            at = index;
                            matchLength = needle.Length;
                        }
                    }
                }
                if (at < 0)
                    continue;
                state.Found[i] = true;
                if (!c.Source.Negate && state.First == null)
                    state.First = BinarySnippet(buffer, length, at, matchLength, position + at);
            }
            if (state.Decided(_conditions, _all) || active.All(i => state.Found[i]))
                return;
            // the tail stays, for matches across the chunks' edge
            int keep = Math.Min(longest - 1, length);
            Buffer.BlockCopy(buffer, length - keep, buffer, 0, keep);
            position += length - keep;
            carry = keep;
        }
    }

    /// <summary>The bytes around a binary match, printable ones as they are, others as dots.</summary>
    private static ContentMatch BinarySnippet(byte[] buffer, int length, int at, int matchLength, long offset)
    {
        int start = Math.Max(0, at - 24);
        int end = Math.Min(length, at + Math.Max(matchLength, 1) + 48);
        var text = new StringBuilder(end - start);
        for (int k = start; k < end; k++)
            text.Append(buffer[k] is >= 0x20 and < 0x7F ? (char)buffer[k] : '·');
        return new ContentMatch(0, text.ToString(), at - start, Math.Min(matchLength, end - at), offset);
    }

    /// <summary>The already-read head of a stream followed by the rest of it.</summary>
    private sealed class PrefixedStream : Stream
    {
        private readonly byte[] _head;
        private readonly int _headLength;
        private readonly Stream _rest;
        private int _position;

        public PrefixedStream(byte[] head, int headLength, Stream rest)
        {
            _head = head;
            _headLength = headLength;
            _rest = rest;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position < _headLength)
            {
                int n = Math.Min(buffer.Length, _headLength - _position);
                _head.AsSpan(_position, n).CopyTo(buffer);
                _position += n;
                return n;
            }
            return _rest.Read(buffer);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
