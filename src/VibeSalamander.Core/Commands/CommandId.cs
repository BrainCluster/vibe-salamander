// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Core.Commands;

/// <summary>
/// Every user action. Menus, toolbar, F-key bar and keyboard shortcuts all resolve to these ids.
/// Names follow Open Salamander's CM_* commands.
/// </summary>
public enum CommandId
{
    None,

    // panel navigation
    ChangeDriveLeft,
    ChangeDriveRight,
    GoBack,
    GoForward,
    ParentDirectory,
    RootDirectory,
    PathFromOtherPanel,
    OpenInOtherPanel,
    ThisPc,
    Refresh,
    SwitchPanel,
    SwapPanels,
    MaximizePanel,
    SortByName,
    SortByExtension,
    SortBySize,
    SortByDate,
    SortByAttributes,
    ToggleHiddenFiles,
    Filter,

    // files
    Open,
    QuickRename,
    View,
    OpenWith,
    Edit,
    EditNewFile,
    Copy,
    Move,
    Delete,
    DeletePermanently,
    Properties,
    ContextMenu,
    ChangeAttributes,
    ChangeCase,
    GoToShortcutTarget,
    FindFiles,
    CreateDirectory,
    ChangeDirectory,
    OpenActiveFolder,
    CommandShell,

    // selection
    ToggleMark,
    ToggleMarkAndMoveDown,
    SelectAll,
    UnselectAll,
    InvertSelection,
    SelectByMask,
    UnselectByMask,
    RestoreSelection,
    SelectSameExtension,
    UnselectSameExtension,
    SelectSameName,
    UnselectSameName,
    GoToPreviousSelected,
    GoToNextSelected,

    // clipboard (files)
    ClipboardCut,
    ClipboardCopy,
    ClipboardPaste,

    // clipboard text
    CopyFullNameAsText,
    CopyNameAsText,
    CopyPathAsText,

    // phase 3: views, hot paths, sizes, drive information
    ViewBrief,
    ViewDetailed,
    ViewIcons,
    ViewThumbnails,
    ViewTiles,
    CalculateOccupiedSpace,
    CalculateDirectorySizes,
    DriveInformation,
    GoToHotPath0, GoToHotPath1, GoToHotPath2, GoToHotPath3, GoToHotPath4,
    GoToHotPath5, GoToHotPath6, GoToHotPath7, GoToHotPath8, GoToHotPath9,
    SetHotPath0, SetHotPath1, SetHotPath2, SetHotPath3, SetHotPath4,
    SetHotPath5, SetHotPath6, SetHotPath7, SetHotPath8, SetHotPath9,

    // phase 3B: commands
    CompareDirectories,
    UserMenu,
    EditUserMenu,
    SaveSelection,
    LoadSelection,
    HideSelectedNames,
    HideUnselectedNames,
    ShowHiddenNames,
    MakeFileList,
    PasteShortcut,
    NewMenu,
    NtfsCompress,
    NtfsUncompress,
    NtfsEncrypt,
    NtfsDecrypt,
    Security,
    Email,
    OpenedFilesList,
    WorkingDirectoriesList,
    ConnectNetworkDrive,
    DisconnectNetworkDrive,
    SharedDirectories,

    // options / app
    Configuration,
    ToggleDensity,
    AlwaysOnTop,
    Help,
    About,
    Exit,

    // phase 4: plugins (the archive plugin)
    Pack,
    Unpack,
    PluginsManager,
    CalculateChecksums,
    VerifyChecksums,

    // disk images
    MountImage,
    UnmountImage,

    // the FTP / SFTP plugin
    ConnectFtp,

    // the Disk Map plugin
    DiskMap,

    // the archive plugin's commands
    TestArchive,
    MakeSelfExtractingArchive,
    ArchiveComment,

    // Help → Error Log
    ErrorLog,

    // Commands → Find Duplicates (Alt+Shift+F7)
    FindDuplicates,

    // the menu bar from the keyboard (F9 in Total Commander's shortcuts; F10 always)
    MainMenu,

    // Files → Compare Files (Ctrl+Shift+C)
    CompareFiles,

    // a copy next to the original, named in place (Alt+Shift+D; Shift+F5 in Total Commander's shortcuts)
    Duplicate,
    // many names at once: masks, search and replace, counter (Ctrl+M in Total Commander's shortcuts)
    MultiRename,
    // all files of the folder and its subfolders in one list (Ctrl+B)
    FlatView,
    // the other panel shows the file under the caret (Ctrl+Q)
    QuickView,
    // the hot paths as a menu (Ctrl+D in Total Commander's shortcuts)
    HotPathList,
    // names into the command line (Ctrl+Enter, Ctrl+Shift+Enter, Ctrl+P in Total Commander's shortcuts)
    NameToCommandLine,
    FullNameToCommandLine,
    PathToCommandLine,
    // the earlier commands of the command line (Alt+F8)
    CommandHistory,
    // shortcuts (.lnk) to the items, on the clipboard: Paste puts them anywhere, also in Explorer (Ctrl+Shift+L)
    CopyAsShortcut,
}
