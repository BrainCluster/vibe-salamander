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

    // options / app
    ToggleDensity,
    AlwaysOnTop,
    Help,
    About,
    Exit,
}
