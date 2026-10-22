// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace VibeSalamander.Plugins.Archive;

// ZIP writing for packing and for changing archives in place: .NET's ZipArchive can neither encrypt nor
// copy entries without unpacking them. Written from the ZIP specification (PKWARE APPNOTE 6.3) and the
// WinZip AES specification (AE-1, AES-256); ZIP64 is used when sizes, offsets or counts need it.

/// <summary>One entry of a ZIP's central directory.</summary>
internal sealed class ZipRecord
{
    public ushort VersionMadeBy;
    public ushort VersionNeeded;
    public ushort Flags;
    public ushort Method;
    public ushort Time;
    public ushort Date;
    public uint Crc;
    public long CompressedSize;
    public long UncompressedSize;
    public long LocalHeaderOffset;
    public ushort InternalAttributes;
    public uint ExternalAttributes;
    public byte[] NameBytes = Array.Empty<byte>();

    /// <summary>Extra fields without the ZIP64 one (it is written again as needed).</summary>
    public byte[] Extra = Array.Empty<byte>();

    public byte[] Comment = Array.Empty<byte>();

    public bool IsEncrypted => (Flags & 1) != 0;

    public bool HasDataDescriptor => (Flags & 8) != 0;

    public string Name => Zip.DecodeName(NameBytes, Flags);

    public bool IsDirectory
    {
        get
        {
            string name = Name;
            return name.EndsWith('/') || name.EndsWith('\\') || ((ExternalAttributes & 0x10) != 0 && (VersionMadeBy >> 8) == 0 && UncompressedSize == 0);
        }
    }

    public DateTime LastWrite => Zip.FromDosTime(Date, Time);

    public ZipRecord Clone() => (ZipRecord)MemberwiseClone();
}

internal static class Zip
{
    private const uint LocalSignature = 0x04034b50;
    private const uint CentralSignature = 0x02014b50;
    private const uint EndSignature = 0x06054b50;
    private const uint Zip64EndSignature = 0x06064b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const uint DescriptorSignature = 0x08074b50;
    public const ushort AesMethod = 99;
    public const ushort AesExtraId = 0x9901;

    static Zip() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>UTF-8 when the entry says so, else the DOS code page (IBM 437) like most ZIP tools.</summary>
    public static string DecodeName(byte[] bytes, ushort flags) =>
        (flags & 0x800) != 0 ? Encoding.UTF8.GetString(bytes) : Encoding.GetEncoding(437).GetString(bytes);

    public static DateTime FromDosTime(ushort date, ushort time)
    {
        try
        {
            return new DateTime(1980 + (date >> 9), Math.Max(1, (date >> 5) & 15), Math.Max(1, date & 31),
                time >> 11, (time >> 5) & 63, Math.Min(59, (time & 31) * 2), DateTimeKind.Local);
        }
        catch (ArgumentOutOfRangeException)
        {
            return new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
        }
    }

    public static (ushort Date, ushort Time) ToDosTime(DateTime time)
    {
        if (time.Year < 1980) time = new DateTime(1980, 1, 1);
        if (time.Year > 2107) time = new DateTime(2107, 12, 31, 23, 59, 58);
        return ((ushort)(((time.Year - 1980) << 9) | (time.Month << 5) | time.Day),
                (ushort)((time.Hour << 11) | (time.Minute << 5) | (time.Second / 2)));
    }

    // ---- the archive comment ----------------------------------------------------------------------

    /// <summary>The comment as text: UTF-8 when it is valid UTF-8, else the DOS code page (IBM 437).</summary>
    public static string DecodeComment(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).Replace("\r\n", "\n");
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(437).GetString(bytes).Replace("\r\n", "\n");
        }
    }

    /// <summary>Text as a comment: Windows line ends; plain ASCII, or UTF-8 when it needs more.</summary>
    public static byte[] EncodeComment(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        if (bytes.Length > 0xFFFF)
            throw new InvalidDataException("A ZIP comment can have at most 65,535 bytes.");
        return bytes;
    }

    /// <summary>Where the end record (the last part of a ZIP, which holds the comment) starts.</summary>
    public static long FindEndRecord(Stream s)
    {
        long length = s.Length;
        int tail = (int)Math.Min(length, 22 + 65535);
        byte[] buffer = new byte[tail];
        s.Position = length - tail;
        s.ReadExactly(buffer);
        for (int i = tail - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i)) == EndSignature &&
                i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(i + 20)) <= tail)
                return length - tail + i;
        }
        throw new InvalidDataException("This is not a ZIP archive, or it is damaged.");
    }

    /// <summary>Replaces the archive's comment in place (the end record is the last part of the file).</summary>
    public static void WriteComment(string path, string text)
    {
        byte[] comment = EncodeComment(text);
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        long end = FindEndRecord(file);
        byte[] length = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(length, (ushort)comment.Length);
        file.Position = end + 20;
        file.Write(length);
        file.Write(comment);
        file.SetLength(end + 22 + comment.Length);
    }

    // ---- reading the central directory ------------------------------------------------------------

    /// <summary>The entries of a ZIP archive and its comment. Throws InvalidDataException for damaged or split archives.</summary>
    public static (List<ZipRecord> Records, byte[] Comment) ReadDirectory(Stream s)
    {
        long length = s.Length;
        int tail = (int)Math.Min(length, 22 + 65535);
        byte[] buffer = new byte[tail];
        s.Position = length - tail;
        s.ReadExactly(buffer);
        int end = -1;
        for (int i = tail - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i)) == EndSignature &&
                i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(i + 20)) <= tail)
            {
                end = i;
                break;
            }
        }
        if (end < 0)
            throw new InvalidDataException("This is not a ZIP archive, or it is damaged.");
        ReadOnlySpan<byte> e = buffer.AsSpan(end);
        if (BinaryPrimitives.ReadUInt16LittleEndian(e[4..]) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(e[6..]) != 0)
            throw new InvalidDataException("Archives split into several files can't be changed.");
        long count = BinaryPrimitives.ReadUInt16LittleEndian(e[10..]);
        long size = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
        long offset = BinaryPrimitives.ReadUInt32LittleEndian(e[16..]);
        byte[] comment = e.Slice(22, BinaryPrimitives.ReadUInt16LittleEndian(e[20..])).ToArray();
        long endPosition = length - tail + end;

        if (count == 0xFFFF || size == 0xFFFFFFFF || offset == 0xFFFFFFFF)
        {
            // ZIP64: the locator is right before the end record
            if (endPosition >= 20)
            {
                byte[] locator = new byte[20];
                s.Position = endPosition - 20;
                s.ReadExactly(locator);
                if (BinaryPrimitives.ReadUInt32LittleEndian(locator) == Zip64LocatorSignature)
                {
                    byte[] record = new byte[56];
                    s.Position = (long)BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
                    s.ReadExactly(record);
                    if (BinaryPrimitives.ReadUInt32LittleEndian(record) != Zip64EndSignature)
                        throw new InvalidDataException("This ZIP archive is damaged (ZIP64 end of central directory).");
                    count = (long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(32));
                    size = (long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(40));
                    offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(48));
                }
            }
        }
        if (offset < 0 || size < 0 || offset + size > length || size > int.MaxValue)
            throw new InvalidDataException("This ZIP archive is damaged (central directory).");

        byte[] directory = new byte[size];
        s.Position = offset;
        s.ReadExactly(directory);
        var records = new List<ZipRecord>((int)Math.Min(count, 100_000));
        int p = 0;
        for (long n = 0; n < count; n++)
        {
            if (p + 46 > directory.Length || BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(p)) != CentralSignature)
                throw new InvalidDataException("This ZIP archive is damaged (central directory).");
            ReadOnlySpan<byte> h = directory.AsSpan(p);
            var r = new ZipRecord
            {
                VersionMadeBy = BinaryPrimitives.ReadUInt16LittleEndian(h[4..]),
                VersionNeeded = BinaryPrimitives.ReadUInt16LittleEndian(h[6..]),
                Flags = BinaryPrimitives.ReadUInt16LittleEndian(h[8..]),
                Method = BinaryPrimitives.ReadUInt16LittleEndian(h[10..]),
                Time = BinaryPrimitives.ReadUInt16LittleEndian(h[12..]),
                Date = BinaryPrimitives.ReadUInt16LittleEndian(h[14..]),
                Crc = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]),
                CompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(h[20..]),
                UncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(h[24..]),
                InternalAttributes = BinaryPrimitives.ReadUInt16LittleEndian(h[36..]),
                ExternalAttributes = BinaryPrimitives.ReadUInt32LittleEndian(h[38..]),
                LocalHeaderOffset = BinaryPrimitives.ReadUInt32LittleEndian(h[42..]),
            };
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(h[28..]);
            int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(h[30..]);
            int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(h[32..]);
            if (p + 46 + nameLength + extraLength + commentLength > directory.Length)
                throw new InvalidDataException("This ZIP archive is damaged (central directory).");
            r.NameBytes = directory.AsSpan(p + 46, nameLength).ToArray();
            r.Extra = ReadExtra(directory.AsSpan(p + 46 + nameLength, extraLength), r);
            r.Comment = directory.AsSpan(p + 46 + nameLength + extraLength, commentLength).ToArray();
            records.Add(r);
            p += 46 + nameLength + extraLength + commentLength;
        }
        return (records, comment);
    }

    /// <summary>Takes the ZIP64 values out of the extra fields into the record; returns the other fields.</summary>
    private static byte[] ReadExtra(ReadOnlySpan<byte> extra, ZipRecord r)
    {
        using var kept = new MemoryStream();
        int p = 0;
        while (p + 4 <= extra.Length)
        {
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(extra[p..]);
            int size = BinaryPrimitives.ReadUInt16LittleEndian(extra[(p + 2)..]);
            if (p + 4 + size > extra.Length)
                break; // a broken last field: dropped
            ReadOnlySpan<byte> data = extra.Slice(p + 4, size);
            if (id == 0x0001)
            {
                int q = 0;
                if (r.UncompressedSize == 0xFFFFFFFF && q + 8 <= size) { r.UncompressedSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(data[q..]); q += 8; }
                if (r.CompressedSize == 0xFFFFFFFF && q + 8 <= size) { r.CompressedSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(data[q..]); q += 8; }
                if (r.LocalHeaderOffset == 0xFFFFFFFF && q + 8 <= size) { r.LocalHeaderOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(data[q..]); }
            }
            else
            {
                kept.Write(extra.Slice(p, 4 + size));
            }
            p += 4 + size;
        }
        return kept.ToArray();
    }

    /// <summary>Where the entry's local header, data and data descriptor end (to copy it as it is).</summary>
    public static long EntryEnd(Stream s, ZipRecord r)
    {
        byte[] header = new byte[30];
        s.Position = r.LocalHeaderOffset;
        s.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != LocalSignature)
            throw new InvalidDataException($"This ZIP archive is damaged (\"{r.Name}\").");
        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26));
        int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
        long dataEnd = r.LocalHeaderOffset + 30 + nameLength + extraLength + r.CompressedSize;
        ushort localFlags = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
        if ((localFlags & 8) == 0 && !r.HasDataDescriptor)
            return dataEnd;
        // data descriptor: optional signature, CRC, sizes (8 bytes each with ZIP64)
        bool zip64 = r.CompressedSize >= 0xFFFFFFFF || r.UncompressedSize >= 0xFFFFFFFF || HasZip64LocalExtra(s, r.LocalHeaderOffset + 30 + nameLength, extraLength);
        byte[] signature = new byte[4];
        s.Position = dataEnd;
        int read = s.ReadAtLeast(signature, 4, throwOnEndOfStream: false);
        bool hasSignature = read == 4 && BinaryPrimitives.ReadUInt32LittleEndian(signature) == DescriptorSignature;
        return dataEnd + (hasSignature ? 4 : 0) + 4 + (zip64 ? 16 : 8);
    }

    private static bool HasZip64LocalExtra(Stream s, long position, int length)
    {
        byte[] extra = new byte[length];
        s.Position = position;
        s.ReadExactly(extra);
        for (int p = 0; p + 4 <= length;)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(p)) == 0x0001)
                return true;
            p += 4 + BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(p + 2));
        }
        return false;
    }

    /// <summary>
    /// Checks a password against an AES-encrypted entry (its 2-byte password verifier). Null when the entry
    /// can't tell (not AES): then any password is taken.
    /// </summary>
    public static bool? CheckPassword(Stream s, ZipRecord r, string password)
    {
        if (!r.IsEncrypted || r.Method != AesMethod)
            return null;
        byte[] header = new byte[30];
        s.Position = r.LocalHeaderOffset;
        s.ReadExactly(header);
        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26));
        int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
        byte[] extra = new byte[extraLength];
        s.Position = r.LocalHeaderOffset + 30 + nameLength;
        s.ReadExactly(extra);
        int strength = 0;
        for (int p = 0; p + 4 <= extraLength;)
        {
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(p));
            int size = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(p + 2));
            if (id == AesExtraId && size >= 7 && p + 4 + size <= extraLength)
                strength = extra[p + 4 + 4];
            p += 4 + size;
        }
        int keyLength = strength switch { 1 => 16, 2 => 24, 3 => 32, _ => 0 };
        if (keyLength == 0)
            return null;
        byte[] saltAndVerifier = new byte[keyLength / 2 + 2];
        s.ReadExactly(saltAndVerifier); // the data starts right after the extra field
        byte[] salt = saltAndVerifier[..(keyLength / 2)];
        byte[] derived = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 1000, HashAlgorithmName.SHA1, 2 * keyLength + 2);
        return derived[2 * keyLength] == saltAndVerifier[^2] && derived[2 * keyLength + 1] == saltAndVerifier[^1];
    }
}

/// <summary>Writes a ZIP archive into a seekable stream: new entries, and entries copied from another ZIP as they are.</summary>
internal sealed class ZipBuilder
{
    private readonly Stream _out;
    private readonly List<ZipRecord> _records = new();
    private readonly byte[] _buffer = new byte[1 << 20];

    /// <summary>Above this size an entry gets ZIP64 sizes from the start (deflate may grow incompressible data a little).</summary>
    private const long Zip64Threshold = 0xF0000000;

    public ZipBuilder(Stream output) => _out = output;

    public int Count => _records.Count;

    private static bool NeedsUtf8(string name)
    {
        foreach (char c in name)
            if (c > 0x7E || c < 0x20)
                return true;
        return false;
    }

    /// <summary>Copies an entry of another ZIP (compressed and possibly encrypted) without unpacking it.</summary>
    public void CopyRaw(Stream source, ZipRecord record, Action<int>? progress = null)
    {
        long start = record.LocalHeaderOffset;
        long end = Zip.EntryEnd(source, record);
        ZipRecord copy = record.Clone();
        copy.LocalHeaderOffset = _out.Position;
        source.Position = start;
        long left = end - start;
        while (left > 0)
        {
            int read = source.Read(_buffer, 0, (int)Math.Min(_buffer.Length, left));
            if (read <= 0)
                throw new InvalidDataException($"This ZIP archive is damaged (\"{record.Name}\").");
            _out.Write(_buffer, 0, read);
            left -= read;
            progress?.Invoke(read);
        }
        _records.Add(copy);
    }

    /// <summary>A directory entry ("docs/images/").</summary>
    public void AddDirectory(string name, DateTime lastWrite)
    {
        if (!name.EndsWith('/'))
            name += "/";
        bool utf8 = NeedsUtf8(name);
        byte[] nameBytes = utf8 ? Encoding.UTF8.GetBytes(name) : Encoding.ASCII.GetBytes(name);
        (ushort date, ushort time) = Zip.ToDosTime(lastWrite);
        var r = new ZipRecord
        {
            VersionMadeBy = 63,
            VersionNeeded = 20,
            Flags = (ushort)(utf8 ? 0x800 : 0),
            Method = 0,
            Date = date,
            Time = time,
            ExternalAttributes = 0x10,
            NameBytes = nameBytes,
            LocalHeaderOffset = _out.Position,
        };
        WriteLocalHeader(r, Array.Empty<byte>(), zip64: false);
        _records.Add(r);
    }

    /// <summary>
    /// A file entry from a stream. <paramref name="level"/> null = stored (no compression).
    /// <paramref name="password"/>: encrypted with AES-256 (WinZip AE-1).
    /// </summary>
    public void AddFile(string name, Stream source, long expectedLength, DateTime lastWrite, CompressionLevel? level, string? password,
        Action<int>? progress = null, Action? checkpoint = null)
    {
        bool utf8 = NeedsUtf8(name);
        byte[] nameBytes = utf8 ? Encoding.UTF8.GetBytes(name) : Encoding.ASCII.GetBytes(name);
        bool zip64 = expectedLength >= Zip64Threshold;
        bool aes = password != null;
        ushort method = (ushort)(level == null ? 0 : 8);
        (ushort date, ushort time) = Zip.ToDosTime(lastWrite);
        byte[] aesExtra = aes ? AesExtra(method) : Array.Empty<byte>();
        var r = new ZipRecord
        {
            VersionMadeBy = 63,
            VersionNeeded = (ushort)(aes ? 51 : zip64 ? 45 : 20),
            Flags = (ushort)((utf8 ? 0x800 : 0) | (aes ? 1 : 0)),
            Method = aes ? Zip.AesMethod : method,
            Date = date,
            Time = time,
            ExternalAttributes = 0x20,
            NameBytes = nameBytes,
            Extra = aesExtra,
            LocalHeaderOffset = _out.Position,
        };
        WriteLocalHeader(r, aesExtra, zip64);
        long dataStart = _out.Position;

        var crc = new System.IO.Hashing.Crc32();
        long total = 0;
        var sink = new NonClosingStream(_out);
        WinZipAesEncryptStream? encryption = aes ? new WinZipAesEncryptStream(sink, password!) : null;
        Stream target = (Stream?)encryption ?? sink;
        Stream compressor = level is CompressionLevel l ? new DeflateStream(target, l, leaveOpen: true) : target;
        int read;
        while ((read = source.Read(_buffer, 0, _buffer.Length)) > 0)
        {
            checkpoint?.Invoke();
            crc.Append(_buffer.AsSpan(0, read));
            compressor.Write(_buffer, 0, read);
            total += read;
            progress?.Invoke(read);
        }
        if (compressor != target)
            compressor.Dispose(); // the rest of the deflate data
        encryption?.Dispose(); // the authentication code

        long compressed = _out.Position - dataStart;
        if (!zip64 && (compressed >= 0xFFFFFFFF || total >= 0xFFFFFFFF))
            throw new IOException($"\"{name}\" grew over 4 GB while it was being packed.");
        r.Crc = crc.GetCurrentHashAsUInt32();
        r.CompressedSize = compressed;
        r.UncompressedSize = total;
        if (zip64)
            r.VersionNeeded = Math.Max(r.VersionNeeded, (ushort)45);

        // the sizes and the CRC go into the local header now that they are known
        long end = _out.Position;
        Span<byte> patch = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(patch, r.Crc);
        BinaryPrimitives.WriteUInt32LittleEndian(patch[4..], zip64 ? 0xFFFFFFFF : (uint)compressed);
        BinaryPrimitives.WriteUInt32LittleEndian(patch[8..], zip64 ? 0xFFFFFFFF : (uint)total);
        _out.Position = r.LocalHeaderOffset + 14;
        _out.Write(patch);
        if (zip64)
        {
            Span<byte> sizes = stackalloc byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(sizes, (ulong)total);
            BinaryPrimitives.WriteUInt64LittleEndian(sizes[8..], (ulong)compressed);
            _out.Position = r.LocalHeaderOffset + 30 + nameBytes.Length + 4;
            _out.Write(sizes);
        }
        _out.Position = end;
        _records.Add(r);
    }

    /// <summary>WinZip AES extra field: AE-1, "AE", AES-256, the real compression method.</summary>
    private static byte[] AesExtra(ushort method)
    {
        byte[] extra = new byte[11];
        BinaryPrimitives.WriteUInt16LittleEndian(extra, Zip.AesExtraId);
        BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(2), 7);
        BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(4), 1); // AE-1: the CRC is stored too
        extra[6] = (byte)'A';
        extra[7] = (byte)'E';
        extra[8] = 3; // AES-256
        BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(9), method);
        return extra;
    }

    private void WriteLocalHeader(ZipRecord r, byte[] extra, bool zip64)
    {
        int extraLength = extra.Length + (zip64 ? 20 : 0);
        byte[] header = new byte[30 + r.NameBytes.Length + extraLength];
        Span<byte> h = header;
        BinaryPrimitives.WriteUInt32LittleEndian(h, 0x04034b50);
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], zip64 ? Math.Max(r.VersionNeeded, (ushort)45) : r.VersionNeeded);
        BinaryPrimitives.WriteUInt16LittleEndian(h[6..], r.Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(h[8..], r.Method);
        BinaryPrimitives.WriteUInt16LittleEndian(h[10..], r.Time);
        BinaryPrimitives.WriteUInt16LittleEndian(h[12..], r.Date);
        // CRC and sizes (14..25): patched after the data; 0xFFFFFFFF with ZIP64
        if (zip64)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h[18..], 0xFFFFFFFF);
            BinaryPrimitives.WriteUInt32LittleEndian(h[22..], 0xFFFFFFFF);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(h[26..], (ushort)r.NameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(h[28..], (ushort)extraLength);
        r.NameBytes.CopyTo(h[30..]);
        int p = 30 + r.NameBytes.Length;
        if (zip64)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(h[p..], 0x0001);
            BinaryPrimitives.WriteUInt16LittleEndian(h[(p + 2)..], 16);
            p += 20; // the sizes are patched later
        }
        extra.CopyTo(h[p..]);
        _out.Write(header);
    }

    /// <summary>Writes the central directory and the end records.</summary>
    public void Finish(byte[]? comment = null)
    {
        comment ??= Array.Empty<byte>();
        if (comment.Length > 0xFFFF)
            comment = comment[..0xFFFF];
        long directoryStart = _out.Position;
        using (var directory = new BufferedStream(new NonClosingStream(_out), 1 << 16))
        {
            foreach (ZipRecord r in _records)
                WriteCentralRecord(directory, r);
        }
        long directoryEnd = _out.Position;
        long directorySize = directoryEnd - directoryStart;
        bool zip64 = _records.Count >= 0xFFFF || directoryStart >= 0xFFFFFFFF || directorySize >= 0xFFFFFFFF;
        if (zip64)
        {
            byte[] end64 = new byte[56 + 20];
            Span<byte> e = end64;
            BinaryPrimitives.WriteUInt32LittleEndian(e, 0x06064b50);
            BinaryPrimitives.WriteUInt64LittleEndian(e[4..], 44);
            BinaryPrimitives.WriteUInt16LittleEndian(e[12..], 45);
            BinaryPrimitives.WriteUInt16LittleEndian(e[14..], 45);
            BinaryPrimitives.WriteUInt64LittleEndian(e[24..], (ulong)_records.Count);
            BinaryPrimitives.WriteUInt64LittleEndian(e[32..], (ulong)_records.Count);
            BinaryPrimitives.WriteUInt64LittleEndian(e[40..], (ulong)directorySize);
            BinaryPrimitives.WriteUInt64LittleEndian(e[48..], (ulong)directoryStart);
            BinaryPrimitives.WriteUInt32LittleEndian(e[56..], 0x07064b50);
            BinaryPrimitives.WriteUInt64LittleEndian(e[64..], (ulong)directoryEnd);
            BinaryPrimitives.WriteUInt32LittleEndian(e[72..], 1);
            _out.Write(end64);
        }
        byte[] end = new byte[22 + comment.Length];
        Span<byte> d = end;
        BinaryPrimitives.WriteUInt32LittleEndian(d, 0x06054b50);
        ushort count16 = (ushort)Math.Min(_records.Count, 0xFFFF);
        BinaryPrimitives.WriteUInt16LittleEndian(d[8..], count16);
        BinaryPrimitives.WriteUInt16LittleEndian(d[10..], count16);
        BinaryPrimitives.WriteUInt32LittleEndian(d[12..], (uint)Math.Min(directorySize, 0xFFFFFFFF));
        BinaryPrimitives.WriteUInt32LittleEndian(d[16..], (uint)Math.Min(directoryStart, 0xFFFFFFFF));
        BinaryPrimitives.WriteUInt16LittleEndian(d[20..], (ushort)comment.Length);
        comment.CopyTo(d[22..]);
        _out.Write(end);
        _out.Flush();
    }

    private static void WriteCentralRecord(Stream output, ZipRecord r)
    {
        bool bigUncompressed = r.UncompressedSize >= 0xFFFFFFFF;
        bool bigCompressed = r.CompressedSize >= 0xFFFFFFFF;
        bool bigOffset = r.LocalHeaderOffset >= 0xFFFFFFFF;
        int zip64Length = (bigUncompressed ? 8 : 0) + (bigCompressed ? 8 : 0) + (bigOffset ? 8 : 0);
        int extraLength = r.Extra.Length + (zip64Length > 0 ? 4 + zip64Length : 0);
        if (extraLength > 0xFFFF)
        {
            r.Extra = Array.Empty<byte>(); // can't happen with sane archives; the ZIP64 field matters more
            extraLength = zip64Length > 0 ? 4 + zip64Length : 0;
        }
        byte[] header = new byte[46 + r.NameBytes.Length + extraLength + r.Comment.Length];
        Span<byte> h = header;
        BinaryPrimitives.WriteUInt32LittleEndian(h, 0x02014b50);
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], r.VersionMadeBy);
        BinaryPrimitives.WriteUInt16LittleEndian(h[6..], zip64Length > 0 ? Math.Max(r.VersionNeeded, (ushort)45) : r.VersionNeeded);
        BinaryPrimitives.WriteUInt16LittleEndian(h[8..], r.Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(h[10..], r.Method);
        BinaryPrimitives.WriteUInt16LittleEndian(h[12..], r.Time);
        BinaryPrimitives.WriteUInt16LittleEndian(h[14..], r.Date);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], r.Crc);
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], bigCompressed ? 0xFFFFFFFF : (uint)r.CompressedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], bigUncompressed ? 0xFFFFFFFF : (uint)r.UncompressedSize);
        BinaryPrimitives.WriteUInt16LittleEndian(h[28..], (ushort)r.NameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(h[30..], (ushort)extraLength);
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], (ushort)r.Comment.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(h[36..], r.InternalAttributes);
        BinaryPrimitives.WriteUInt32LittleEndian(h[38..], r.ExternalAttributes);
        BinaryPrimitives.WriteUInt32LittleEndian(h[42..], bigOffset ? 0xFFFFFFFF : (uint)r.LocalHeaderOffset);
        r.NameBytes.CopyTo(h[46..]);
        int p = 46 + r.NameBytes.Length;
        if (zip64Length > 0)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(h[p..], 0x0001);
            BinaryPrimitives.WriteUInt16LittleEndian(h[(p + 2)..], (ushort)zip64Length);
            p += 4;
            if (bigUncompressed) { BinaryPrimitives.WriteUInt64LittleEndian(h[p..], (ulong)r.UncompressedSize); p += 8; }
            if (bigCompressed) { BinaryPrimitives.WriteUInt64LittleEndian(h[p..], (ulong)r.CompressedSize); p += 8; }
            if (bigOffset) { BinaryPrimitives.WriteUInt64LittleEndian(h[p..], (ulong)r.LocalHeaderOffset); p += 8; }
        }
        r.Extra.CopyTo(h[p..]);
        p += r.Extra.Length;
        r.Comment.CopyTo(h[p..]);
        output.Write(header);
    }
}

/// <summary>
/// WinZip AES encryption of an entry's data (AES-256 in CTR mode with a little-endian counter, keys from
/// PBKDF2-HMAC-SHA1 with 1000 rounds, HMAC-SHA1 authentication code). Writes the salt and the password
/// verifier first and the 10-byte authentication code when disposed; the inner stream stays open.
/// </summary>
internal sealed class WinZipAesEncryptStream : Stream
{
    private const int KeyLength = 32;
    private const int BlocksPerBatch = 4096;

    private readonly Stream _inner;
    private readonly Aes _aes;
    private readonly IncrementalHash _hmac;
    private readonly byte[] _counters = new byte[BlocksPerBatch * 16];
    private readonly byte[] _keystream = new byte[BlocksPerBatch * 16];
    private readonly byte[] _output = new byte[BlocksPerBatch * 16];
    private int _keystreamPosition = BlocksPerBatch * 16;
    private ulong _counter = 1;
    private bool _finished;

    public WinZipAesEncryptStream(Stream inner, string password)
    {
        _inner = inner;
        byte[] salt = RandomNumberGenerator.GetBytes(KeyLength / 2);
        byte[] derived = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 1000, HashAlgorithmName.SHA1, 2 * KeyLength + 2);
        _aes = Aes.Create();
        _aes.Key = derived[..KeyLength];
        _hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, derived[KeyLength..(2 * KeyLength)]);
        _inner.Write(salt);
        _inner.Write(derived, 2 * KeyLength, 2); // password verifier
        CryptographicOperations.ZeroMemory(derived);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            if (_keystreamPosition == _keystream.Length)
                NextKeystream();
            int n = Math.Min(buffer.Length, _keystream.Length - _keystreamPosition);
            for (int i = 0; i < n; i++)
                _output[i] = (byte)(buffer[i] ^ _keystream[_keystreamPosition + i]);
            _keystreamPosition += n;
            _hmac.AppendData(_output, 0, n);
            _inner.Write(_output, 0, n);
            buffer = buffer[n..];
        }
    }

    /// <summary>The next counter blocks (1, 2, 3, ... as 128-bit little-endian numbers), encrypted in one call.</summary>
    private void NextKeystream()
    {
        Array.Clear(_counters);
        for (int b = 0; b < BlocksPerBatch; b++)
            BinaryPrimitives.WriteUInt64LittleEndian(_counters.AsSpan(b * 16), _counter++);
        _aes.EncryptEcb(_counters, _keystream, PaddingMode.None);
        _keystreamPosition = 0;
    }

    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_finished)
        {
            _finished = true;
            byte[] mac = _hmac.GetHashAndReset();
            _inner.Write(mac, 0, 10);
            _hmac.Dispose();
            _aes.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Writes through to a stream that must stay open (compressors close what they write to).</summary>
internal sealed class NonClosingStream : Stream
{
    private readonly Stream _inner;

    public NonClosingStream(Stream inner) => _inner = inner;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
