// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.Interop;

namespace VibeSalamander.Core.FileSystem;

/// <summary>Paths as other computers reach them.</summary>
public static class NetworkPaths
{
    /// <summary>
    /// A path on a mapped network drive in its network form ("Z:\Reports\q3.xlsx" → "\\server\share\Reports\q3.xlsx"),
    /// for pasting into a mail or a chat; any other path as it is.
    /// </summary>
    public static string ToUnc(string path) => ToUnc(path, NativeMethods.GetRemoteName);

    /// <param name="remoteOf">The network name of a drive root ("Z:\" → "\\server\share"), or null: not mapped.</param>
    public static string ToUnc(string path, Func<string, string?> remoteOf)
    {
        if (path.Length < 2 || path[1] != ':' || !char.IsAsciiLetter(path[0]))
            return path; // already a network path, or not a drive path at all
        string? remote = remoteOf(char.ToUpperInvariant(path[0]) + @":\");
        if (string.IsNullOrEmpty(remote))
            return path;
        string rest = path.Length > 2 ? path[2..].TrimStart('\\') : string.Empty;
        return rest.Length == 0 ? remote.TrimEnd('\\') : remote.TrimEnd('\\') + "\\" + rest;
    }
}
