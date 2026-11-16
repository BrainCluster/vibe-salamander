// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Core.FileSystem;

/// <summary>Where a file of a cloud service (OneDrive, iCloud, Dropbox...) keeps its content.</summary>
public enum CloudState
{
    /// <summary>An ordinary file, or a synced file whose content is on this computer.</summary>
    Local,

    /// <summary>Only in the cloud: reading the content downloads it.</summary>
    OnlineOnly,

    /// <summary>"Always keep on this device": the content stays here.</summary>
    AlwaysKept,
}

/// <summary>
/// Files that cloud services keep as placeholders: they are listed with their name, size and date, but their content
/// is downloaded when something reads it (and files on offline storage are recalled from it). A file manager must
/// not download a whole cloud drive because a tool only wanted to look: Find with text, Find Duplicates, comparing
/// content, checksums, thumbnails, quick view and program icons leave such files alone unless the user asks.
/// Opening, viewing, copying and moving a file are explicit, and work as always (Windows downloads it).
/// </summary>
public static class CloudFiles
{
    /// <summary>FILE_ATTRIBUTE_PINNED: the user chose "Always keep on this device".</summary>
    public const FileAttributes Pinned = (FileAttributes)0x00080000;

    /// <summary>FILE_ATTRIBUTE_UNPINNED: the user chose "Free up space" (the content may go at any time).</summary>
    public const FileAttributes Unpinned = (FileAttributes)0x00100000;

    /// <summary>FILE_ATTRIBUTE_RECALL_ON_OPEN: (a folder) its listing isn't fully on this computer.</summary>
    public const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

    /// <summary>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS: the content isn't on this computer; reading it fetches it.</summary>
    public const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>True for a file whose content isn't on this computer (reading it downloads or recalls it).</summary>
    public static bool IsOnlineOnly(FileAttributes attributes) =>
        (attributes & FileAttributes.Directory) == 0 && (attributes & (RecallOnDataAccess | FileAttributes.Offline)) != 0;

    /// <summary>True for a file whose content isn't on this computer (never for directories and the up-dir).</summary>
    public static bool IsOnlineOnly(FileEntry entry) => !entry.IsDirectory && !entry.IsUpDir && IsOnlineOnly(entry.Attributes);

    /// <summary>Where the content of a file with these attributes is.</summary>
    public static CloudState State(FileAttributes attributes) =>
        IsOnlineOnly(attributes) ? CloudState.OnlineOnly
        : (attributes & Pinned) != 0 ? CloudState.AlwaysKept
        : CloudState.Local;
}
