// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Core.Viewing;

/// <summary>What Convert (Ctrl+K) changes: null keeps that part as each file has it.</summary>
/// <param name="ReadAs">The encoding the files are in (null: recognized from each file).</param>
public sealed record ConversionRequest(LineEnding? LineEnding, TextEncoding? Encoding, TextEncoding? ReadAs = null);

public enum ConversionOutcome
{
    Converted,

    /// <summary>Already as asked: not written again.</summary>
    Unchanged,

    /// <summary>Not text (a program, a picture): left alone.</summary>
    NotText,

    /// <summary>Characters the new encoding can't hold: left alone, nothing would be lost silently.</summary>
    Unencodable,

    Failed,
}

/// <summary>
/// Salamander's Convert: line endings (CRLF / LF / CR) and the text encoding of files, in place. Saved like the
/// editor saves (a temporary file replacing the original in one step), files that aren't text or that would lose
/// characters are left as they are.
/// </summary>
public static class TextConversion
{
    public static (ConversionOutcome Outcome, string? Problem) Convert(string path, ConversionRequest request)
    {
        try
        {
            LoadedText loaded = TextDocuments.Load(path, request.ReadAs);
            if (loaded.LooksBinary)
                return (ConversionOutcome.NotText, null);
            TextEncoding encoding = request.Encoding ?? loaded.Encoding;
            string text = request.LineEnding is LineEnding ending ? TextDocuments.WithLineEndings(loaded.Text, ending) : loaded.Text;
            bool sameEncoding = encoding.Encoding.CodePage == loaded.Encoding.Encoding.CodePage && encoding.HasBom == loaded.Encoding.HasBom;
            if (sameEncoding && string.Equals(text, loaded.Text, StringComparison.Ordinal) && request.ReadAs == null)
                return (ConversionOutcome.Unchanged, null);
            int lost = TextDocuments.Unencodable(text, encoding);
            if (lost > 0)
                return (ConversionOutcome.Unencodable, $"{lost} characters can't be stored in {encoding.Name}");
            TextDocuments.Save(path, text, encoding, ending: null);
            return (ConversionOutcome.Converted, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (ConversionOutcome.Failed, ex.Message);
        }
    }
}
