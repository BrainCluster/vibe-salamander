namespace VibeSalamander.Core.Settings;

// The option groups of Configuration (they follow Open Salamander's configuration pages).

/// <summary>Configuration → General.</summary>
public sealed class GeneralOptions
{
    /// <summary>Write the settings when Vibe Salamander closes (otherwise only on Save Configuration).</summary>
    public bool SaveOnExit { get; set; } = true;

    /// <summary>Copies from a CD/DVD lose the read-only attribute every file there has.</summary>
    public bool ClearReadOnlyFromCd { get; set; } = true;

    /// <summary>A second start brings the running window to the front instead of opening another one.</summary>
    public bool SingleInstance { get; set; }

    /// <summary>A beep when transfers finish while the window is in the background.</summary>
    public bool BeepWhenDoneInBackground { get; set; } = true;

    /// <summary>Enter on .iso / .img / .vhd(x) mounts the image and opens its drive in the other panel (off: the default app).</summary>
    public bool MountDiskImages { get; set; } = true;

    /// <summary>Command line: close the console window when the command ends (cmd /c instead of /k).</summary>
    public bool CloseShellAfterCommand { get; set; }

    /// <summary>Compare Directories: times that differ by at most this many seconds are equal.</summary>
    public int CompareTimeTolerance { get; set; } = 2;

    /// <summary>Compare Directories: ignore differences of exactly one or two hours (daylight saving, FAT vs NTFS).</summary>
    public bool CompareIgnoreDstHours { get; set; }

    /// <summary>Quick Rename (F2) selects the whole name including the extension.</summary>
    public bool RenameSelectsExtension { get; set; }

    /// <summary>Edit New File (Shift+F4) selects the whole name including the extension.</summary>
    public bool EditNewSelectsExtension { get; set; }

    /// <summary>How many transfers run at the same time (1-8).</summary>
    public int ParallelTransfers { get; set; } = 3;
}

public enum DeleteMode
{
    /// <summary>F8 moves everything to the Recycle Bin (Shift+Del deletes permanently).</summary>
    RecycleAll,
    /// <summary>F8 deletes permanently, without the Recycle Bin.</summary>
    DeletePermanently,
    /// <summary>F8 moves files matching the masks to the Recycle Bin and deletes the others permanently.</summary>
    RecycleMatching,
}

/// <summary>Configuration → Recycle Bin.</summary>
public sealed class RecycleOptions
{
    public DeleteMode Mode { get; set; } = DeleteMode.RecycleAll;
    public string Masks { get; set; } = "*.doc;*.docx;*.xls;*.xlsx;*.ppt;*.pptx;*.txt;*.pdf;*.jpg;*.png";
}

/// <summary>Configuration → Confirmations (Salamander's "Confirm on" / "Show message" lists).</summary>
public sealed class ConfirmationOptions
{
    // confirm on
    public bool PermanentDelete { get; set; } = true;
    public bool NonEmptyDirectoryDelete { get; set; } = true;
    public bool FileOverwrite { get; set; } = true;
    public bool HiddenSystemFileDelete { get; set; } = true;
    public bool HiddenSystemDirectoryDelete { get; set; } = true;
    public bool HiddenSystemFileOverwrite { get; set; } = true;
    public bool NtfsCompress { get; set; } = true;
    public bool NtfsEncrypt { get; set; } = true;
    public bool DragAndDrop { get; set; } = true;
    public bool DirectoryMerge { get; set; }

    // show message
    public bool CloseFind { get; set; } = true;
    public bool StopFind { get; set; } = true;
    public bool CreateTargetPath { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public bool CloseApplication { get; set; }
    public bool ExitWithRunningTransfers { get; set; } = true;
    public bool Email { get; set; } = true;
    public bool CreateMissingDirectory { get; set; } = true;
}

/// <summary>How names are shown (Salamander: Configuration → Panels → Show names).</summary>
public enum NameCasing
{
    AsStored,
    /// <summary>Partially mixed case only for names stored in DOS style (all upper case 8.3).</summary>
    MixedIfDos,
    /// <summary>Files in lower case, directories in upper case.</summary>
    LowerFilesUpperDirectories,
    PartiallyMixed,
    Lower,
    Upper,
}

/// <summary>How file sizes are shown (Configuration → Panels → Show sizes).</summary>
public enum SizeFormat
{
    /// <summary>"13,711,106" (Salamander default).</summary>
    Bytes,
    /// <summary>"13,390 KB" (Explorer default).</summary>
    KiloBytes,
    /// <summary>"13.1 MB".</summary>
    Short,
}

/// <summary>Configuration → Panels.</summary>
public sealed class PanelOptions
{
    /// <summary>Select / Unselect (Num + / Num -), Select All and Invert also mark directories.</summary>
    public bool SelectIncludesDirectories { get; set; }

    public NameCasing NameCasing { get; set; } = NameCasing.AsStored;
    public SizeFormat SizeFormat { get; set; } = SizeFormat.Bytes;

    /// <summary>Typing in a panel goes to the command line (hold Alt for quick search).</summary>
    public bool TypingToCommandLine { get; set; }

    /// <summary>Shift+0…9 also goes to the hot paths.</summary>
    public bool ShiftDigitGoesToHotPath { get; set; }

    /// <summary>Right-click opens the context menu (off: right-click marks, Ctrl+right-click opens the menu).</summary>
    public bool RightClickOpensMenu { get; set; } = true;

    /// <summary>The Vibe Salamander context menu (with "Windows context menu" at the bottom); off = the Windows menu directly.</summary>
    public bool VibeContextMenu { get; set; } = true;

    /// <summary>Clicking the name of the focused item again (slowly) starts Rename.</summary>
    public bool SlowClickRename { get; set; } = true;

    /// <summary>Drags released sooner than this (ms) are cancelled (protects against accidental drags).</summary>
    public int DragCancelMilliseconds { get; set; } = 150;

    /// <summary>Sort with the regional settings (off: plain character codes).</summary>
    public bool SortRegional { get; set; } = true;

    /// <summary>"file2" before "file10".</summary>
    public bool SortNumerical { get; set; } = true;

    /// <summary>Sort by time: newer items on top.</summary>
    public bool SortNewestFirst { get; set; } = true;

    /// <summary>Sort by time: directories stay sorted by name.</summary>
    public bool SortDirectoriesByNameOnTimeSort { get; set; }

    /// <summary>Directories have extensions too (shown in the Ext column, sorted by extension).</summary>
    public bool DirectoriesHaveExtensions { get; set; }
}

/// <summary>Configuration → Appearance.</summary>
public sealed class AppearanceOptions
{
    /// <summary>A single click opens an item (pointing selects it).</summary>
    public bool SingleClickOpens { get; set; }

    /// <summary>Hidden and system items are drawn dimmed.</summary>
    public bool DimHiddenItems { get; set; } = true;

    /// <summary>A maximize / restore button in the address bar.</summary>
    public bool MaximizeButtonInAddressBar { get; set; } = true;

    /// <summary>Panel font ("" = the system font).</summary>
    public string FontFamily { get; set; } = string.Empty;

    /// <summary>Panel font size (0 = the density's size).</summary>
    public double FontSize { get; set; }

    /// <summary>Information line; variables $(Name), $(Size), $(Date), $(Time), $(Attributes), $(Type), $(Path).</summary>
    public string InfoLineFormat { get; set; } = DefaultInfoLine;

    public const string DefaultInfoLine = "$(Name): $(Size), $(Date), $(Time), $(Attributes)";

    /// <summary>Thumbnails view: size of the preview in pixels.</summary>
    public int ThumbnailSize { get; set; } = 112;
}

public enum TitleMode
{
    DirectoryName,
    Shortened,
    FullPath,
}

/// <summary>Configuration → Main Window.</summary>
public sealed class MainWindowOptions
{
    public TitleMode TitleMode { get; set; } = TitleMode.FullPath;

    /// <summary>Text before the path in the window title (e.g. the computer name).</summary>
    public string TitlePrefix { get; set; } = string.Empty;
}

/// <summary>Per drive type settings (Configuration → Drives).</summary>
public sealed class DriveTypeOptions
{
    /// <summary>Panels update by themselves when files change.</summary>
    public bool AutoRefresh { get; set; } = true;

    /// <summary>Generic icons instead of the files' own icons (faster on slow drives).</summary>
    public bool SimpleIcons { get; set; }
}

/// <summary>Configuration → Drives, and Change Drive Menu and Drive Bar.</summary>
public sealed class DriveOptions
{
    public DriveTypeOptions Removable { get; set; } = new();
    public DriveTypeOptions Fixed { get; set; } = new();
    public DriveTypeOptions CdRom { get; set; } = new() { AutoRefresh = false, SimpleIcons = false };
    public DriveTypeOptions Network { get; set; } = new();

    /// <summary>Don't refresh network drives when the window becomes active.</summary>
    public bool NoRefreshOnActivation { get; set; }

    /// <summary>Where a panel goes when its directory disappears ("" = the nearest existing parent, else This PC).</summary>
    public string InaccessibleFallback { get; set; } = string.Empty;

    /// <summary>Drive letters ("D") left out of the drive bar and the Change Drive menu.</summary>
    public List<string> HiddenDrives { get; set; } = new();

    /// <summary>Drive letters followed by a separator in the drive bar and the Change Drive menu.</summary>
    public List<string> SeparatorAfter { get; set; } = new();

    public bool MenuShowDocuments { get; set; } = true;
    public bool MenuShowNetwork { get; set; } = true;
    public bool MenuShowOtherPanelPath { get; set; } = true;
    public bool MenuShowHotPaths { get; set; } = true;
}

/// <summary>Configuration → History.</summary>
public sealed class HistoryOptions
{
    /// <summary>Keep the lists of opened files and copy/move targets between sessions.</summary>
    public bool SaveHistory { get; set; } = true;

    /// <summary>Keep the List of Working Directories (Alt+F12) between sessions.</summary>
    public bool SaveWorkingDirectories { get; set; } = true;

    /// <summary>Up / Down in the command line recall earlier commands.</summary>
    public bool CommandLineHistory { get; set; } = true;

    /// <summary>Keep the command line history between sessions (it can contain passwords).</summary>
    public bool SaveCommandLineHistory { get; set; }
}

/// <summary>A viewer (F3) or editor (F4) for files matching masks; "" command = the default Windows app.</summary>
public sealed class ProgramRule
{
    public string Masks { get; set; } = "*.*";
    public string Command { get; set; } = string.Empty;
    public string Arguments { get; set; } = "\"$(FullName)\"";
    public string InitialDirectory { get; set; } = "$(Path)";

    public ProgramRule Clone() => new() { Masks = Masks, Command = Command, Arguments = Arguments, InitialDirectory = InitialDirectory };
}

/// <summary>Colors of the panels: a scheme and the user's own colors (#AARRGGBB or #RRGGBB, "" = scheme color).</summary>
public sealed class ColorOptions
{
    /// <summary>Scheme id (see UiState.Schemes in the app), e.g. "Vibe", "Yazi", "Norton" or "Custom".</summary>
    public string Scheme { get; set; } = "Vibe";

    public string Text { get; set; } = string.Empty;
    public string SecondaryText { get; set; } = string.Empty;
    public string Selected { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;
    public string PanelBackground { get; set; } = string.Empty;
    public string FocusBackground { get; set; } = string.Empty;
    public string DirectoryText { get; set; } = string.Empty;
    public string FocusText { get; set; } = string.Empty;

    /// <summary>Use the scheme's colors for file types (executables, archives, images, media, documents, code).</summary>
    public bool FileTypeColors { get; set; } = true;

    /// <summary>Additional rules for highlighting files and directories (first match wins).</summary>
    public List<HighlightRule> Rules { get; set; } = HighlightRule.Defaults();
}

/// <summary>Files matching the masks and the attribute conditions get their own name color.</summary>
public sealed class HighlightRule
{
    public string Masks { get; set; } = "*.*";

    // null = don't care
    public bool? Archive { get; set; }
    public bool? ReadOnly { get; set; }
    public bool? Hidden { get; set; }
    public bool? System { get; set; }
    public bool? Compressed { get; set; }
    public bool? Encrypted { get; set; }
    public bool? Directory { get; set; }

    /// <summary>Name color "#RRGGBB".</summary>
    public string Color { get; set; } = "#2E8B57";

    public HighlightRule Clone()
    {
        var copy = (HighlightRule)MemberwiseClone();
        copy._mask = null;
        return copy;
    }

    /// <summary>No rules: file types are colored by the scheme (Configuration → Colors → File type colors).</summary>
    public static List<HighlightRule> Defaults() => new();

    /// <summary>The two rules earlier versions created by default (now covered by the scheme's file type colors).</summary>
    public static bool IsLegacyDefault(HighlightRule rule) =>
        rule.Directory == false && rule.Archive == null && rule.ReadOnly == null && rule.Hidden == null &&
        rule.System == null && rule.Compressed == null && rule.Encrypted == null &&
        ((rule.Masks == "*.exe;*.com;*.bat;*.cmd;*.ps1;*.msi" && rule.Color == "#2E9E5B") ||
         (rule.Masks == "*.zip;*.7z;*.rar;*.tar;*.gz;*.tgz;*.bz2;*.xz;*.cab;*.iso" && rule.Color == "#9B59B6"));

    /// <summary>True when the item matches the masks and every attribute condition.</summary>
    public bool Matches(string name, FileAttributes attributes, bool isDirectory)
    {
        static bool Check(bool? wanted, bool actual) => wanted == null || wanted == actual;
        if (!Check(Directory, isDirectory) ||
            !Check(Archive, (attributes & FileAttributes.Archive) != 0) ||
            !Check(ReadOnly, (attributes & FileAttributes.ReadOnly) != 0) ||
            !Check(Hidden, (attributes & FileAttributes.Hidden) != 0) ||
            !Check(System, (attributes & FileAttributes.System) != 0) ||
            !Check(Compressed, (attributes & FileAttributes.Compressed) != 0) ||
            !Check(Encrypted, (attributes & FileAttributes.Encrypted) != 0))
            return false;
        if (_mask == null || !string.Equals(_maskText, Masks, StringComparison.Ordinal))
        {
            _maskText = Masks;
            _mask = new FileSystem.WildcardMask(Masks);
        }
        return _mask.IsMatch(name);
    }

    private FileSystem.WildcardMask? _mask;
    private string? _maskText;
}

/// <summary>Plugins (Plugins → Plugins Manager) and the last choices of Pack / Unpack.</summary>
public sealed class PluginOptions
{
    /// <summary>Ids of plugins that are not loaded ("vibe.archive").</summary>
    public List<string> Disabled { get; set; } = new();

    /// <summary>Last format of Pack (Alt+F5), e.g. "zip", "tar.gz".</summary>
    public string PackFormat { get; set; } = "zip";

    /// <summary>"Store", "Fastest", "Normal" or "Maximum".</summary>
    public string PackCompression { get; set; } = "Normal";

    /// <summary>Move to archive: the packed files are deleted when packing succeeded.</summary>
    public bool PackDeleteSources { get; set; }

    /// <summary>Unpack (Alt+F6) each archive into a directory named after it.</summary>
    public bool UnpackToSubfolder { get; set; } = true;

    /// <summary>Files to unpack.</summary>
    public string UnpackMask { get; set; } = "*.*";
}
