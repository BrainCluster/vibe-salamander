// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;

namespace VibeSalamander.Core.Viewing;

public enum LineEnding
{
    /// <summary>Windows: "\r\n".</summary>
    CrLf,

    /// <summary>Unix, macOS: "\n".</summary>
    Lf,

    /// <summary>Classic Mac OS: "\r".</summary>
    Cr,
}

/// <summary>A text file loaded for editing: its text (without the byte order mark), encoding and line endings.</summary>
public sealed record LoadedText(string Text, TextEncoding Encoding, LineEnding LineEnding, bool MixedLineEndings, bool LooksBinary);

/// <summary>Loading and saving text files for the editor.</summary>
public static class TextDocuments
{
    /// <summary>The largest file the editor opens (the viewer shows any size).</summary>
    public const long MaxEditableBytes = 48L * 1024 * 1024;

    public static string Text(LineEnding ending) => ending switch
    {
        LineEnding.Lf => "\n",
        LineEnding.Cr => "\r",
        _ => "\r\n",
    };

    /// <summary>The line ending most lines use (CRLF for text without any); mixed when there are several kinds.</summary>
    public static (LineEnding Ending, bool Mixed) DetectLineEnding(string text)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                {
                    cr++;
                }
            }
            else if (text[i] == '\n')
            {
                lf++;
            }
        }
        int kinds = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        LineEnding ending = crlf >= lf && crlf >= cr ? LineEnding.CrLf : lf >= cr ? LineEnding.Lf : LineEnding.Cr;
        return (ending, kinds > 1);
    }

    /// <summary>All line breaks made <paramref name="ending"/>.</summary>
    public static string WithLineEndings(string text, LineEnding ending)
    {
        string newline = Text(ending);
        var sb = new StringBuilder(text.Length + text.Length / 40);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                sb.Append(newline);
            }
            else if (c == '\n')
            {
                sb.Append(newline);
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>Reads a file for editing (in the given encoding, or the one recognized from its bytes).</summary>
    public static LoadedText Load(string path, TextEncoding? encoding = null)
    {
        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (stream.Length > MaxEditableBytes)
                throw new IOException($"The file is larger than {MaxEditableBytes / (1024 * 1024)} MB: open it in the viewer (F3).");
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        ReadOnlySpan<byte> head = bytes.AsSpan(0, Math.Min(bytes.Length, 64 * 1024));
        TextEncoding detected = encoding ?? TextEncodings.Detect(head);
        // a chosen encoding with a BOM in the file: the BOM isn't text
        int bom = encoding == null ? detected.BomLength : StartsWithPreamble(bytes, encoding.Encoding);
        if (encoding != null && bom > 0)
            detected = encoding with { BomLength = bom };
        string text = detected.Encoding.GetString(bytes, bom, bytes.Length - bom);
        var (ending, mixed) = DetectLineEnding(text);
        return new LoadedText(text, detected, ending, mixed, TextEncodings.LooksBinary(head, detected));
    }

    private static int StartsWithPreamble(byte[] bytes, Encoding encoding)
    {
        ReadOnlySpan<byte> preamble = encoding.Preamble;
        return preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble) ? preamble.Length : 0;
    }

    /// <summary>The bytes the text is saved as: the byte order mark (when the encoding has one) and the text.</summary>
    public static byte[] Encode(string text, TextEncoding encoding)
    {
        byte[] body = encoding.Encoding.GetBytes(text);
        if (!encoding.HasBom)
            return body;
        byte[] preamble = encoding.Encoding.GetPreamble();
        if (preamble.Length == 0)
            preamble = new UTF8Encoding(true).GetPreamble();
        byte[] all = new byte[preamble.Length + body.Length];
        preamble.CopyTo(all, 0);
        body.CopyTo(all, preamble.Length);
        return all;
    }

    /// <summary>
    /// Characters the encoding can't store (they would be saved as "?"), counted; 0 when everything fits.
    /// </summary>
    public static int Unencodable(string text, TextEncoding encoding)
    {
        _ = TextEncodings.Utf8; // the code pages are registered
        Encoding strict = Encoding.GetEncoding(encoding.Encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
        try
        {
            strict.GetByteCount(text);
            return 0;
        }
        catch (EncoderFallbackException)
        {
            // which characters: one at a time (only when something doesn't fit; at most 1000 are counted)
            int count = 0;
            for (int i = 0; i < text.Length && count < 1000; i++)
            {
                int length = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
                try
                {
                    strict.GetByteCount(text.AsSpan(i, length));
                }
                catch (EncoderFallbackException)
                {
                    count++;
                }
                i += length - 1;
            }
            return Math.Max(1, count);
        }
    }

    /// <summary>
    /// Saves safely: the new content goes into a temporary file next to the original, which then replaces it in one
    /// step (the file's attributes and permissions stay). A crash or a full disk leaves the original as it was.
    /// With <paramref name="backup"/> the previous version is kept as "name.ext.bak".
    /// </summary>
    public static void Save(string path, string text, TextEncoding encoding, LineEnding ending, bool backup = false)
    {
        byte[] bytes = Encode(WithLineEndings(text, ending), encoding);
        string directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        string temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid().ToString("N")[..8]}.saving");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                    throw new UnauthorizedAccessException($"\"{Path.GetFileName(path)}\" is read-only.");
                string? backupPath = backup ? path + ".bak" : null;
                if (backupPath != null && File.Exists(backupPath))
                {
                    File.SetAttributes(backupPath, FileAttributes.Normal);
                    File.Delete(backupPath);
                }
                // hidden and system files are replaced like others, and keep those attributes
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    File.SetAttributes(path, attributes & ~(FileAttributes.Hidden | FileAttributes.System));
                try
                {
                    File.Replace(temp, path, backupPath, ignoreMetadataErrors: true);
                }
                finally
                {
                    if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 && File.Exists(path))
                        File.SetAttributes(path, File.GetAttributes(path) | (attributes & (FileAttributes.Hidden | FileAttributes.System)));
                }
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                    // the temporary file stays; it's hidden by its dot and harmless
                }
            }
        }
    }
}
