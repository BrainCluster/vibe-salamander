// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;

namespace VibeSalamander.Core.Commands;

/// <summary>Names, groups and toolbar icons of the commands (keyboard and toolbar settings).</summary>
public static class CommandCatalog
{
    /// <summary>Toolbar item id for the "View" drop-down (not a command).</summary>
    public const string ViewMenuId = "ViewMenu";

    /// <summary>Toolbar item id for a separator.</summary>
    public const string SeparatorId = "-";

    private static readonly Dictionary<CommandId, string> Names = new()
    {
        [CommandId.ChangeDriveLeft] = "Change drive (left panel)",
        [CommandId.ChangeDriveRight] = "Change drive (right panel)",
        [CommandId.GoBack] = "Back",
        [CommandId.GoForward] = "Forward",
        [CommandId.PathFromOtherPanel] = "Path from other panel",
        [CommandId.OpenInOtherPanel] = "Open focused item in other panel",
        [CommandId.ThisPc] = "This PC",
        [CommandId.SwitchPanel] = "Switch to the other panel",
        [CommandId.MaximizePanel] = "Maximize / restore panel",
        [CommandId.ToggleHiddenFiles] = "Show hidden and system files",
        [CommandId.Filter] = "Filter...",
        [CommandId.Open] = "Open / execute",
        [CommandId.QuickRename] = "Rename",
        [CommandId.View] = "Open in default app",
        [CommandId.OpenWith] = "Open with...",
        [CommandId.EditNewFile] = "Edit new file",
        [CommandId.Copy] = "Copy...",
        [CommandId.Move] = "Move / rename...",
        [CommandId.Delete] = "Delete (Recycle Bin)",
        [CommandId.DeletePermanently] = "Delete permanently",
        [CommandId.ContextMenu] = "Context menu",
        [CommandId.ChangeAttributes] = "Change attributes...",
        [CommandId.ChangeCase] = "Change case...",
        [CommandId.FindFiles] = "Find files and directories",
        [CommandId.FindDuplicates] = "Find duplicate files",
        [CommandId.MainMenu] = "Open the menu bar",
        [CommandId.CreateDirectory] = "Create directory",
        [CommandId.ChangeDirectory] = "Change directory (edit address bar)",
        [CommandId.OpenActiveFolder] = "Open active folder in Explorer",
        [CommandId.ToggleMark] = "Select / unselect",
        [CommandId.ToggleMarkAndMoveDown] = "Select / unselect and move down",
        [CommandId.SelectByMask] = "Select by mask...",
        [CommandId.UnselectByMask] = "Unselect by mask...",
        [CommandId.ClipboardCut] = "Cut",
        [CommandId.ClipboardCopy] = "Copy to clipboard",
        [CommandId.ClipboardPaste] = "Paste",
        [CommandId.CopyFullNameAsText] = "Copy path + name as text",
        [CommandId.CopyNameAsText] = "Copy name as text",
        [CommandId.CopyPathAsText] = "Copy path as text",
        [CommandId.ToggleDensity] = "Compact / comfortable density",
        [CommandId.AlwaysOnTop] = "Always on top",
        [CommandId.Help] = "Help",
        [CommandId.ErrorLog] = "Error log",
        [CommandId.ViewBrief] = "View: Brief",
        [CommandId.ViewDetailed] = "View: Detailed",
        [CommandId.ViewIcons] = "View: Icons",
        [CommandId.ViewThumbnails] = "View: Thumbnails",
        [CommandId.ViewTiles] = "View: Tiles",
        [CommandId.CalculateOccupiedSpace] = "Calculate occupied space",
        [CommandId.CalculateDirectorySizes] = "Calculate all directory sizes",
        [CommandId.CompareDirectories] = "Compare directories...",
        [CommandId.EditUserMenu] = "Edit user menu...",
        [CommandId.MakeFileList] = "Make file list",
        [CommandId.NewMenu] = "New (Explorer menu)",
        [CommandId.NtfsCompress] = "NTFS compress",
        [CommandId.NtfsUncompress] = "NTFS uncompress",
        [CommandId.NtfsEncrypt] = "NTFS encrypt",
        [CommandId.NtfsDecrypt] = "NTFS decrypt",
        [CommandId.Email] = "Email...",
        [CommandId.OpenedFilesList] = "List of opened files",
        [CommandId.WorkingDirectoriesList] = "List of working directories",
        [CommandId.ConnectNetworkDrive] = "Connect network drive...",
        [CommandId.DisconnectNetworkDrive] = "Disconnect network drive...",
        [CommandId.Configuration] = "Configuration...",
        [CommandId.Pack] = "Pack...",
        [CommandId.Unpack] = "Unpack...",
        [CommandId.PluginsManager] = "Plugins manager...",
        [CommandId.CalculateChecksums] = "Calculate checksums...",
        [CommandId.VerifyChecksums] = "Verify checksums...",
        [CommandId.MountImage] = "Mount disk image",
        [CommandId.UnmountImage] = "Unmount disk image",
        [CommandId.ConnectFtp] = "Connect to FTP / SFTP server...",
        [CommandId.DiskMap] = "Show disk map",
        [CommandId.TestArchive] = "Test archive",
        [CommandId.MakeSelfExtractingArchive] = "Make self-extracting archive...",
        [CommandId.ArchiveComment] = "Archive comment...",
    };

    /// <summary>A readable name ("Compare directories..."); others are derived from the id ("Sort by name").</summary>
    public static string DisplayName(CommandId command) => Localization.L.T(EnglishName(command));

    /// <summary>The untranslated name (also the key of its translation).</summary>
    public static string EnglishName(CommandId command)
    {
        if (Names.TryGetValue(command, out string? name))
            return name;
        if (command is >= CommandId.GoToHotPath0 and <= CommandId.GoToHotPath9)
            return $"Go to hot path {command - CommandId.GoToHotPath0}";
        if (command is >= CommandId.SetHotPath0 and <= CommandId.SetHotPath9)
            return $"Set hot path {command - CommandId.SetHotPath0}";
        // PascalCase → "Sentence case"
        string id = command.ToString();
        var sb = new StringBuilder(id.Length + 8);
        for (int i = 0; i < id.Length; i++)
        {
            char c = id[i];
            if (i > 0 && char.IsUpper(c))
                sb.Append(' ').Append(char.ToLowerInvariant(c));
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Group for the keyboard settings page.</summary>
    public static string Category(CommandId command) => Localization.L.T(EnglishCategory(command));

    private static string EnglishCategory(CommandId command) => command switch
    {
        CommandId.Pack or CommandId.Unpack or CommandId.MountImage or CommandId.UnmountImage => "Files",
        CommandId.CalculateChecksums or CommandId.VerifyChecksums => "Files",
        CommandId.TestArchive or CommandId.MakeSelfExtractingArchive or CommandId.ArchiveComment => "Files",
        CommandId.ConnectFtp or CommandId.DiskMap or CommandId.FindDuplicates => "Commands",
        CommandId.PluginsManager => "Options",
        <= CommandId.Filter => "Panels",
        <= CommandId.CommandShell => "Files",
        <= CommandId.GoToNextSelected => "Selection",
        <= CommandId.CopyPathAsText => "Clipboard",
        <= CommandId.DriveInformation => "Views and tools",
        <= CommandId.SetHotPath9 => "Hot paths",
        <= CommandId.SharedDirectories => "Commands",
        _ => "Options",
    };

    /// <summary>Segoe Fluent Icons glyph for the toolbar.</summary>
    public static string Glyph(CommandId command) => command switch
    {
        CommandId.GoBack => "",
        CommandId.GoForward => "",
        CommandId.ParentDirectory => "",
        CommandId.RootDirectory => "",
        CommandId.Refresh => "",
        CommandId.ThisPc => "",
        CommandId.Copy => "",
        CommandId.Move => "",
        CommandId.QuickRename => "",
        CommandId.Delete => "",
        CommandId.DeletePermanently => "",
        CommandId.CreateDirectory => "",
        CommandId.EditNewFile => "",
        CommandId.NewMenu => "",
        CommandId.Properties => "",
        CommandId.FindFiles => "",
        CommandId.Filter => "",
        CommandId.SelectAll => "",
        CommandId.UnselectAll => "",
        CommandId.InvertSelection => "",
        CommandId.SwapPanels => "",
        CommandId.ToggleHiddenFiles => "",
        CommandId.CommandShell => "",
        CommandId.OpenActiveFolder => "",
        CommandId.ToggleDensity => "",
        CommandId.View => "",
        CommandId.OpenWith => "",
        CommandId.Edit => "",
        CommandId.ClipboardCut => "",
        CommandId.ClipboardCopy => "",
        CommandId.ClipboardPaste => "",
        CommandId.PasteShortcut => "",
        CommandId.GoToShortcutTarget => "",
        CommandId.MaximizePanel => "",
        CommandId.ChangeAttributes => "",
        CommandId.ChangeCase => "",
        CommandId.CompareDirectories => "",
        CommandId.CalculateOccupiedSpace => "",
        CommandId.CalculateDirectorySizes => "",
        CommandId.DriveInformation => "",
        CommandId.UserMenu => "",
        CommandId.OpenedFilesList => "",
        CommandId.WorkingDirectoriesList => "",
        CommandId.ConnectNetworkDrive => "",
        CommandId.DisconnectNetworkDrive => "",
        CommandId.SharedDirectories => "",
        CommandId.Email => "",
        CommandId.Security => "",
        CommandId.MakeFileList => "",
        CommandId.ViewBrief => "",
        CommandId.ViewDetailed => "",
        CommandId.ViewIcons => "",
        CommandId.ViewThumbnails => "",
        CommandId.ViewTiles => "",
        CommandId.Pack => "\uE7B8",
        CommandId.Unpack => "\uE896",
        CommandId.PluginsManager => "\uEA86",
        CommandId.CalculateChecksums => "\uE9D5",
        CommandId.VerifyChecksums => "\uE73E",
        CommandId.ConnectFtp => "\uE968",
        CommandId.DiskMap => "\uEB05",
        CommandId.TestArchive => "\uE9D9",
        CommandId.MakeSelfExtractingArchive => "\uE7B8",
        CommandId.ArchiveComment => "\uE90A",
        CommandId.MountImage => "\uE958",
        CommandId.UnmountImage => "\uE711",
        // menu items (and the toolbar)
        CommandId.ChangeDriveLeft => "\uEDA2",
        CommandId.ChangeDriveRight => "\uEDA2",
        CommandId.PathFromOtherPanel => "\uE8AB",
        CommandId.OpenInOtherPanel => "\uE8A7",
        CommandId.SwitchPanel => "\uE8AB",
        CommandId.SortByName => "\uE8CB",
        CommandId.SortByExtension => "\uE7C3",
        CommandId.SortBySize => "\uEB05",
        CommandId.SortByDate => "\uE787",
        CommandId.SortByAttributes => "\uE8EC",
        CommandId.Open => "\uE8E5",
        CommandId.ContextMenu => "\uE712",
        CommandId.ChangeDirectory => "\uE8DA",
        CommandId.ToggleMark => "\uE73E",
        CommandId.ToggleMarkAndMoveDown => "\uE73E",
        CommandId.SelectByMask => "\uE762",
        CommandId.UnselectByMask => "\uE8E6",
        CommandId.RestoreSelection => "\uE7A7",
        CommandId.SelectSameExtension => "\uE762",
        CommandId.UnselectSameExtension => "\uE8E6",
        CommandId.SelectSameName => "\uE762",
        CommandId.UnselectSameName => "\uE8E6",
        CommandId.GoToPreviousSelected => "\uE70E",
        CommandId.GoToNextSelected => "\uE70D",
        CommandId.CopyFullNameAsText => "\uE8C8",
        CommandId.CopyNameAsText => "\uE8C8",
        CommandId.CopyPathAsText => "\uE8C8",
        CommandId.EditUserMenu => "\uE70F",
        CommandId.SaveSelection => "\uE74E",
        CommandId.LoadSelection => "\uE8E5",
        CommandId.HideSelectedNames => "\uED1A",
        CommandId.HideUnselectedNames => "\uED1A",
        CommandId.ShowHiddenNames => "\uE7B3",
        CommandId.NtfsCompress => "\uF012",
        CommandId.NtfsUncompress => "\uE8B7",
        CommandId.NtfsEncrypt => "\uE72E",
        CommandId.NtfsDecrypt => "\uE785",
        CommandId.AlwaysOnTop => "\uE718",
        CommandId.About => "\uE946",
        CommandId.ErrorLog => "\uEBE8",
        CommandId.Exit => "\uE7E8",
        CommandId.Configuration => "",
        CommandId.Help => "",
        _ => "",
    };

    /// <summary>Commands that make sense on the toolbar (all commands, without the per-digit hot path ones).</summary>
    public static IEnumerable<CommandId> ToolbarCommands =>
        Enum.GetValues<CommandId>().Where(c => c is not (CommandId.None or CommandId.MainMenu) && c is not (>= CommandId.GoToHotPath0 and <= CommandId.SetHotPath9));

    /// <summary>The default top toolbar (the layout of phase 1-3A).</summary>
    public static List<string> DefaultToolbar() => new()
    {
        nameof(CommandId.GoBack), nameof(CommandId.GoForward), nameof(CommandId.ParentDirectory), nameof(CommandId.Refresh), nameof(CommandId.ThisPc),
        SeparatorId,
        nameof(CommandId.Copy), nameof(CommandId.Move), nameof(CommandId.QuickRename), nameof(CommandId.Delete), nameof(CommandId.CreateDirectory), nameof(CommandId.Properties),
        SeparatorId,
        nameof(CommandId.FindFiles), nameof(CommandId.Filter),
        SeparatorId,
        nameof(CommandId.SelectAll), nameof(CommandId.UnselectAll), nameof(CommandId.InvertSelection),
        SeparatorId,
        nameof(CommandId.SwapPanels), nameof(CommandId.ToggleHiddenFiles), nameof(CommandId.CommandShell), nameof(CommandId.OpenActiveFolder),
        SeparatorId,
        ViewMenuId, nameof(CommandId.ToggleDensity),
    };
}
