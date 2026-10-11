// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Core.FileSystem;

/// <summary>Drive data attached to the This PC entries (<see cref="FileEntry.Tag"/>).</summary>
public sealed class DriveEntryInfo
{
    public required string Root { get; init; }          // "C:\"
    public required DriveType Type { get; init; }
    public string Label { get; init; } = string.Empty;
    public string FileSystem { get; init; } = string.Empty;
    public string RemoteName { get; init; } = string.Empty; // "\\server\share" for mapped drives
    public bool IsReady { get; init; }
    public long TotalSize { get; init; }
    public long FreeSpace { get; init; }

    public bool HasSize => IsReady && TotalSize > 0;

    /// <summary>Used space in percent (0–100), or -1 when unknown.</summary>
    public int UsedPercent => HasSize ? (int)Math.Clamp(Math.Round(100.0 * (TotalSize - FreeSpace) / TotalSize), 0, 100) : -1;

    /// <summary>"Local Disk (C:)" style caption, like Explorer.</summary>
    public string Caption
    {
        get
        {
            string letter = Root.TrimEnd('\\');
            string label = !string.IsNullOrEmpty(Label) ? Label :
                           !string.IsNullOrEmpty(RemoteName) ? RemoteName : TypeName;
            return $"{label} ({letter})";
        }
    }

    public string TypeName => Localization.L.T(Type switch
    {
        DriveType.Fixed => "Local Disk",
        DriveType.Removable => "Removable Disk",
        DriveType.CDRom => "CD/DVD Drive",
        DriveType.Network => "Network Drive",
        DriveType.Ram => "RAM Disk",
        _ => "Drive",
    });
}

/// <summary>
/// "This PC": all drives with label, type, capacity and fullness. The panel goes here from the ".." of a
/// drive root (as with the Open Salamander This PC plugin).
/// </summary>
public sealed class ThisPcProvider : IFileSystemProvider
{
    public const string RootPath = "thispc:";

    /// <summary>Maximum time to wait for slow drives (disconnected network drives, empty card readers).</summary>
    public static readonly TimeSpan DriveQueryTimeout = TimeSpan.FromMilliseconds(2000);

    public string Id => "thispc";

    public bool IsLocalDisk => false;

    public bool CanHandle(string path) => path.StartsWith(RootPath, StringComparison.OrdinalIgnoreCase);

    public string GetDisplayPath(string path) => Localization.L.T("This PC");

    public ParentLocation? GetParent(string path) => null;

    public async Task<DirectoryListing> ListAsync(string path, ListingOptions options, CancellationToken cancellationToken)
    {
        IReadOnlyList<DriveEntryInfo> drives = await QueryDrivesAsync(cancellationToken).ConfigureAwait(false);
        var entries = drives.Select(d => new FileEntry(
            d.Root.TrimEnd('\\'), d.Root, isDirectory: true, d.TotalSize, DateTime.MinValue,
            FileAttributes.Directory, tag: d)).ToList();
        return new DirectoryListing(RootPath, entries);
    }

    /// <summary>Queries every drive in parallel; drives that don't answer in time are returned without sizes.</summary>
    public static async Task<IReadOnlyList<DriveEntryInfo>> QueryDrivesAsync(CancellationToken cancellationToken = default)
    {
        string[] roots = Environment.GetLogicalDrives();
        var tasks = roots.Select(root => (root, task: Task.Run(() => QueryDrive(root), cancellationToken))).ToList();

        Task all = Task.WhenAll(tasks.Select(t => t.task));
        await Task.WhenAny(all, Task.Delay(DriveQueryTimeout, cancellationToken)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var result = new List<DriveEntryInfo>(tasks.Count);
        foreach (var (root, task) in tasks)
        {
            if (task.IsCompletedSuccessfully)
                result.Add(task.Result);
            else
                result.Add(new DriveEntryInfo { Root = root, Type = SafeDriveType(root) });
        }
        return result;
    }

    private static DriveType SafeDriveType(string root)
    {
        try { return new DriveInfo(root).DriveType; }
        catch { return DriveType.Unknown; }
    }

    private static DriveEntryInfo QueryDrive(string root)
    {
        var drive = new DriveInfo(root);
        DriveType type = drive.DriveType;
        string remote = type == DriveType.Network ? Interop.NativeMethods.GetRemoteName(root) ?? string.Empty : string.Empty;
        try
        {
            if (drive.IsReady)
            {
                return new DriveEntryInfo
                {
                    Root = root,
                    Type = type,
                    Label = drive.VolumeLabel,
                    FileSystem = drive.DriveFormat,
                    RemoteName = remote,
                    IsReady = true,
                    TotalSize = drive.TotalSize,
                    FreeSpace = drive.AvailableFreeSpace,
                };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // not ready (no media, disconnected)
        }
        return new DriveEntryInfo { Root = root, Type = type, RemoteName = remote };
    }
}
