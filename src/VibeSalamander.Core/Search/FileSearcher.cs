// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Enumeration;
using System.Text;
using VibeSalamander.Core.FileSystem;

namespace VibeSalamander.Core.Search;

public sealed class SearchOptions
{
    public required string Directory { get; init; }
    public WildcardMask Mask { get; init; } = new("*.*");
    public bool IncludeSubdirectories { get; init; } = true;
    public bool IncludeDirectories { get; init; } = true;
    public bool IncludeHidden { get; init; } = true;

    /// <summary>Text the file must contain (UTF-8/ANSI or UTF-16 encoded); null = no content search.</summary>
    public string? ContainingText { get; init; }
    public bool MatchCase { get; init; }
}

/// <summary>Find Files and Directories (Alt+F7): name masks, recursion, optional content search.</summary>
public static class FileSearcher
{
    /// <summary>
    /// Searches on a background thread. <paramref name="onFound"/> is called for each match (from the
    /// search thread); <paramref name="currentDirectory"/> reports where the search is.
    /// </summary>
    public static Task<int> SearchAsync(SearchOptions options, Action<FileEntry> onFound,
        IProgress<string>? currentDirectory, CancellationToken ct) =>
        Task.Run(() =>
        {
            int found = 0;
            byte[][]? needles = options.ContainingText is { Length: > 0 } text ? BuildNeedles(text, options.MatchCase) : null;
            var pending = new Stack<string>();
            pending.Push(options.Directory);
            long lastReport = 0;

            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                string dir = pending.Pop();
                long now = Environment.TickCount64;
                if (now - lastReport > 150)
                {
                    currentDirectory?.Report(dir);
                    lastReport = now;
                }

                List<FileEntry> entries;
                try
                {
                    entries = new FileSystemEnumerable<FileEntry>(dir, (ref FileSystemEntry e) => new FileEntry(
                        e.FileName.ToString(), e.ToFullPath(), e.IsDirectory, e.IsDirectory ? 0 : e.Length,
                        e.LastWriteTimeUtc.LocalDateTime, e.Attributes),
                        new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }).ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                var subdirectories = new List<string>();
                foreach (FileEntry entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!options.IncludeHidden && (entry.IsHidden || entry.IsSystem))
                        continue;
                    if (entry.IsDirectory)
                    {
                        if (options.IncludeSubdirectories && (entry.Attributes & FileAttributes.ReparsePoint) == 0)
                            subdirectories.Add(entry.FullPath);
                        if (options.IncludeDirectories && needles == null && options.Mask.IsMatch(entry.Name))
                        {
                            onFound(entry);
                            found++;
                        }
                    }
                    else if (options.Mask.IsMatch(entry.Name) && (needles == null || ContainsAny(entry.FullPath, needles, options.MatchCase, ct)))
                    {
                        onFound(entry);
                        found++;
                    }
                }
                // push in reverse so directories are searched in alphabetical order
                subdirectories.Sort(StringComparer.CurrentCultureIgnoreCase);
                for (int i = subdirectories.Count - 1; i >= 0; i--)
                    pending.Push(subdirectories[i]);
            }
            currentDirectory?.Report(string.Empty);
            return found;
        }, ct);

    /// <summary>Encodings searched: UTF-8 (also covers ASCII/most ANSI text) and UTF-16 LE.</summary>
    private static byte[][] BuildNeedles(string text, bool matchCase)
    {
        string t = matchCase ? text : FoldAscii(text);
        return new[] { Encoding.UTF8.GetBytes(t), Encoding.Unicode.GetBytes(t) };
    }

    private static string FoldAscii(string s)
    {
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (chars[i] is >= 'A' and <= 'Z')
                chars[i] = (char)(chars[i] + 32);
        return new string(chars);
    }

    /// <summary>Streams the file and looks for any of the needles (case folding for ASCII letters).</summary>
    internal static bool ContainsAny(string path, byte[][] needles, bool matchCase, CancellationToken ct)
    {
        int maxNeedle = needles.Max(n => n.Length);
        if (maxNeedle == 0)
            return true;
        const int chunk = 256 * 1024;
        byte[] buffer = new byte[chunk + maxNeedle];
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            int carry = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int read = stream.Read(buffer, carry, chunk);
                if (read <= 0)
                    return false;
                int length = carry + read;
                if (!matchCase)
                    for (int i = carry; i < length; i++)
                        if (buffer[i] is >= (byte)'A' and <= (byte)'Z')
                            buffer[i] += 32;
                ReadOnlySpan<byte> span = buffer.AsSpan(0, length);
                foreach (byte[] needle in needles)
                    if (span.IndexOf(needle) >= 0)
                        return true;
                // keep the tail so matches crossing the chunk boundary are found
                carry = Math.Min(maxNeedle - 1, length);
                Buffer.BlockCopy(buffer, length - carry, buffer, 0, carry);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
