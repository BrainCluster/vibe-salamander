// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Formats.Tar;
using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace VibeSalamander.Plugins.Archive;

/// <summary>The result of testing one file of an archive.</summary>
/// <param name="Problem">null = the file unpacks and its checksum matches.</param>
internal sealed record TestedFile(string Name, long Size, string? Problem);

/// <summary>
/// Test Archive (Salamander's "Test archive"): every file is unpacked in memory (nothing is written) and its
/// CRC-32 compared with the one the archive stored. Damaged data, wrong passwords and broken headers show up
/// as problems of the files they hit.
/// </summary>
internal static class ArchiveTest
{
    /// <summary>Thrown when the archive needs a password (none given, or it was wrong for the names).</summary>
    internal sealed class PasswordNeeded : Exception
    {
    }

    /// <summary>
    /// Tests every file. <paramref name="tested"/> gets each result as it comes; <paramref name="progress"/>
    /// the share done (0..1). Throws <see cref="PasswordNeeded"/> when the files are encrypted and there is no
    /// password, and InvalidDataException when the archive can't be read at all.
    /// </summary>
    public static void Run(string path, string kind, string? password, Action<TestedFile> tested, Action<double> progress,
        CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case "tar" or "tar.gz" or "tgz" or "tar.bz2" or "tbz" or "tbz2" or "tar.xz" or "txz" or "tar.lz":
                RunTar(path, kind, tested, progress, cancellationToken);
                break;
            case "gz" or "bz2" or "xz" or "lz":
                RunSingle(path, kind, tested, progress, cancellationToken);
                break;
            default:
                RunArchive(path, password, tested, progress, cancellationToken);
                break;
        }
    }

    private static bool LooksLikePassword(Exception ex) =>
        ex is System.Security.Cryptography.CryptographicException or SharpCompress.Common.CryptographicException ||
        ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase);

    /// <summary>ZIP, 7-Zip, RAR (SharpCompress).</summary>
    private static void RunArchive(string path, string? password, Action<TestedFile> tested, Action<double> progress, CancellationToken ct)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        IArchive archive;
        List<IArchiveEntry> entries;
        try
        {
            archive = ArchiveFactory.OpenArchive(file, new ReaderOptions { Password = password, LeaveStreamOpen = true });
            entries = archive.Entries.ToList();
        }
        catch (Exception ex) when (LooksLikePassword(ex))
        {
            throw new PasswordNeeded(); // 7-Zip with encrypted names
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
        using (archive)
        {
            var files = entries.Where(e => !e.IsDirectory).ToList();
            if (password == null && files.Any(e => e.IsEncrypted))
                throw new PasswordNeeded();
            long total = Math.Max(1, files.Sum(e => Math.Max(0, e.Size)));
            long done = 0;
            byte[] buffer = new byte[1 << 16];

            void Test(IEntry entry, Func<Stream> open)
            {
                ct.ThrowIfCancellationRequested();
                string? problem = null;
                long read = 0;
                try
                {
                    var crc = new System.IO.Hashing.Crc32();
                    using (Stream data = open())
                    {
                        int n;
                        while ((n = data.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            crc.Append(buffer.AsSpan(0, n));
                            read += n;
                            progress(Math.Min(1, (done + read) / (double)total));
                        }
                    }
                    // the stored CRC (0 when the format keeps none, as with WinZip AES AE-2)
                    if (entry.Crc != 0 && (uint)entry.Crc != crc.GetCurrentHashAsUInt32())
                        problem = entry.IsEncrypted ? "Wrong password, or the data is damaged (CRC error)" : "Damaged (CRC error)";
                    else if (entry.Size > 0 && read != entry.Size)
                        problem = "Damaged (the size doesn't match)";
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    problem = entry.IsEncrypted && LooksLikePassword(ex) ? "Wrong password" : ex.Message;
                }
                done += Math.Max(0, entry.Size);
                tested(new TestedFile(entry.Key ?? string.Empty, Math.Max(0, entry.Size), problem));
            }

            if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
            {
                // solid archives unpack in one pass
                using IReader reader = archive.ExtractAllEntries();
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    bool next;
                    try
                    {
                        next = reader.MoveToNextEntry();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        tested(new TestedFile(string.Empty, 0, ex.Message));
                        break;
                    }
                    if (!next)
                        break;
                    if (!reader.Entry.IsDirectory)
                        Test(reader.Entry, reader.OpenEntryStream);
                }
            }
            else
            {
                foreach (IArchiveEntry entry in files)
                    Test(entry, entry.OpenEntryStream);
            }
        }
    }

    /// <summary>TAR, also compressed: every file read to its end (TAR keeps no checksums of the data).</summary>
    private static void RunTar(string path, string kind, Action<TestedFile> tested, Action<double> progress, CancellationToken ct)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        long length = Math.Max(1, file.Length);
        using Stream data = Archiver.OpenTarData(kind, file);
        using var reader = new TarReader(data, leaveOpen: true);
        byte[] buffer = new byte[1 << 16];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            TarEntry? entry;
            try
            {
                entry = reader.GetNextEntry(copyData: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                tested(new TestedFile(string.Empty, 0, "Damaged: " + ex.Message));
                return;
            }
            if (entry == null)
                return;
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile))
                continue;
            string? problem = null;
            long read = 0;
            try
            {
                if (entry.DataStream is { } stream)
                {
                    int n;
                    while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        read += n;
                    }
                }
                if (read != entry.Length)
                    problem = "Damaged (the file ends too early)";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                problem = ex.Message;
            }
            progress(Math.Min(1, Archiver.SafePosition(file) / (double)length));
            tested(new TestedFile(entry.Name, entry.Length, problem));
        }
    }

    /// <summary>One compressed file (notes.txt.gz): unpacked to its end.</summary>
    private static void RunSingle(string path, string kind, Action<TestedFile> tested, Action<double> progress, CancellationToken ct)
    {
        string name = Path.GetFileName(path)[..^(kind.Length + 1)];
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        long length = Math.Max(1, file.Length);
        string? problem = null;
        long read = 0;
        try
        {
            using Stream data = kind switch
            {
                "gz" => new GZipStream(file, CompressionMode.Decompress, leaveOpen: true),
                "bz2" => SharpCompress.Compressors.BZip2.BZip2Stream.Create(file, SharpCompress.Compressors.CompressionMode.Decompress, decompressConcatenated: true),
                "xz" => new SharpCompress.Compressors.Xz.XZStream(file),
                _ => SharpCompress.Compressors.LZMA.LZipStream.Create(file, SharpCompress.Compressors.CompressionMode.Decompress),
            };
            byte[] buffer = new byte[1 << 16];
            int n;
            while ((n = data.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                read += n;
                progress(Math.Min(1, Archiver.SafePosition(file) / (double)length));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            problem = "Damaged: " + ex.Message;
        }
        tested(new TestedFile(name, read, problem));
    }
}
