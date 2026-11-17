// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using VibeSalamander.Core.Operations.Elevation;

namespace VibeSalamander.Core.Operations;

/// <summary>What Change Attributes (Ctrl+F2) does to each item.</summary>
public sealed class AttributeChange
{
    /// <summary>Attributes to switch on.</summary>
    public FileAttributes Set { get; init; }

    /// <summary>Attributes to switch off.</summary>
    public FileAttributes Clear { get; init; }

    public DateTime? LastWriteTime { get; init; }
    public DateTime? CreationTime { get; init; }
    public DateTime? LastAccessTime { get; init; }

    /// <summary>Also apply to the contents of selected directories.</summary>
    public bool IncludeSubdirectories { get; init; }

    /// <summary>The attributes this dialog may change (Salamander: R, H, S, A).</summary>
    public const FileAttributes Editable = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive;
}

public enum CaseMode
{
    /// <summary>all lower case</summary>
    Lower,
    /// <summary>ALL UPPER CASE</summary>
    Upper,
    /// <summary>First letter upper, the rest lower ("Readme.txt")</summary>
    Capitalize,
    /// <summary>Each Word Capitalized ("My Holiday Photos.jpg")</summary>
    CapitalizeWords,
}

public enum CasePart
{
    WholeName,
    NameOnly,
    ExtensionOnly,
}

/// <summary>Change Attributes and Change Case, applied to a selection (optionally recursively).</summary>
public static class NameAndAttributeOperations
{
    /// <param name="elevation">Offers "Continue as administrator" where access is denied (null: not offered).</param>
    public static Task<OperationResult> ChangeAttributesAsync(IReadOnlyList<string> paths, AttributeChange change,
        IOperationCallbacks callbacks, CancellationToken ct = default, IElevation? elevation = null) =>
        ChangeAttributesAsync(paths, change, callbacks, new Elevator(elevation), ct);

    /// <summary>The same, with the way to administrator rights given (tests); the helper ends with the operation.</summary>
    internal static Task<OperationResult> ChangeAttributesAsync(IReadOnlyList<string> paths, AttributeChange change,
        IOperationCallbacks callbacks, Elevator elevator, CancellationToken ct) =>
        Task.Run(async () =>
        {
            using Elevator ending = elevator;
            var result = new OperationResult();
            foreach (string path in Expand(paths, change.IncludeSubdirectories))
            {
                ct.ThrowIfCancellationRequested();
                IFileOps ops = elevator.Local;
                while (true)
                {
                    FileAttributes? clearedReadOnly = null;
                    try
                    {
                        FileAttributes attr = File.GetAttributes(path);
                        FileAttributes updated = (attr | change.Set) & ~change.Clear;
                        // timestamps can't be changed on read-only files: set times first with RO cleared
                        if (change.LastWriteTime != null || change.CreationTime != null || change.LastAccessTime != null)
                        {
                            if ((attr & FileAttributes.ReadOnly) != 0)
                            {
                                ops.SetAttributes(path, attr & ~(FileAttributes.ReadOnly | FileAttributes.Directory));
                                clearedReadOnly = attr;
                            }
                            ops.SetTimes(path, (attr & FileAttributes.Directory) != 0, change.LastWriteTime, change.CreationTime, change.LastAccessTime);
                        }
                        ops.SetAttributes(path, updated & ~FileAttributes.Directory);
                        clearedReadOnly = null;
                        result.Succeeded++;
                        break;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        // a time that couldn't be set: the item keeps the read-only attribute it had
                        if (clearedReadOnly is FileAttributes original)
                            TrySetAttributes(ops, path, original & ~FileAttributes.Directory);
                        ErrorChoice choice = await elevator.AskAsync(callbacks.ResolveErrorAsync, path, ex.Message, ex, ops != elevator.Local, ct);
                        if (choice == ErrorChoice.Elevate)
                        {
                            ops = elevator.Ops; // again, as administrator
                            continue;
                        }
                        if (choice == ErrorChoice.Retry) continue;
                        if (choice == ErrorChoice.Cancel) { result.Cancelled = true; return result; }
                        result.Failed++;
                        break;
                    }
                }
            }
            return result;
        }, ct);

    private static void TrySetAttributes(IFileOps ops, string path, FileAttributes attributes)
    {
        try
        {
            ops.SetAttributes(path, attributes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the error is reported for the item anyway
        }
    }

    /// <param name="elevation">Offers "Continue as administrator" where access is denied (null: not offered).</param>
    public static Task<OperationResult> ChangeCaseAsync(IReadOnlyList<string> paths, CaseMode mode, CasePart part,
        bool includeSubdirectories, IOperationCallbacks callbacks, CancellationToken ct = default, IElevation? elevation = null) =>
        ChangeCaseAsync(paths, mode, part, includeSubdirectories, callbacks, new Elevator(elevation), ct);

    /// <summary>The same, with the way to administrator rights given (tests); the helper ends with the operation.</summary>
    internal static Task<OperationResult> ChangeCaseAsync(IReadOnlyList<string> paths, CaseMode mode, CasePart part,
        bool includeSubdirectories, IOperationCallbacks callbacks, Elevator elevator, CancellationToken ct) =>
        Task.Run(async () =>
        {
            using Elevator ending = elevator;
            var result = new OperationResult();
            // deepest items first, so renaming a directory doesn't invalidate the paths of its content
            var items = Expand(paths, includeSubdirectories).OrderByDescending(p => p.Length).ToList();
            foreach (string path in items)
            {
                ct.ThrowIfCancellationRequested();
                string dir = Path.GetDirectoryName(path)!;
                string name = Path.GetFileName(path);
                bool isDirectory = Directory.Exists(path);
                string newName = ApplyCase(name, mode, part, isDirectory);
                if (newName == name)
                    continue;
                IFileOps ops = elevator.Local;
                while (true)
                {
                    try
                    {
                        // only the case differs: Windows renames in place (no temporary name a failure could leave behind)
                        ops.Move(path, Path.Combine(dir, newName), isDirectory);
                        result.Succeeded++;
                        break;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        ErrorChoice choice = await elevator.AskAsync(callbacks.ResolveErrorAsync, path, ex.Message, ex, ops != elevator.Local, ct);
                        if (choice == ErrorChoice.Elevate)
                        {
                            ops = elevator.Ops;
                            continue;
                        }
                        if (choice == ErrorChoice.Retry) continue;
                        if (choice == ErrorChoice.Cancel) { result.Cancelled = true; return result; }
                        result.Failed++;
                        break;
                    }
                }
            }
            return result;
        }, ct);

    /// <summary>Converts the case of a file name (directories have no extension part).</summary>
    public static string ApplyCase(string name, CaseMode mode, CasePart part, bool isDirectory)
    {
        int dot = isDirectory ? -1 : name.LastIndexOf('.');
        string baseName = dot > 0 ? name[..dot] : name;
        string ext = dot > 0 ? name[dot..] : string.Empty; // includes the dot
        return part switch
        {
            CasePart.NameOnly => Convert(baseName, mode) + ext,
            CasePart.ExtensionOnly => baseName + Convert(ext, mode),
            // "Capitalize first letter" treats the whole name as one text ("My file.txt")
            _ => mode == CaseMode.Capitalize ? Convert(name, mode) : Convert(baseName, mode) + Convert(ext, mode),
        };
    }

    private static string Convert(string text, CaseMode mode)
    {
        if (text.Length == 0)
            return text;
        CultureInfo culture = CultureInfo.CurrentCulture;
        switch (mode)
        {
            case CaseMode.Lower:
                return text.ToLower(culture);
            case CaseMode.Upper:
                return text.ToUpper(culture);
            case CaseMode.Capitalize:
            {
                string lower = text.ToLower(culture);
                int first = 0;
                while (first < lower.Length && !char.IsLetter(lower[first]))
                    first++;
                return first < lower.Length ? lower[..first] + char.ToUpper(lower[first], culture) + lower[(first + 1)..] : lower;
            }
            default: // CapitalizeWords
            {
                char[] chars = text.ToLower(culture).ToCharArray();
                bool startOfWord = true;
                for (int i = 0; i < chars.Length; i++)
                {
                    if (char.IsLetterOrDigit(chars[i]))
                    {
                        if (startOfWord)
                            chars[i] = char.ToUpper(chars[i], culture);
                        startOfWord = false;
                    }
                    else
                    {
                        startOfWord = true;
                    }
                }
                return new string(chars);
            }
        }
    }

    /// <summary>The items themselves plus, when requested, everything inside selected directories.</summary>
    private static IEnumerable<string> Expand(IReadOnlyList<string> paths, bool recursive)
    {
        foreach (string path in paths)
        {
            yield return path;
            if (recursive && Directory.Exists(path) && !IsLink(path))
            {
                var children = new List<string>();
                Collect(path, children);
                foreach (string child in children)
                    yield return child;
            }
        }
    }

    /// <summary>
    /// Everything inside a directory, without going into junctions and symbolic links: the folders they point to
    /// are elsewhere and weren't selected (the link itself is included, like any item).
    /// </summary>
    private static void Collect(string directory, List<string> into)
    {
        List<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory, "*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        foreach (string entry in entries)
        {
            into.Add(entry);
            if (Directory.Exists(entry) && !IsLink(entry))
                Collect(entry, into);
        }
    }

    private static bool IsLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 && new DirectoryInfo(path).LinkTarget != null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // unreadable: not gone into
        }
    }
}
