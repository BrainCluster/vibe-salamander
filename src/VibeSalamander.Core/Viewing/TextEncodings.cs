// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;

namespace VibeSalamander.Core.Viewing;

/// <summary>An encoding a text file can be read (and saved) in, with its byte order mark if it has one.</summary>
/// <param name="Name">For the user: "UTF-8", "UTF-8 with BOM", "Windows-1250 (Central European)".</param>
/// <param name="BomLength">Bytes of the byte order mark at the start of the file (0 = none).</param>
public sealed record TextEncoding(string Name, Encoding Encoding, int BomLength)
{
    /// <summary>Bytes per code unit: 2 for UTF-16, 4 for UTF-32, else 1 (lines end at "\n" in that unit).</summary>
    public int UnitSize => Encoding switch
    {
        UnicodeEncoding => 2,
        UTF32Encoding => 4,
        _ => 1,
    };

    public bool IsBigEndian => Encoding.CodePage is 1201 or 12001;

    public bool HasBom => BomLength > 0;
}

/// <summary>Recognizing a file's encoding, and the encodings offered to choose from.</summary>
public static class TextEncodings
{
    static TextEncodings()
    {
        // Windows code pages (1250, 852, ...) aren't in .NET by default
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static Encoding Strict(Encoding e) => Encoding.GetEncoding(e.CodePage, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);

    public static readonly TextEncoding Utf8 = new("UTF-8", new UTF8Encoding(false), 0);
    public static readonly TextEncoding Utf8Bom = new("UTF-8 with BOM", new UTF8Encoding(true), 3);
    public static readonly TextEncoding Utf16Le = new("UTF-16 LE", new UnicodeEncoding(false, true), 2);
    public static readonly TextEncoding Utf16Be = new("UTF-16 BE", new UnicodeEncoding(true, true), 2);
    public static readonly TextEncoding Utf32Le = new("UTF-32 LE", new UTF32Encoding(false, true), 4);

    /// <summary>The system's ANSI code page (Windows-1252 in Western Europe, 1250 in Central Europe, ...).</summary>
    public static TextEncoding Ansi => CodePage(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);

    /// <summary>The system's OEM code page (the console's: 437, 852, ...).</summary>
    public static TextEncoding Oem => CodePage(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);

    /// <summary>A single-byte or multi-byte Windows code page.</summary>
    public static TextEncoding CodePage(int codePage)
    {
        _ = Utf8; // the static constructor has registered the code pages
        Encoding encoding = Encoding.GetEncoding(codePage);
        return new TextEncoding($"{encoding.WebName} ({codePage})", Strict(encoding), 0);
    }

    /// <summary>The encodings the viewer and the editor offer (the detected one comes first separately).</summary>
    public static IReadOnlyList<TextEncoding> Choices()
    {
        var list = new List<TextEncoding> { Utf8, Utf8Bom, Utf16Le, Utf16Be, Ansi, Oem };
        foreach (int cp in new[] { 1250, 1251, 1252, 1253, 1254, 1257, 28591, 28592, 852, 437, 866, 932, 936, 949, 950, 20866 })
            if (!list.Any(e => e.Encoding.CodePage == cp && e.BomLength == 0))
                list.Add(CodePage(cp));
        return list;
    }

    /// <summary>
    /// The encoding of a file from its first bytes: a byte order mark, UTF-16 without one (many zero bytes on one
    /// side), valid UTF-8, else the system's ANSI code page.
    /// </summary>
    public static TextEncoding Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            return Utf8Bom;
        if (head.Length >= 4 && head[0] == 0xFF && head[1] == 0xFE && head[2] == 0 && head[3] == 0)
            return Utf32Le;
        if (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE)
            return Utf16Le;
        if (head.Length >= 2 && head[0] == 0xFE && head[1] == 0xFF)
            return Utf16Be;

        // UTF-16 without a BOM: text in Latin letters has a zero in every other byte
        if (head.Length >= 4)
        {
            int pairs = head.Length / 2, zeroEven = 0, zeroOdd = 0;
            for (int i = 0; i + 1 < head.Length; i += 2)
            {
                if (head[i] == 0) zeroEven++;
                if (head[i + 1] == 0) zeroOdd++;
            }
            if (zeroOdd > pairs * 0.4 && zeroEven < pairs * 0.05)
                return Utf16Le with { BomLength = 0 };
            if (zeroEven > pairs * 0.4 && zeroOdd < pairs * 0.05)
                return Utf16Be with { BomLength = 0 };
        }
        return IsUtf8(head) ? Utf8 : Ansi;
    }

    /// <summary>True when the bytes are valid UTF-8 (a sequence cut off at the end counts as valid).</summary>
    public static bool IsUtf8(ReadOnlySpan<byte> bytes)
    {
        int i = 0;
        while (i < bytes.Length)
        {
            byte b = bytes[i];
            int more = b < 0x80 ? 0 : (b & 0xE0) == 0xC0 ? 1 : (b & 0xF0) == 0xE0 ? 2 : (b & 0xF8) == 0xF0 ? 3 : -1;
            if (more < 0 || (more == 1 && b < 0xC2))
                return false;
            for (int k = 1; k <= more; k++)
            {
                if (i + k >= bytes.Length)
                    return true; // cut off by the sample's end
                if ((bytes[i + k] & 0xC0) != 0x80)
                    return false;
            }
            i += more + 1;
        }
        return true;
    }

    /// <summary>
    /// True when the bytes look like a binary file rather than text: zero bytes (in a file that isn't UTF-16) or many
    /// control characters.
    /// </summary>
    public static bool LooksBinary(ReadOnlySpan<byte> head, TextEncoding encoding)
    {
        if (head.Length == 0 || encoding.UnitSize > 1)
            return false;
        int control = 0;
        foreach (byte b in head)
        {
            if (b == 0)
                return true;
            if (b < 0x20 && b is not (byte)'\t' and not (byte)'\n' and not (byte)'\r' and not 0x0C and not 0x1B)
                control++;
        }
        return control > head.Length / 20;
    }
}
