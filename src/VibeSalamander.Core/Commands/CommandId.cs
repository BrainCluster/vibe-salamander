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

    // files
    Open,
    QuickRename,
    View,
    Edit,
    EditNewFile,
    Copy,
    Move,
    Delete,
    DeletePermanently,
    Properties,
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

    // clipboard text
    CopyFullNameAsText,
    CopyNameAsText,
    CopyPathAsText,

    // options / app
    ToggleDensity,
    AlwaysOnTop,
    Help,
    About,
    Exit,
}
