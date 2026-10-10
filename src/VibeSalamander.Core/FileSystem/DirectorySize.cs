using System.IO.Enumeration;

namespace VibeSalamander.Core.FileSystem;

/// <summary>Result of <see cref="DirectorySize.CalculateAsync"/>.</summary>
public readonly record struct OccupiedSpace(long Bytes, long Files, long Directories)
{
    public static OccupiedSpace operator +(OccupiedSpace a, OccupiedSpace b) =>
        new(a.Bytes + b.Bytes, a.Files + b.Files, a.Directories + b.Directories);
}

/// <summary>Calculate Occupied Space (Alt+F10) / directory sizes: sums a directory tree.</summary>
public static class DirectorySize
{
    /// <summary>
    /// Everything inside <paramref name="directory"/> (not counting the directory itself). Junctions and
    /// symbolic links are not followed, so a tree is never counted twice or endlessly.
    /// </summary>
    public static Task<OccupiedSpace> CalculateAsync(string directory, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            long bytes = 0, files = 0, dirs = 0;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = 0, // hidden and system files occupy space too
                ReturnSpecialDirectories = false,
            };
            // reparse points are counted but never entered: skipping them altogether would also drop
            // files that are reparse points themselves (OneDrive placeholders, deduplicated files)
            var entries = new FileSystemEnumerable<(long Length, bool IsDirectory)>(directory,
                (ref FileSystemEntry e) => (e.Length, e.IsDirectory), options)
            {
                ShouldRecursePredicate = (ref FileSystemEntry e) => (e.Attributes & FileAttributes.ReparsePoint) == 0,
            };
            int n = 0;
            foreach (var (length, isDirectory) in entries)
            {
                if ((++n & 1023) == 0)
                    ct.ThrowIfCancellationRequested();
                if (isDirectory)
                    dirs++;
                else
                {
                    files++;
                    bytes += length;
                }
            }
            return new OccupiedSpace(bytes, files, dirs);
        }, ct);
}
