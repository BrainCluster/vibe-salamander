// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;

namespace VibeSalamander.Plugins.Checksum;

/// <summary>The checksums the plugin calculates, in Open Salamander's order.</summary>
public enum HashKind
{
    Crc,
    Md5,
    Sha1,
    Sha256,
    Sha512,
}

/// <summary>Names, file extensions and sizes of the checksums.</summary>
public static class HashKinds
{
    public static readonly HashKind[] All = { HashKind.Crc, HashKind.Md5, HashKind.Sha1, HashKind.Sha256, HashKind.Sha512 };

    /// <summary>Column title and option label ("CRC", "SHA-256").</summary>
    public static string ColumnName(HashKind kind) => kind switch
    {
        HashKind.Crc => "CRC",
        HashKind.Md5 => "MD5",
        HashKind.Sha1 => "SHA-1",
        HashKind.Sha256 => "SHA-256",
        _ => "SHA-512",
    };

    /// <summary>Extension of its checksum file, with the dot.</summary>
    public static string Extension(HashKind kind) => kind switch
    {
        HashKind.Crc => ".sfv",
        HashKind.Md5 => ".md5",
        HashKind.Sha1 => ".sha1",
        HashKind.Sha256 => ".sha256",
        _ => ".sha512",
    };

    /// <summary>Save dialog type name ("SFV files").</summary>
    public static string FileTypeName(HashKind kind) => kind switch
    {
        HashKind.Crc => "SFV files",
        HashKind.Md5 => "MD5 files",
        HashKind.Sha1 => "SHA-1 files",
        HashKind.Sha256 => "SHA-256 files",
        _ => "SHA-512 files",
    };

    /// <summary>Length of the checksum in hex digits.</summary>
    public static int HexLength(HashKind kind) => kind switch
    {
        HashKind.Crc => 8,
        HashKind.Md5 => 32,
        HashKind.Sha1 => 40,
        HashKind.Sha256 => 64,
        _ => 128,
    };

    /// <summary>The name in "MD5 (file) = hash" lines (BSD / OpenSSL style).</summary>
    public static string Id(HashKind kind) => kind switch
    {
        HashKind.Md5 => "MD5",
        HashKind.Sha1 => "SHA1",
        HashKind.Sha256 => "SHA256",
        HashKind.Sha512 => "SHA512",
        _ => "CRC32",
    };

    /// <summary>Column width in pixels in the plugin windows.</summary>
    public static double ColumnWidth(HashKind kind) => kind switch
    {
        HashKind.Crc => 90,
        HashKind.Md5 => 270,
        HashKind.Sha1 => 330,
        HashKind.Sha256 => 520,
        _ => 1030,
    };
}

/// <summary>CRC-32 (IEEE 802.3, the one SFV files use).</summary>
public sealed class Crc32
{
    private static readonly uint[] Table = BuildTable();
    private uint _crc = 0xFFFFFFFF;

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        uint crc = _crc;
        foreach (byte b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        _crc = crc;
    }

    public uint Value => _crc ^ 0xFFFFFFFF;
}

/// <summary>Several checksums of one file at once (the file is read only once).</summary>
public sealed class MultiHasher : IDisposable
{
    private readonly Crc32? _crc;
    private readonly Dictionary<HashKind, IncrementalHash> _hashes = new();

    public MultiHasher(IEnumerable<HashKind> kinds)
    {
        foreach (HashKind kind in kinds.Distinct())
        {
            switch (kind)
            {
                case HashKind.Crc: _crc = new Crc32(); break;
                case HashKind.Md5: _hashes[kind] = IncrementalHash.CreateHash(HashAlgorithmName.MD5); break;
                case HashKind.Sha1: _hashes[kind] = IncrementalHash.CreateHash(HashAlgorithmName.SHA1); break;
                case HashKind.Sha256: _hashes[kind] = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); break;
                case HashKind.Sha512: _hashes[kind] = IncrementalHash.CreateHash(HashAlgorithmName.SHA512); break;
            }
        }
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        _crc?.Append(data);
        foreach (IncrementalHash hash in _hashes.Values)
            hash.AppendData(data);
    }

    /// <summary>The results as lower-case hex ("cbf43926").</summary>
    public Dictionary<HashKind, string> Finish()
    {
        var result = new Dictionary<HashKind, string>();
        if (_crc != null)
            result[HashKind.Crc] = _crc.Value.ToString("x8");
        foreach (var (kind, hash) in _hashes)
            result[kind] = Convert.ToHexStringLower(hash.GetHashAndReset());
        return result;
    }

    /// <summary>Reads the file and calculates; <paramref name="progress"/> gets the bytes read so far.</summary>
    public static Dictionary<HashKind, string> HashFile(string path, IEnumerable<HashKind> kinds, Action<long>? progress,
        CancellationToken cancellationToken)
    {
        using var hasher = new MultiHasher(kinds);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            1, FileOptions.SequentialScan);
        byte[] buffer = new byte[1024 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hasher.Append(buffer.AsSpan(0, read));
            progress?.Invoke(read);
        }
        return hasher.Finish();
    }

    public void Dispose()
    {
        foreach (IncrementalHash hash in _hashes.Values)
            hash.Dispose();
    }
}
