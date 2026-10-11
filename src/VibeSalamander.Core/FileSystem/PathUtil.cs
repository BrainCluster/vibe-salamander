// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Core.FileSystem;

public static class PathUtil
{
    /// <summary>"c:" → "C:\", "C:\dir\" → "C:\dir", "\\srv\share" stays.</summary>
    public static string NormalizeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;
        path = path.Trim();
        if (path.Length == 2 && path[1] == ':')
            return char.ToUpperInvariant(path[0]) + @":\";
        if (path.Length == 3 && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))
            return char.ToUpperInvariant(path[0]) + @":\";
        path = path.Replace('/', '\\');
        if (path.Length > 3 && path.EndsWith('\\'))
            path = path.TrimEnd('\\');
        if (path.Length >= 2 && path[1] == ':')
            path = char.ToUpperInvariant(path[0]) + path[1..];
        return path;
    }

    public static bool IsDriveRoot(string path) =>
        path.Length == 3 && path[1] == ':' && path[2] == '\\';

    /// <summary>Root of a path: "C:\" or "\\server\share\".</summary>
    public static string? GetRoot(string path)
    {
        try
        {
            return Path.GetPathRoot(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Resolves what the user typed in a "change directory" box relative to the current path.</summary>
    public static string Resolve(string currentDirectory, string input)
    {
        input = Environment.ExpandEnvironmentVariables(input.Trim().Trim('"'));
        if (input.Length == 2 && input[1] == ':')
            return NormalizeDirectory(input);
        if (Path.IsPathRooted(input) && !(input.StartsWith('\\') && !input.StartsWith(@"\\")))
            return NormalizeDirectory(Path.GetFullPath(input));
        if (input.StartsWith('\\') && !input.StartsWith(@"\\")) // "\dir" is relative to the current root
            return NormalizeDirectory(Path.GetFullPath(Path.Combine(GetRoot(currentDirectory) ?? currentDirectory, input.TrimStart('\\'))));
        return NormalizeDirectory(Path.GetFullPath(Path.Combine(currentDirectory, input)));
    }
}
