// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.Interop;

namespace VibeSalamander.Core.Commands;

/// <summary>What was right-clicked.</summary>
public enum ContextMenuTarget
{
    /// <summary>A drive in This PC.</summary>
    Drive,

    /// <summary>A file or directory on a server (FTP, SFTP).</summary>
    RemoteItem,

    /// <summary>A file or directory inside an archive shown as a folder.</summary>
    ArchiveItem,

    /// <summary>A file or directory on a disk.</summary>
    Item,

    /// <summary>The empty area or ".." of a folder on a disk: the folder itself.</summary>
    LocalFolder,

    /// <summary>The empty area or ".." anywhere else (This PC, a server, an archive).</summary>
    OtherFolder,
}

/// <summary>Everything the context menu depends on, found out by the panel.</summary>
public sealed record ContextMenuFacts
{
    public ContextMenuTarget Target { get; init; }
    public bool IsDirectory { get; init; }

    /// <summary>How many items are marked (the menu then works on them).</summary>
    public int MarkedCount { get; init; }

    /// <summary>An archive a plugin opens (Enter browses it).</summary>
    public bool IsArchiveFile { get; init; }

    /// <summary>A disk image (.iso, .vhdx, ...) that can be mounted.</summary>
    public bool IsDiskImage { get; init; }

    /// <summary>Configuration: Enter on a disk image mounts it (Mount comes first then).</summary>
    public bool MountDiskImages { get; init; }

    /// <summary>A script that can run in a console (one file, on a disk).</summary>
    public ScriptHost Script { get; init; }

    /// <summary>A plugin can unpack the file.</summary>
    public bool CanUnpack { get; init; }

    /// <summary>The archive's plugin has commands for it (its submenu, "Archives ▸").</summary>
    public bool HasArchivePlugin { get; init; }

    /// <summary>Inside an archive that can be changed (ZIP, TAR): move out and delete.</summary>
    public bool EditableArchive { get; init; }

    /// <summary>The drive is a mounted disk image.</summary>
    public bool IsMountedImageDrive { get; init; }

    /// <summary>The folder is on a mounted disk image: its drive ("E:"), else null.</summary>
    public string? MountedImageDrive { get; init; }

    /// <summary>The panel shows a server.</summary>
    public bool IsRemotePanel { get; init; }

    /// <summary>The Windows context menu can be offered (files and drives on disk).</summary>
    public bool OffersWindowsMenu { get; init; }
}

public enum ContextMenuEntryKind
{
    Command,
    Separator,

    /// <summary>View ▸ (Brief, Detailed, ...).</summary>
    ViewSubmenu,

    /// <summary>Sort by ▸.</summary>
    SortSubmenu,

    /// <summary>The archive plugin's submenu ("Archives ▸", "7-Zip ▸").</summary>
    ArchivePluginSubmenu,

    /// <summary>Run the script ("Run with PowerShell" / "Run in cmd").</summary>
    RunScript,

    /// <summary>"Windows context menu" (opens Explorer's menu).</summary>
    WindowsMenu,
}

/// <param name="Text">English (translated when the menu is shown).</param>
/// <param name="Glyph">A Segoe Fluent Icons character.</param>
/// <param name="Bold">The default action (what Enter or a double-click does).</param>
public sealed record ContextMenuEntry(ContextMenuEntryKind Kind, string Text = "", string Glyph = "", CommandId Command = CommandId.None, bool Bold = false)
{
    public static readonly ContextMenuEntry Separator = new(ContextMenuEntryKind.Separator);
}

/// <summary>
/// The Vibe Salamander context menu: the most used commands for what was right-clicked, and "Windows context menu"
/// at the bottom for everything else. Only what the menu holds; the panel shows it.
/// </summary>
public static class ContextMenuPlan
{
    public static IReadOnlyList<ContextMenuEntry> For(ContextMenuFacts f)
    {
        var menu = new List<ContextMenuEntry>();
        void Add(string text, string glyph, CommandId command, bool bold = false) =>
            menu.Add(new ContextMenuEntry(ContextMenuEntryKind.Command, text, glyph, command, bold));
        void Separator()
        {
            if (menu.Count > 0 && menu[^1].Kind != ContextMenuEntryKind.Separator)
                menu.Add(ContextMenuEntry.Separator);
        }
        void Other(ContextMenuEntryKind kind) => menu.Add(new ContextMenuEntry(kind));

        switch (f.Target)
        {
            case ContextMenuTarget.Drive:
                Add("Open", "", CommandId.Open, bold: true);
                Add("Open in other panel", "", CommandId.OpenInOtherPanel);
                if (f.IsMountedImageDrive)
                    Add("Unmount", "", CommandId.UnmountImage);
                Separator();
                Add("Drive information", "", CommandId.DriveInformation);
                Add("Properties", "", CommandId.Properties);
                break;

            case ContextMenuTarget.RemoteItem:
                // on a server: copies go through the transfer panel
                Add("Open", f.IsDirectory ? "" : "", CommandId.Open, bold: true);
                if (!f.IsDirectory)
                {
                    Add("View", "", CommandId.View);
                    Add("Edit", "", CommandId.Edit);
                }
                Separator();
                Add("Copy to other panel...", "", CommandId.Copy);
                Add("Move to other panel...", "", CommandId.Move);
                Add("Rename", "", CommandId.QuickRename);
                Add("Delete", "", CommandId.Delete);
                Separator();
                Add("Copy name as text", "", CommandId.CopyNameAsText);
                Add("Copy path", "", CommandId.CopyFullNameAsText);
                break;

            case ContextMenuTarget.ArchiveItem:
                // inside an archive: ZIP and TAR also move out and delete, the others are read-only
                Add("Open", f.IsDirectory ? "" : "", CommandId.Open, bold: true);
                Separator();
                Add("Copy out to other panel...", "", CommandId.Copy);
                if (f.EditableArchive)
                    Add("Move out to other panel...", "", CommandId.Move);
                Add("Copy name as text", "", CommandId.CopyNameAsText);
                if (f.EditableArchive)
                {
                    Separator();
                    Add("Delete", "", CommandId.Delete);
                }
                break;

            case ContextMenuTarget.Item:
                ItemCommands(f, Add, Separator, Other);
                break;

            case ContextMenuTarget.LocalFolder:
                // the directory itself (empty area or "..")
                Add("New folder", "", CommandId.CreateDirectory);
                Add("New text document", "", CommandId.EditNewFile);
                Add("New (Windows)...", "", CommandId.NewMenu);
                Separator();
                Add("Paste", "", CommandId.ClipboardPaste);
                Add("Paste shortcut", "", CommandId.PasteShortcut);
                Separator();
                Other(ContextMenuEntryKind.ViewSubmenu);
                Other(ContextMenuEntryKind.SortSubmenu);
                Add("Refresh", "", CommandId.Refresh);
                Add("Select all", "", CommandId.SelectAll);
                Separator();
                Add("Open in Explorer", "", CommandId.OpenActiveFolder);
                Add("Command prompt here", "", CommandId.CommandShell);
                if (f.MountedImageDrive is { } drive)
                {
                    Separator();
                    Add($"Unmount {drive.ToUpperInvariant()}", "", CommandId.UnmountImage);
                }
                break;

            default:
                if (f.IsRemotePanel)
                {
                    Add("New folder", "", CommandId.CreateDirectory);
                    Add("Paste", "", CommandId.ClipboardPaste);
                    Separator();
                }
                Other(ContextMenuEntryKind.ViewSubmenu);
                Other(ContextMenuEntryKind.SortSubmenu);
                Add("Refresh", "", CommandId.Refresh);
                break;
        }

        if (f.OffersWindowsMenu)
        {
            Separator();
            Other(ContextMenuEntryKind.WindowsMenu);
        }
        return menu;
    }

    /// <summary>A file or directory on a disk (or the marked items).</summary>
    private static void ItemCommands(ContextMenuFacts f, Action<string, string, CommandId, bool> add, Action separator, Action<ContextMenuEntryKind> other)
    {
        void Add(string text, string glyph, CommandId command, bool bold = false) => add(text, glyph, command, bold);
        int count = f.MarkedCount;
        bool several = count > 1;
        bool directory = f.IsDirectory;
        bool archive = !directory && f.IsArchiveFile;
        bool image = !directory && !archive && f.IsDiskImage;

        if (image && f.MountDiskImages)
            Add("Mount", "", CommandId.MountImage, bold: true);
        else
            Add(archive ? "Open archive" : "Open", directory || archive ? "" : "", CommandId.Open, bold: true);
        if (image && !f.MountDiskImages)
            Add("Mount", "", CommandId.MountImage);
        if (directory || archive)
            Add("Open in other panel", "", CommandId.OpenInOtherPanel);
        if (!directory && !several)
        {
            Add("Open with...", "", CommandId.OpenWith);
            Add("Edit", "", CommandId.Edit);
        }
        if (!directory && !several && f.Script != ScriptHost.None)
            other(ContextMenuEntryKind.RunScript); // in a console window that stays open
        separator();
        Add("Cut", "", CommandId.ClipboardCut);
        Add("Copy", "", CommandId.ClipboardCopy);
        Add("Paste", "", CommandId.ClipboardPaste);
        separator();
        Add(several ? $"Copy {count} items to other panel..." : "Copy to other panel...", "", CommandId.Copy);
        Add(several ? $"Move {count} items to other panel..." : "Move to other panel...", "", CommandId.Move);
        if (!several)
            Add("Rename", "", CommandId.QuickRename);
        Add(several ? $"Delete {count} items" : "Delete", "", CommandId.Delete);
        separator();
        Add(several ? "Pack into archive..." : "Pack...", "", CommandId.Pack);
        if (f.CanUnpack)
            Add("Unpack...", "", CommandId.Unpack);
        if (!several && archive && f.HasArchivePlugin)
            other(ContextMenuEntryKind.ArchivePluginSubmenu);
        Add("Copy path", "", CommandId.CopyFullNameAsText);
        separator();
        Add("Properties", "", CommandId.Properties);
    }
}
