// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Plugins.Archive;

/// <summary>
/// The parts of a split archive ("backup.zip.001", ".002", ...) read as one file: seekable, read-only.
/// </summary>
internal sealed class PartsStream : Stream
{
    private readonly FileStream[] _parts;
    private readonly long[] _starts;
    private readonly long _length;
    private long _position;

    public PartsStream(IReadOnlyList<string> paths)
    {
        _parts = new FileStream[paths.Count];
        _starts = new long[paths.Count];
        long start = 0;
        try
        {
            for (int i = 0; i < paths.Count; i++)
            {
                _parts[i] = new FileStream(paths[i], FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                _starts[i] = start;
                start += _parts[i].Length;
            }
        }
        catch
        {
            foreach (FileStream? part in _parts)
                part?.Dispose();
            throw;
        }
        _length = start;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, _length);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int total = 0;
        while (buffer.Length > 0 && _position < _length)
        {
            // the part that holds the position
            int index = Array.BinarySearch(_starts, _position);
            if (index < 0)
                index = ~index - 1;
            FileStream part = _parts[index];
            long inPart = _position - _starts[index];
            if (inPart >= part.Length)
            {
                _position = index + 1 < _starts.Length ? _starts[index + 1] : _length; // an empty part
                continue;
            }
            part.Position = inPart;
            int read = part.Read(buffer[..(int)Math.Min(buffer.Length, part.Length - inPart)]);
            if (read <= 0)
                break;
            _position += read;
            total += read;
            buffer = buffer[read..];
        }
        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            foreach (FileStream part in _parts)
                part.Dispose();
        base.Dispose(disposing);
    }
}
