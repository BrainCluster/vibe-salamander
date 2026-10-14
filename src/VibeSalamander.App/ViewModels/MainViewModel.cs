// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using VibeSalamander.Core;
using VibeSalamander.Core.Commands;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Formatting;
using VibeSalamander.Core.Interop;
using VibeSalamander.Core.Operations;
using VibeSalamander.Core.Panels;
using VibeSalamander.Core.Settings;
using VibeSalamander.Core.Tools;
using VibeSalamander.Plugins;
using VibeSalamander.Services;

namespace VibeSalamander.ViewModels;

/// <summary>UI services the main view model needs from the window.</summary>
public interface IUiHost
{
    IntPtr WindowHandle { get; }
    Task<string?> PromptAsync(string title, string label, string initialText, string primaryButton, bool selectNameWithoutExtension = false);
    Task<bool> ConfirmAsync(string title, string message, string primaryButton);
    Task ShowMessageAsync(string title, string message);
    void ShowDriveMenu(PaneViewModel pane);
    void FocusPane(PaneViewModel pane);
    void SetPanelMaximized(PaneViewModel? pane);
    void ApplyDensity();
    void ApplyAlwaysOnTop(bool onTop);
    void ShowAbout();
    void Exit();

    // phase 2
    Views.Dialogs Dialogs { get; }
    /// <summary>A short non-modal message (errors, hints) that disappears by itself, instead of a dialog.</summary>
    void Notify(string message, bool isError = true);
    /// <summary>Shift+F7: puts the pane's address bar into edit mode (no dialog).</summary>
    void EditPath(PaneViewModel pane);
    /// <summary>Ctrl+F1: volume details in a flyout at the pane's address bar.</summary>
    void ShowDriveInformation(PaneViewModel pane);
    void ShowFindWindow(string directory);
    Task<string?> EditNameInPlaceAsync(PaneViewModel pane, FileItemViewModel item, bool selectWholeName = false);
    void ShowContextMenu(PaneViewModel pane);

    // phase 3C
    /// <summary>Options → Configuration: the settings window.</summary>
    void ShowConfiguration();

    // phase 4
    /// <summary>The configuration window at a page ("Plugins").</summary>
    void ShowConfigurationPage(string page);

    /// <summary>Brings the main window back in front (e.g. after Explorer opened a window).</summary>
    void BringToFront();

    // phase 3B
    /// <summary>Shows a command menu at the pane's caret item (atCaret) or at its address bar.</summary>
    void ShowMenu(PaneViewModel pane, IReadOnlyList<MenuEntry> entries, bool atCaret);
    /// <summary>The Explorer "New" menu for the pane's directory; true when an item was chosen.</summary>
    bool ShowNewMenu(PaneViewModel pane);
}

/// <summary>Engine prompts marshalled to the UI thread.</summary>
internal sealed class UiOperationCallbacks : IOperationCallbacks
{
    private readonly DispatcherQueue _dispatcher;
    private readonly Views.Dialogs _dialogs;

    public UiOperationCallbacks(DispatcherQueue dispatcher, Views.Dialogs dialogs)
    {
        _dispatcher = dispatcher;
        _dialogs = dialogs;
    }

    public Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict) => OnUi(() => _dialogs.ConflictAsync(conflict));

    public Task<ErrorChoice> ResolveErrorAsync(OperationError error) => OnUi(() => _dialogs.ErrorAsync(error));

    private Task<T> OnUi<T>(Func<Task<T>> action)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = _dispatcher.TryEnqueue(async () =>
        {
            try
            {
                tcs.SetResult(await action());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        if (!queued)
            tcs.SetException(new InvalidOperationException("The window is closing."));
        return tcs.Task;
    }
}

/// <summary>Owns both panels and executes every <see cref="CommandId"/>.</summary>
public sealed class MainViewModel : BindableBase
{
    private readonly IUiHost _host;
    private readonly DispatcherQueue _dispatcher;
    private readonly UiOperationCallbacks _callbacks;
    private PaneViewModel _active;
    private PaneViewModel? _maximized;
    private string _lastSelectMask = "*.*";
    private bool _lastSelectDirectories;

    public MainViewModel(IUiHost host, DispatcherQueue dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;
        _callbacks = new UiOperationCallbacks(dispatcher, host.Dialogs);
        Settings = SettingsStore.Load();
        Registry = new FileSystemRegistry();
        // plugins: archives open like directories ("C:\\x.zip\\dir"), so their provider goes before the disk's
        Plugins = new PluginManager();
        ArchiveProvider = new ArchiveFileSystemProvider(Plugins);
        Registry.RegisterFirst(ArchiveProvider);
        Icons = new IconCache(dispatcher);
        Thumbnails = new ThumbnailCache(dispatcher);
        Settings.HotPaths ??= new List<string>(); // "HotPaths": null in a hand-edited settings file
        while (Settings.HotPaths.Count < 10)
            Settings.HotPaths.Add(string.Empty);
        Settings.KeyBindings ??= new List<KeyBindingOverride>();
        Settings.Toolbar ??= CommandCatalog.DefaultToolbar();
        KeyMap = KeyMap.Create(Settings.KeyBindings);

        Left = new PaneViewModel("Left", Registry, dispatcher, Icons, Settings.Left, () => Settings.ShowHidden);
        Right = new PaneViewModel("Right", Registry, dispatcher, Icons, Settings.Right, () => Settings.ShowHidden);
        _active = Settings.RightPanelActive ? Right : Left;
        _active.IsActive = true;
        Settings.UserMenu ??= UserMenu.CreateDefaults();
        Settings.Compare ??= new CompareOptions();
        Settings.OpenedFiles ??= new List<string>();
        Settings.WorkingDirectories ??= new List<string>();
        foreach (PaneViewModel pane in new[] { Left, Right })
        {
            pane.Navigated += (_, path) => RememberRecent(Settings.WorkingDirectories, path, WorkingDirectoriesLimit);
            pane.FileOpened += (_, path) => RememberRecent(Settings.OpenedFiles, path);
            PaneViewModel p = pane;
            pane.ArchiveFileOpenRequested += async (_, item) => await OpenFromArchiveAsync(p, item);
            pane.DiskImageOpenRequested += async (_, path) => await OpenDiskImageAsync(p, path);
        }

        Settings.General ??= new GeneralOptions();
        Settings.Recycle ??= new RecycleOptions();
        Settings.Confirm ??= new ConfirmationOptions();
        Settings.Panels ??= new PanelOptions();
        Settings.Appearance ??= new AppearanceOptions();
        Settings.Colors ??= new ColorOptions();
        Settings.Colors.Rules ??= HighlightRule.Defaults();
        Settings.Colors.Rules.RemoveAll(HighlightRule.IsLegacyDefault); // replaced by the schemes' file type colors
        Settings.Window ??= new MainWindowOptions();
        Settings.Window.TitlePrefix ??= string.Empty;
        Settings.Drives ??= new DriveOptions();
        Settings.Drives.Removable ??= new DriveTypeOptions();
        Settings.Drives.Fixed ??= new DriveTypeOptions();
        Settings.Drives.CdRom ??= new DriveTypeOptions();
        Settings.Drives.Network ??= new DriveTypeOptions();
        Settings.Drives.HiddenDrives ??= new List<string>();
        Settings.Drives.SeparatorAfter ??= new List<string>();
        Settings.History ??= new HistoryOptions();
        Settings.Plugins ??= new PluginOptions();
        Settings.Plugins.Disabled ??= new List<string>();
        Settings.Plugins.PackFormat ??= "zip";
        Settings.Plugins.PackCompression ??= "Normal";
        Settings.Plugins.UnpackMask ??= "*.*";
        Settings.Viewers ??= new List<ProgramRule>();
        Settings.Editors ??= new List<ProgramRule>();
        Settings.CommandLineHistory ??= new List<string>();
        Settings.HotPathNames ??= new List<string>();
        while (Settings.HotPathNames.Count < 10)
            Settings.HotPathNames.Add(string.Empty);
        _lastSelectDirectories = Settings.Panels.SelectIncludesDirectories;

        Transfers = new TransfersViewModel(dispatcher, host.Dialogs,
            () => new EngineOptions
            {
                ConfirmFileOverwrite = Settings.Confirm.FileOverwrite,
                ConfirmHiddenSystemOverwrite = Settings.Confirm.HiddenSystemFileOverwrite,
                ConfirmDirectoryMerge = Settings.Confirm.DirectoryMerge,
                ClearReadOnlyFromCd = Settings.General.ClearReadOnlyFromCd,
            },
            () => Math.Clamp(Settings.General.ParallelTransfers, 1, 8));
        Transfers.Finished += async (_, _) =>
        {
            try
            {
                await RefreshAllAsync();
            }
            catch (Exception)
            {
                // the watcher refreshes the panels anyway; a failed refresh is not worth a dialog
            }
        };
    }

    /// <summary>Built-in and installed plugins (Plugins → Plugins Manager).</summary>
    public PluginManager Plugins { get; }

    /// <summary>Archives as directories (Enter on an archive).</summary>
    public ArchiveFileSystemProvider ArchiveProvider { get; }

    /// <summary>Loads the plugins; called by the window once the panels exist.</summary>
    public void LoadPlugins() => Plugins.LoadAll(this, Settings.Plugins.Disabled);

    /// <summary>A notification-bar message (also callable from plugins on other threads).</summary>
    public void Notify(string message, bool isError = true)
    {
        if (_dispatcher.HasThreadAccess)
            _host.Notify(message, isError);
        else
            _dispatcher.TryEnqueue(() => _host.Notify(message, isError));
    }

    /// <summary>Background copy/move/delete queue shown in the transfer panel.</summary>
    public TransfersViewModel Transfers { get; }

    public AppSettings Settings { get; }
    public FileSystemRegistry Registry { get; }
    public IconCache Icons { get; }
    public ThumbnailCache Thumbnails { get; }
    /// <summary>Shortcuts: the Salamander defaults plus the user's changes (Configuration → Keyboard).</summary>
    public KeyMap KeyMap { get; private set; }

    /// <summary>Replaces the key map (after the keyboard settings changed) and stores the differences.</summary>
    public void SetKeyMap(KeyMap map)
    {
        KeyMap = map;
        Settings.KeyBindings = map.GetOverrides();
        OnPropertyChanged(nameof(KeyMap));
    }

    /// <summary>Set after Import Configuration / Reset: the imported file must not be overwritten on exit.</summary>
    public bool SuppressSettingsSave { get; set; }
    public PaneViewModel Left { get; }
    public PaneViewModel Right { get; }

    public IntPtr WindowHandle => _host.WindowHandle;

    public PaneViewModel Active => _active;
    public PaneViewModel Inactive => _active == Left ? Right : Left;

    public async Task InitializeAsync()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Task drivesLeft = Left.LoadDrivesAsync();
        Task drivesRight = Right.LoadDrivesAsync();
        await Task.WhenAll(
            Left.NavigateAsync(StartPath(Settings.Left.Path, home), addToHistory: false),
            Right.NavigateAsync(StartPath(Settings.Right.Path, home), addToHistory: false),
            drivesLeft, drivesRight);
        _host.FocusPane(_active);
    }

    private string StartPath(string saved, string fallback)
    {
        if (saved.StartsWith(ThisPcProvider.RootPath, StringComparison.OrdinalIgnoreCase))
            return saved;
        return Directory.Exists(saved) || ArchiveProvider.CanHandle(saved) ? saved : fallback; // a panel may have been in an archive
    }

    public void Activate(PaneViewModel pane)
    {
        if (_active == pane)
            return;
        _active.IsActive = false;
        _active.EndQuickSearch();
        _active = pane;
        _active.IsActive = true;
        OnPropertyChanged(nameof(Active));
        OnPropertyChanged(nameof(WindowTitle));
    }

    public string WindowTitle => $"{_active.DisplayPath} - Vibe Salamander";

    public void SaveSettings() => SaveSettings(onExit: false);

    /// <summary>Writes the settings; on exit only when General → Save configuration on exit is on.</summary>
    public void SaveSettings(bool onExit)
    {
        if (SuppressSettingsSave || (onExit && !Settings.General.SaveOnExit))
            return;
        Left.StoreSettings(Settings.Left);
        Right.StoreSettings(Settings.Right);
        Settings.RightPanelActive = _active == Right;

        // Configuration → History: lists that should not be kept are left out of the file (they stay in memory)
        List<string> opened = Settings.OpenedFiles, working = Settings.WorkingDirectories, commands = Settings.CommandLineHistory;
        try
        {
            if (!Settings.History.SaveHistory)
                Settings.OpenedFiles = new List<string>();
            if (!Settings.History.SaveWorkingDirectories)
                Settings.WorkingDirectories = new List<string>();
            if (!Settings.History.SaveCommandLineHistory)
                Settings.CommandLineHistory = new List<string>();
            SettingsStore.Save(Settings);
        }
        finally
        {
            Settings.OpenedFiles = opened;
            Settings.WorkingDirectories = working;
            Settings.CommandLineHistory = commands;
        }
    }

    /// <summary>Configuration → History → Clear History.</summary>
    public void ClearHistory()
    {
        Settings.OpenedFiles.Clear();
        Settings.WorkingDirectories.Clear();
        Settings.CommandLineHistory.Clear();
        _savedSelection.Clear();
        SaveSettings();
    }

    // ---- command dispatch --------------------------------------------------------------------

    public bool CanExecute(CommandId command) => command switch
    {
        CommandId.Copy or CommandId.Move or CommandId.Delete or CommandId.DeletePermanently or
        CommandId.QuickRename or CommandId.CreateDirectory or CommandId.EditNewFile or
        CommandId.ChangeAttributes or CommandId.ChangeCase or CommandId.ClipboardCut or
        CommandId.ClipboardCopy or CommandId.ClipboardPaste => _active.IsLocalDisk,
        CommandId.GoBack => _active.CanGoBack,
        CommandId.GoForward => _active.CanGoForward,
        _ => true,
    };

    public async Task ExecuteAsync(CommandId command)
    {
        PaneViewModel p = _active;
        try
        {
            if (p.IsArchive && await ArchiveCommandAsync(p, command))
                return;
            switch (command)
            {
                case CommandId.ChangeDriveLeft: Activate(Left); _host.ShowDriveMenu(Left); break;
                case CommandId.ChangeDriveRight: Activate(Right); _host.ShowDriveMenu(Right); break;
                case CommandId.GoBack: await p.GoBackAsync(); break;
                case CommandId.GoForward: await p.GoForwardAsync(); break;
                case CommandId.ParentDirectory: await p.GoUpAsync(); break;
                case CommandId.RootDirectory: await p.GoToRootAsync(); break;
                case CommandId.ThisPc: await p.NavigateAsync(ThisPcProvider.RootPath); break;
                case CommandId.PathFromOtherPanel: await p.NavigateAsync(Inactive.Path); break;
                case CommandId.OpenInOtherPanel: await OpenInOtherPanelAsync(); break;
                case CommandId.Refresh: await p.RefreshAsync(); await Task.WhenAll(Left.LoadDrivesAsync(), Right.LoadDrivesAsync()); break;
                case CommandId.SwitchPanel: Activate(Inactive); _host.FocusPane(_active); break;
                case CommandId.SwapPanels: await SwapPanelsAsync(); break;
                case CommandId.MaximizePanel: _maximized = _maximized == null ? p : null; _host.SetPanelMaximized(_maximized); break;
                case CommandId.SortByName: p.SetSort(SortMode.Name); break;
                case CommandId.SortByExtension: p.SetSort(SortMode.Extension); break;
                case CommandId.SortBySize: p.SetSort(SortMode.Size); break;
                case CommandId.SortByDate: p.SetSort(SortMode.Date); break;
                case CommandId.SortByAttributes: p.SetSort(SortMode.Attributes); break;
                case CommandId.ToggleHiddenFiles:
                    Settings.ShowHidden = !Settings.ShowHidden;
                    await Task.WhenAll(Left.RefreshAsync(), Right.RefreshAsync());
                    break;

                case CommandId.Filter: await FilterAsync(); break;

                // phase 3
                case CommandId.ViewBrief: p.ViewMode = ViewMode.Brief; break;
                case CommandId.ViewDetailed: p.ViewMode = ViewMode.Detailed; break;
                case CommandId.ViewIcons: p.ViewMode = ViewMode.Icons; break;
                case CommandId.ViewThumbnails: p.ViewMode = ViewMode.Thumbnails; break;
                case CommandId.ViewTiles: p.ViewMode = ViewMode.Tiles; break;
                case CommandId.CalculateOccupiedSpace: await CalculateOccupiedSpaceAsync(p, p.GetOperationItems()); break;

                // phase 3B
                case CommandId.CompareDirectories: await CompareDirectoriesAsync(); break;
                case CommandId.UserMenu: ShowUserMenu(p); break;
                case CommandId.EditUserMenu: await EditUserMenuAsync(); break;
                case CommandId.SaveSelection: SaveSelection(p); break;
                case CommandId.LoadSelection: LoadSelection(p); break;
                case CommandId.HideSelectedNames: HideNames(p, selected: true); break;
                case CommandId.HideUnselectedNames: HideNames(p, selected: false); break;
                case CommandId.ShowHiddenNames: ShowHiddenNames(p); break;
                case CommandId.MakeFileList: MakeFileList(p); break;
                case CommandId.PasteShortcut: await PasteShortcutsAsync(p); break;
                case CommandId.NewMenu: await NewFromShellMenuAsync(p); break;
                case CommandId.NtfsCompress: await NtfsAsync(p, NtfsAction.Compress); break;
                case CommandId.NtfsUncompress: await NtfsAsync(p, NtfsAction.Uncompress); break;
                case CommandId.NtfsEncrypt: await NtfsAsync(p, NtfsAction.Encrypt); break;
                case CommandId.NtfsDecrypt: await NtfsAsync(p, NtfsAction.Decrypt); break;
                case CommandId.Security: ShowSecurity(p); break;
                case CommandId.Email: await EmailAsync(p); break;
                case CommandId.OpenedFilesList: ShowOpenedFiles(p); break;
                case CommandId.WorkingDirectoriesList: ShowWorkingDirectories(p); break;
                case CommandId.ConnectNetworkDrive:
                    if (NetworkDrives.ShowConnectDialog(_host.WindowHandle))
                        await Task.WhenAll(Left.LoadDrivesAsync(), Right.LoadDrivesAsync());
                    break;
                case CommandId.DisconnectNetworkDrive:
                    if (NetworkDrives.ShowDisconnectDialog(_host.WindowHandle))
                        await Task.WhenAll(Left.LoadDrivesAsync(), Right.LoadDrivesAsync());
                    break;
                case CommandId.SharedDirectories: await ShowSharesAsync(p); break;
                case CommandId.Configuration: _host.ShowConfiguration(); break;
                case CommandId.PluginsManager: _host.ShowConfigurationPage("Plugins"); break;
                case CommandId.CalculateDirectorySizes:
                    await CalculateOccupiedSpaceAsync(p, p.Items.Where(i => i.Entry.IsDirectory && !i.Entry.IsUpDir && !i.IsDrive).ToList());
                    break;
                case CommandId.DriveInformation: _host.ShowDriveInformation(p); break;
                case >= CommandId.GoToHotPath0 and <= CommandId.GoToHotPath9: await GoToHotPathAsync(command - CommandId.GoToHotPath0); break;
                case >= CommandId.SetHotPath0 and <= CommandId.SetHotPath9: SetHotPath(command - CommandId.SetHotPath0); break;
                case CommandId.OpenWith: OpenWith(); break;
                case CommandId.ContextMenu: _host.ShowContextMenu(p); break;
                case CommandId.ChangeAttributes: await ChangeAttributesAsync(); break;
                case CommandId.ChangeCase: await ChangeCaseAsync(); break;
                case CommandId.GoToShortcutTarget: await GoToShortcutTargetAsync(); break;
                case CommandId.FindFiles: _host.ShowFindWindow(p.IsLocalDisk ? p.Path : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)); break;
                case CommandId.SelectByMask: await MarkByMaskAsync(mark: true); break;
                case CommandId.UnselectByMask: await MarkByMaskAsync(mark: false); break;
                case CommandId.RestoreSelection: p.RestoreSelection(); break;
                case CommandId.SelectSameExtension: p.MarkSame(byExtension: true, mark: true); break;
                case CommandId.UnselectSameExtension: p.MarkSame(byExtension: true, mark: false); break;
                case CommandId.SelectSameName: p.MarkSame(byExtension: false, mark: true); break;
                case CommandId.UnselectSameName: p.MarkSame(byExtension: false, mark: false); break;
                case CommandId.GoToPreviousSelected: MoveToMarked(p, -1); break;
                case CommandId.GoToNextSelected: MoveToMarked(p, +1); break;
                case CommandId.ClipboardCut: await ClipboardSetAsync(cut: true); break;
                case CommandId.ClipboardCopy: await ClipboardSetAsync(cut: false); break;
                case CommandId.ClipboardPaste: await ClipboardPasteAsync(); break;

                case CommandId.Open: await p.OpenFocusedAsync(); break;
                case CommandId.QuickRename: await RenameAsync(); break;
                case CommandId.View: ViewFile(); break;
                case CommandId.Edit: EditFile(); break;
                case CommandId.EditNewFile: await EditNewFileAsync(); break;
                case CommandId.Copy: await CopyOrMoveAsync(move: false); break;
                case CommandId.Move: await CopyOrMoveAsync(move: true); break;
                case CommandId.Pack: await PackAsync(); break;
                case CommandId.MountImage:
                    if (p.FocusedItem is { Entry.IsDirectory: false } diskImage && p.IsLocalDisk && DiskImages.IsDiskImage(diskImage.Entry.FullPath))
                        await MountImageAsync(p, diskImage.Entry.FullPath);
                    break;
                case CommandId.UnmountImage:
                    if (ImageDriveOf(p) is { } imageRoot)
                        await UnmountImageAsync(imageRoot);
                    else
                        _host.Notify("Focus a mounted disk image (a drive in This PC, or a panel on it) to unmount it.", isError: false);
                    break;
                case CommandId.Unpack: await UnpackAsync(); break;
                case CommandId.Delete: await DeleteAsync(permanent: false); break;
                case CommandId.DeletePermanently: await DeleteAsync(permanent: true); break;
                case CommandId.Properties: ShowProperties(); break;
                case CommandId.CreateDirectory: await CreateDirectoryAsync(); break;
                case CommandId.ChangeDirectory: ChangeDirectory(); break;
                case CommandId.OpenActiveFolder: if (p.IsLocalDisk) ShellOperations.OpenInExplorer(p.Path); else ShellOperations.OpenInExplorer("shell:MyComputerFolder"); break;
                case CommandId.CommandShell: if (p.IsLocalDisk) ShellOperations.OpenCommandShell(p.Path); break;

                case CommandId.ToggleMark: p.ToggleMark(p.CaretIndex); break;
                case CommandId.ToggleMarkAndMoveDown: p.ToggleMark(p.CaretIndex); MoveCaret(p, +1); break;
                case CommandId.SelectAll: p.MarkAll(true); break;
                case CommandId.UnselectAll: p.MarkAll(false); break;
                case CommandId.InvertSelection: p.InvertMarks(); break;

                case CommandId.CopyFullNameAsText: CopyText(string.Join(Environment.NewLine, p.GetOperationItems().Select(i => i.Entry.FullPath))); break;
                case CommandId.CopyNameAsText: CopyText(string.Join(Environment.NewLine, p.GetOperationItems().Select(i => i.Entry.Name))); break;
                case CommandId.CopyPathAsText: CopyText(p.IsLocalDisk ? p.Path : p.DisplayPath); break;

                case CommandId.ToggleDensity:
                    Settings.Density = Settings.Density == Density.Compact ? Density.Comfortable : Density.Compact;
                    _host.ApplyDensity();
                    break;
                case CommandId.AlwaysOnTop:
                    if (!Settings.AlwaysOnTop && Settings.Confirm.AlwaysOnTop &&
                        !await _host.ConfirmAsync("Always on Top", "Do you want to keep the Vibe Salamander window on top of all other windows?", "Turn On"))
                        break;
                    Settings.AlwaysOnTop = !Settings.AlwaysOnTop;
                    _host.ApplyAlwaysOnTop(Settings.AlwaysOnTop);
                    break;
                case CommandId.Help:
                    ShellOperations.Open("https://github.com/OpenSalamander/salamander");
                    break;
                case CommandId.About: _host.ShowAbout(); break;
                case CommandId.Exit: _host.Exit(); break;
            }
        }
        catch (Exception ex)
        {
            _host.Notify(ex.Message);
        }
        OnPropertyChanged(nameof(WindowTitle));
    }

    /// <summary>Raised when the caret should move (Insert marks and moves down).</summary>
    public event EventHandler<(PaneViewModel Pane, int Index)>? CaretMoveRequested;

    private void MoveCaret(PaneViewModel pane, int delta)
    {
        int index = Math.Clamp(pane.CaretIndex + delta, 0, Math.Max(0, pane.Items.Count - 1));
        CaretMoveRequested?.Invoke(this, (pane, index));
    }

    private static void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    // ---- commands ----------------------------------------------------------------------------

    private async Task OpenInOtherPanelAsync()
    {
        FileItemViewModel? item = _active.FocusedItem;
        if (item == null)
            return;
        if (item.Drive is { } drive)
            await Inactive.ChangeDriveAsync(drive.Root);
        else if (item.Entry.IsUpDir)
        {
            ParentLocation? parent = Registry.Find(_active.Path)?.GetParent(_active.Path);
            if (parent != null)
                await Inactive.NavigateAsync(parent.Path, parent.FocusName);
        }
        else if (item.Entry.IsDirectory)
            await Inactive.NavigateAsync(item.Entry.FullPath);
        else
            await Inactive.NavigateAsync(_active.Path, item.Entry.Name);
    }

    private async Task SwapPanelsAsync()
    {
        string left = Left.IsThisPc ? ThisPcProvider.RootPath : Left.Path;
        string right = Right.IsThisPc ? ThisPcProvider.RootPath : Right.Path;
        await Task.WhenAll(Left.NavigateAsync(right), Right.NavigateAsync(left));
    }

    private void ViewFile()
    {
        // no internal viewer: F3 uses a configured viewer for the file's mask, else the associated program
        FileItemViewModel? item = _active.FocusedItem;
        if (item != null && !item.Entry.IsDirectory)
        {
            if (!RunProgramRule(Settings.Viewers, item))
                ShellOperations.Open(item.Entry.FullPath, _active.Path);
            RememberRecent(Settings.OpenedFiles, item.Entry.FullPath);
        }
    }

    private void EditFile()
    {
        FileItemViewModel? item = _active.FocusedItem;
        if (item != null && !item.Entry.IsDirectory)
        {
            if (!RunProgramRule(Settings.Editors, item))
                ShellOperations.Edit(item.Entry.FullPath);
            RememberRecent(Settings.OpenedFiles, item.Entry.FullPath);
        }
    }

    /// <summary>Configuration → Viewers / Editors: the first rule whose masks match starts its program.</summary>
    private bool RunProgramRule(IEnumerable<ProgramRule> rules, FileItemViewModel item, UserMenuContext? fileContext = null)
    {
        foreach (ProgramRule rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Command) || !new WildcardMask(rule.Masks).IsMatch(item.Entry.Name))
                continue;
            UserMenuContext context = fileContext ?? UserMenuContextFor(_active);
            string directory = UserMenu.Expand(string.IsNullOrWhiteSpace(rule.InitialDirectory) ? "$(Path)" : rule.InitialDirectory, context);
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    UserMenu.Expand(rule.Command, context).Trim(), UserMenu.Expand(rule.Arguments, context))
                {
                    UseShellExecute = true,
                    WorkingDirectory = Directory.Exists(directory) ? directory : string.Empty,
                });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
            {
                _host.Notify($"{rule.Command}: {ex.Message}");
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// Shift+F4: creates "New Text Document.txt" in the panel and lets you name it in place (like Explorer),
    /// then opens it in the editor. Esc keeps the suggested name.
    /// </summary>
    private async Task EditNewFileAsync()
    {
        PaneViewModel pane = _active;
        if (!pane.IsLocalDisk)
            return;
        string path = UniquePath(pane.Path, "New Text Document", ".txt");
        await File.WriteAllTextAsync(path, string.Empty);
        string? final = await CreatedItemRenameAsync(pane, path, allowSubdirectories: false);
        if (final != null && File.Exists(final))
            ShellOperations.Edit(final);
    }

    /// <summary>"name.ext", or "name (2).ext", "name (3).ext"... - the first one that doesn't exist yet.</summary>
    private static string UniquePath(string directory, string name, string extension)
    {
        string path = Path.Combine(directory, name + extension);
        for (int i = 2; File.Exists(path) || Directory.Exists(path); i++)
            path = Path.Combine(directory, $"{name} ({i}){extension}");
        return path;
    }

    /// <summary>Focuses a just created item and starts the in-place rename on it. Returns its final path.</summary>
    private async Task<string?> CreatedItemRenameAsync(PaneViewModel pane, string path, bool allowSubdirectories)
    {
        string name = Path.GetFileName(path);
        await pane.NavigateAsync(pane.Path, name, addToHistory: false);
        if (pane.FocusedItem is not { } focused || !string.Equals(focused.Entry.Name, name, StringComparison.OrdinalIgnoreCase))
            await pane.NavigateAsync(pane.Path, name, addToHistory: false); // an auto refresh (after creating it) replaced that load
        // like Explorer, a name that can't be used (exists already, invalid) puts you back into the edit
        for (int attempt = 0; attempt < 5; attempt++)
        {
            FileItemViewModel? item = pane.FocusedItem;
            if (item == null || !string.Equals(item.Entry.Name, name, StringComparison.OrdinalIgnoreCase))
                return path; // created, but not visible (e.g. filtered out)
            string? final = await RenameItemAsync(pane, item, allowSubdirectories,
                selectWholeName: !item.Entry.IsDirectory && Settings.General.EditNewSelectsExtension);
            if (final != null)
                return final;
            await pane.NavigateAsync(pane.Path, name, addToHistory: false);
        }
        return path;
    }

    private async Task CopyOrMoveAsync(bool move)
    {
        var items = _active.GetOperationItems();
        if (items.Count == 0 || !_active.IsLocalDisk)
            return;

        string what = items.Count == 1 ? $"\"{items[0].Entry.Name}\"" : Loc.F("{0} items", items.Count);
        string defaultTarget = Inactive.IsLocalDisk ? Inactive.Path : string.Empty;
        if (defaultTarget.Length > 0 && !defaultTarget.EndsWith('\\'))
            defaultTarget += "\\";

        // "Don't show this dialog again": straight into the other panel, unless that isn't a usable
        // target (This PC, or the same directory) - then the dialog is shown anyway
        bool sameDirectory = string.Equals(Inactive.Path.TrimEnd('\\'), _active.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        string target;
        if (!Settings.AskCopyMoveTarget && Inactive.IsLocalDisk && !sameDirectory)
        {
            target = defaultTarget;
        }
        else
        {
            var answer = await _host.Dialogs.CopyMoveTargetAsync(move, what, defaultTarget);
            _host.FocusPane(_active);
            if (answer is not { } a)
                return;
            target = a.Target;
            if (a.DontAskAgain)
            {
                Settings.AskCopyMoveTarget = false;
                SaveSettings();
            }
        }
        bool endsWithSeparator = target.TrimEnd().EndsWith('\\');
        string resolved = PathUtil.Resolve(_active.Path, target);
        if (endsWithSeparator && !resolved.EndsWith('\\'))
            resolved += "\\"; // "target\" always means "into this directory"
        // Confirmations → "Do you want to create target path?"
        bool intoNewDirectory = (resolved.EndsWith('\\') || items.Count > 1) && !Directory.Exists(resolved);
        if (intoNewDirectory && Settings.Confirm.CreateTargetPath &&
            !await _host.ConfirmAsync(move ? "Move" : "Copy", $"The target directory \"{resolved.TrimEnd('\\')}\" doesn't exist. Do you want to create it?", "Create"))
            return;
        await TransferAsync(items.Select(i => i.Entry.FullPath).ToList(), resolved, move);
    }

    /// <summary>
    /// Copies or moves in the background (F5/F6, paste, drag &amp; drop): the operation is added to the
    /// transfer panel and this returns immediately, so the panels stay usable while it runs.
    /// </summary>
    public Task TransferAsync(IReadOnlyList<string> sources, string target, bool move)
    {
        if (sources.Count == 0)
            return Task.CompletedTask;
        // like Salamander, the marks are cleared once the operation is started; Ctrl+W brings them back
        _active.RememberSelection();
        _active.MarkAll(false);
        Transfers.Enqueue(move ? OperationKind.Move : OperationKind.Copy, sources, target,
            (engine, ct) => move ? engine.MoveAsync(sources, target, ct) : engine.CopyAsync(sources, target, ct));
        return Task.CompletedTask;
    }

    // ---- disk images ------------------------------------------------------------------------

    /// <summary>Enter on an .iso / .img / .vhd(x): mounts it (Configuration → General), else opens it with its default app.</summary>
    private async Task OpenDiskImageAsync(PaneViewModel pane, string path)
    {
        if (Settings.General.MountDiskImages)
        {
            await MountImageAsync(pane, path);
            return;
        }
        try
        {
            ShellOperations.Open(path, pane.Path);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _host.Notify(ex.Message);
        }
    }

    /// <summary>Mounts the image as a drive and opens the drive in the other panel.</summary>
    private async Task MountImageAsync(PaneViewModel pane, string path)
    {
        string name = Path.GetFileName(path);
        _host.Notify($"Mounting \"{name}\"...", isError: false);
        string root;
        try
        {
            root = await DiskImages.MountAsync(path);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException)
        {
            _host.Notify($"\"{name}\" could not be mounted: {ex.Message}");
            return;
        }
        for (int wait = 0; wait < 20 && !await Task.Run(() => IsReady(root)); wait++)
            await Task.Delay(250);
        PaneViewModel other = pane == Left ? Right : Left;
        await Task.WhenAll(Left.LoadDrivesAsync(), Right.LoadDrivesAsync());
        await other.NavigateAsync(root);
        _host.BringToFront(); // the shell's fallback may have opened an Explorer window
        Activate(other);
        _host.FocusPane(other);
        _host.Notify($"\"{name}\" is mounted as {root.TrimEnd('\\')}. Unmount it from the drive's context menu.", isError: false);
    }

    /// <summary>The drive to unmount: the focused drive (This PC), else the panel's own drive, when it is a mounted image.</summary>
    private static string? ImageDriveOf(PaneViewModel p)
    {
        string? root = p.FocusedItem?.Drive?.Root ?? (p.IsLocalDisk && p.Path.Length >= 2 && p.Path[1] == ':' ? p.Path[..2] + "\\" : null);
        return root != null && DiskImages.IsMountedImage(root) ? root : null;
    }

    /// <summary>Unmounts a mounted image; panels showing it go to This PC first (they would keep it busy).</summary>
    public async Task UnmountImageAsync(string root)
    {
        foreach (PaneViewModel pane in new[] { Left, Right })
            if (pane.IsLocalDisk && pane.Path.StartsWith(root[..2], StringComparison.OrdinalIgnoreCase))
                await pane.NavigateAsync(ThisPcProvider.RootPath, root[..2]);
        try
        {
            await DiskImages.UnmountAsync(root);
            _host.Notify($"{root.TrimEnd('\\')} is unmounted.", isError: false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException)
        {
            _host.Notify($"{root.TrimEnd('\\')} could not be unmounted: {ex.Message}");
        }
        await Task.WhenAll(Left.LoadDrivesAsync(), Right.LoadDrivesAsync());
        foreach (PaneViewModel pane in new[] { Left, Right })
            if (pane.IsThisPc)
                await pane.RefreshAsync();
    }

    // ---- drives plugged in and pulled out (live USB) ---------------------------------------------

    /// <summary>
    /// Drives appeared (Windows WM_DEVICECHANGE): the drive bars and This PC are updated, and a USB
    /// drive or memory card opens in the focused panel (Configuration → Drives). Mounted images and
    /// internal disks are not opened.
    /// </summary>
    public async Task OnDrivesArrivedAsync(IReadOnlyList<string> roots)
    {
        try
        {
            // a new drive needs a moment before it can be read
            for (int wait = 0; wait < 20 && !(await Task.Run(() => roots.All(IsReady))); wait++)
                await Task.Delay(250);
            await Task.WhenAll(Left.LoadDrivesAsync(), Right.LoadDrivesAsync());
            foreach (PaneViewModel pane in new[] { Left, Right })
                if (pane.IsThisPc)
                    await pane.RefreshAsync();

            if (!Settings.Drives.OpenPluggedInDrives)
                return;
            foreach (string root in roots)
            {
                if (!await Task.Run(() => IsReady(root) && DriveBus.IsPlugIn(root)))
                    continue;
                PaneViewModel target = _active;
                await target.NavigateAsync(root);
                _host.FocusPane(target);
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // a drive that came and went at once: nothing to show
        }
    }

    /// <summary>Drives are gone: panels showing them go to This PC, the drive bars and This PC are updated.</summary>
    public async Task OnDrivesRemovedAsync(IReadOnlyList<string> roots)
    {
        try
        {
            foreach (PaneViewModel pane in new[] { Left, Right })
            {
                if (pane.IsThisPc)
                    continue;
                string path = pane.Path;
                if (roots.Any(r => path.StartsWith(r[..2], StringComparison.OrdinalIgnoreCase)))
                    await pane.NavigateAsync(ThisPcProvider.RootPath);
            }
            await Task.WhenAll(Left.LoadDrivesAsync(), Right.LoadDrivesAsync());
            foreach (PaneViewModel pane in new[] { Left, Right })
                if (pane.IsThisPc)
                    await pane.RefreshAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // the next refresh shows the right state
        }
    }

    private static bool IsReady(string root)
    {
        try
        {
            return new DriveInfo(root).IsReady;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---- inside archives (the panel shows an archive's content) --------------------------------

    /// <summary>
    /// Commands that work differently in an archive: F5 / Alt+F6 copy the selected items out, F3 / F4
    /// open a temporary copy, and commands that would change the archive say it is read-only.
    /// Returns true when the command was handled here.
    /// </summary>
    private async Task<bool> ArchiveCommandAsync(PaneViewModel p, CommandId command)
    {
        switch (command)
        {
            case CommandId.Copy:
            case CommandId.Unpack:
                await CopyFromArchiveAsync(p);
                return true;
            case CommandId.View:
            case CommandId.Edit:
                if (p.FocusedItem is { Entry.IsDirectory: false, Entry.IsUpDir: false } item)
                    await OpenFromArchiveAsync(p, item);
                return true;
            case CommandId.Move:
            case CommandId.Delete:
            case CommandId.DeletePermanently:
            case CommandId.QuickRename:
            case CommandId.CreateDirectory:
            case CommandId.EditNewFile:
            case CommandId.ChangeAttributes:
            case CommandId.ChangeCase:
            case CommandId.ClipboardCut:
            case CommandId.ClipboardCopy:
            case CommandId.ClipboardPaste:
            case CommandId.Pack:
            case CommandId.NewMenu:
                string name = p.Archive is { } location ? Path.GetFileName(location.ArchivePath) : "The archive";
                _host.Notify($"\"{name}\" is open read-only: copy files out with F5, or unpack it with Alt+F6 from its folder.", isError: false);
                return true;
            default:
                return false;
        }
    }

    /// <summary>F5 in an archive: unpacks the selected files and directories into a directory (the other panel by default).</summary>
    private async Task CopyFromArchiveAsync(PaneViewModel p)
    {
        if (p.Archive is not { } location)
            return;
        var items = p.GetOperationItems().Where(i => !i.Entry.IsUpDir).ToList();
        if (items.Count == 0)
            return;
        string what = items.Count == 1 ? $"\"{items[0].Entry.Name}\"" : Loc.F("{0} items", items.Count);
        string defaultTarget = Inactive.IsLocalDisk ? Inactive.Path : string.Empty;
        if (defaultTarget.Length > 0 && !defaultTarget.EndsWith('\\'))
            defaultTarget += "\\";
        string target;
        if (!Settings.AskCopyMoveTarget && Inactive.IsLocalDisk)
        {
            target = defaultTarget;
        }
        else
        {
            var answer = await _host.Dialogs.CopyMoveTargetAsync(move: false, what, defaultTarget);
            _host.FocusPane(p);
            if (answer is not { } a)
                return;
            target = a.Target;
            if (a.DontAskAgain)
            {
                Settings.AskCopyMoveTarget = false;
                SaveSettings();
            }
        }
        string directory = PathUtil.Resolve(Inactive.IsLocalDisk ? Inactive.Path : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), target);
        if (ArchiveProvider.CanHandle(directory) || !Path.IsPathRooted(directory))
        {
            _host.Notify("Files can only be copied out of an archive into a directory on disk.");
            return;
        }
        if (!Directory.Exists(directory) && Settings.Confirm.CreateTargetPath &&
            !await _host.ConfirmAsync("Copy", $"The target directory \"{directory.TrimEnd('\\')}\" doesn't exist. Do you want to create it?", "Create"))
            return;

        var inner = items.Select(i => location.ItemPath(i.Entry.Name)).ToList();
        p.RememberSelection();
        p.MarkAll(false);
        Transfers.EnqueueJob(OperationKind.Copy, items.Select(i => i.Entry.FullPath).ToList(), directory, job =>
            Task.Run(() => location.Archiver.UnpackAsync(new UnpackRequest
            {
                ArchivePath = location.ArchivePath,
                TargetDirectory = directory,
                Items = inner,
                BasePath = location.InnerPath,
            }, job)));
    }

    /// <summary>Folder for files opened from archives (emptied when the app closes).</summary>
    public static string ArchiveTempRoot => Path.Combine(Path.GetTempPath(), "VibeSalamander", "FromArchives");

    /// <summary>Enter / F3 on a file in an archive: unpacks it into a temporary folder and opens it.</summary>
    private async Task OpenFromArchiveAsync(PaneViewModel p, FileItemViewModel item)
    {
        if (p.Archive is not { } location || item.Entry.IsDirectory)
            return;
        string directory = Path.Combine(ArchiveTempRoot, Guid.NewGuid().ToString("N")[..8]);
        string file = Path.Combine(directory, item.Entry.Name);
        string inner = location.ItemPath(item.Entry.Name);
        Transfers.EnqueueJob(OperationKind.Unpack, new[] { item.Entry.FullPath }, directory, async job =>
        {
            OperationResult result = await Task.Run(() => location.Archiver.UnpackAsync(new UnpackRequest
            {
                ArchivePath = location.ArchivePath,
                TargetDirectory = directory,
                Items = new[] { inner },
                BasePath = location.InnerPath,
            }, job));
            // back on the UI thread: open it with its program (Configuration → Viewers, else the default app)
            if (!result.Cancelled && File.Exists(file))
            {
                FileItemViewModel opened = new(new FileEntry(item.Entry.Name, file, false, item.Entry.Size, item.Entry.LastWrite, FileAttributes.ReadOnly), p.Columns);
                // the rule's $(FullName) / $(Path) must be the temporary copy, not the path inside the archive
                var context = new UserMenuContext
                {
                    Path = directory,
                    OtherPath = (p == Left ? Right : Left).IsLocalDisk ? (p == Left ? Right : Left).Path : string.Empty,
                    FullName = file,
                    Selected = new[] { file },
                };
                if (!RunProgramRule(Settings.Viewers, opened, context))
                    ShellOperations.Open(file, directory);
            }
            return result;
        });
    }

    /// <summary>At exit: removes the temporary copies of files opened from archives (best effort).</summary>
    public static void CleanArchiveTemp()
    {
        try
        {
            if (Directory.Exists(ArchiveTempRoot))
                Directory.Delete(ArchiveTempRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a file is still open in its program: it stays until the next time
        }
    }

    // ---- archives (Alt+F5 / Alt+F6, the archive plugin) -----------------------------------------

    /// <summary>Alt+F5: packs the marked items (or the focused one) into an archive, in the transfer panel.</summary>
    private async Task PackAsync()
    {
        var items = _active.GetOperationItems();
        if (items.Count == 0 || !_active.IsLocalDisk)
            return;
        IReadOnlyList<(IArchiver Archiver, ArchiveFormat Format)> formats = Plugins.PackFormats;
        if (formats.Count == 0)
        {
            _host.Notify("No plugin can pack: enable the Archives plugin in Plugins → Plugins Manager.");
            return;
        }
        PluginOptions o = Settings.Plugins;
        ArchiveFormat format = formats.FirstOrDefault(f => string.Equals(f.Format.Id, o.PackFormat, StringComparison.OrdinalIgnoreCase)).Format
                               ?? formats[0].Format;

        // "photos.zip" for one item, the directory's name for several; into the other panel like F5
        string baseName = items.Count == 1
            ? (items[0].Entry.IsDirectory ? items[0].Entry.Name : Path.GetFileNameWithoutExtension(items[0].Entry.Name))
            : Path.GetFileName(_active.Path.TrimEnd('\\'));
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = _active.Path.Length >= 1 ? _active.Path[..1] : "Archive"; // a drive root: "C"
        string directory = Inactive.IsLocalDisk ? Inactive.Path : _active.Path;
        string what = items.Count == 1 ? $"\"{items[0].Entry.Name}\"" : Loc.F("{0} items", items.Count);

        var answer = await _host.Dialogs.PackAsync(what, Path.Combine(directory, baseName + format.Extension),
            formats.Select(f => f.Format).ToList(), format.Id, o.PackCompression, o.PackDeleteSources);
        _host.FocusPane(_active);
        if (answer == null)
            return;
        (IArchiver archiver, ArchiveFormat chosen) = formats.First(f => f.Format.Id == answer.FormatId);
        string archivePath = PathUtil.Resolve(_active.Path, answer.ArchivePath);
        if (answer.ArchivePath.TrimEnd().EndsWith('\\') || Directory.Exists(archivePath))
            archivePath = Path.Combine(archivePath, baseName + chosen.Extension); // only a directory was given
        else if (!PluginManager.HasArchiveExtension(archivePath, chosen))
            archivePath += chosen.Extension;
        o.PackFormat = chosen.Id;
        o.PackCompression = answer.Compression;
        o.PackDeleteSources = answer.DeleteSources;
        SaveSettings();

        var sources = items.Select(i => i.Entry.FullPath).ToList();
        string baseDirectory = _active.Path;
        var compression = Enum.TryParse(answer.Compression, out ArchiveCompression c) ? c : ArchiveCompression.Normal;
        bool deleteSources = answer.DeleteSources;
        _active.RememberSelection();
        _active.MarkAll(false);
        Transfers.EnqueueJob(OperationKind.Pack, sources, archivePath, async job =>
        {
            // on a worker thread: a plugin that blocks on the job's questions must not block the UI thread
            OperationResult result = await Task.Run(() => archiver.PackAsync(new PackRequest
            {
                Sources = sources,
                BaseDirectory = baseDirectory,
                ArchivePath = archivePath,
                FormatId = chosen.Id,
                Compression = compression,
            }, job));
            // move to archive: only when everything went in (and never when the archive is inside a packed directory)
            if (deleteSources && !result.Cancelled && result.Failed == 0 && result.Skipped == 0 &&
                !sources.Any(src => archivePath.StartsWith(src.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
            {
                var engine = new FileOperationEngine(new JobCallbacks(job));
                OperationResult deleted = await engine.DeleteAsync(sources, job.Cancellation);
                result.Failed += deleted.Failed;
            }
            return result;
        });
    }

    /// <summary>Asks the plugin job's questions for the copy engine (deleting after "move to archive").</summary>
    private sealed class JobCallbacks : IOperationCallbacks
    {
        private readonly IArchiveJob _job;

        public JobCallbacks(IArchiveJob job) => _job = job;

        public Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict) => _job.ResolveConflictAsync(conflict);

        public Task<ErrorChoice> ResolveErrorAsync(OperationError error) => _job.ResolveErrorAsync(error);
    }

    /// <summary>Alt+F6: unpacks the marked archives (or the focused one), each in its own transfer row.</summary>
    private async Task UnpackAsync()
    {
        var items = _active.GetOperationItems();
        if (items.Count == 0 || !_active.IsLocalDisk)
            return;
        var archives = items.Where(i => !i.Entry.IsDirectory)
                            .Select(i => (Item: i, Archiver: Plugins.FindUnpacker(i.Entry.FullPath)))
                            .Where(x => x.Archiver != null)
                            .ToList();
        if (archives.Count == 0)
        {
            _host.Notify(items.Count == 1
                ? $"\"{items[0].Entry.Name}\" is not an archive Vibe Salamander can unpack."
                : "None of the selected files is an archive Vibe Salamander can unpack.");
            return;
        }
        PluginOptions o = Settings.Plugins;
        string defaultTarget = Inactive.IsLocalDisk ? Inactive.Path : _active.Path;
        if (!defaultTarget.EndsWith('\\'))
            defaultTarget += "\\";
        string what = archives.Count == 1 ? $"\"{archives[0].Item.Entry.Name}\"" : Loc.F("{0} archives", archives.Count);
        string example = archives.Count == 1 ? Plugins.ArchiveBaseName(archives[0].Item.Entry.Name) + "\\" : "name\\";
        var answer = await _host.Dialogs.UnpackAsync(what, defaultTarget, o.UnpackMask, o.UnpackToSubfolder, example);
        _host.FocusPane(_active);
        if (answer == null)
            return;
        o.UnpackMask = answer.Mask;
        o.UnpackToSubfolder = answer.ToSubfolder;
        SaveSettings();

        string target = PathUtil.Resolve(_active.Path, answer.Target);
        if (!Directory.Exists(target) && Settings.Confirm.CreateTargetPath &&
            !await _host.ConfirmAsync("Unpack", $"The target directory \"{target.TrimEnd('\\')}\" doesn't exist. Do you want to create it?", "Create"))
            return;
        if (archives.Count > 1)
        {
            _active.RememberSelection();
            _active.MarkAll(false);
        }
        foreach (var (item, archiver) in archives)
        {
            string archivePath = item.Entry.FullPath;
            string directory = answer.ToSubfolder ? Path.Combine(target, Plugins.ArchiveBaseName(item.Entry.Name)) : target;
            string mask = answer.Mask;
            Transfers.EnqueueJob(OperationKind.Unpack, new[] { archivePath }, directory, job =>
                Task.Run(() => archiver!.UnpackAsync(new UnpackRequest { ArchivePath = archivePath, TargetDirectory = directory, Mask = mask }, job)));
        }
    }

    private async Task DeleteAsync(bool permanent)
    {
        var items = _active.GetOperationItems();
        if (items.Count == 0 || !_active.IsLocalDisk)
            return;

        // Configuration → Recycle Bin: what F8 does with each item (Shift+Del is always permanent)
        var recycle = new List<FileItemViewModel>();
        var remove = new List<FileItemViewModel>();
        if (permanent || Settings.Recycle.Mode == DeleteMode.DeletePermanently)
        {
            remove.AddRange(items);
        }
        else if (Settings.Recycle.Mode == DeleteMode.RecycleMatching)
        {
            var mask = new WildcardMask(Settings.Recycle.Masks);
            foreach (FileItemViewModel item in items)
                (item.Entry.IsDirectory || mask.IsMatch(item.Entry.Name) ? recycle : remove).Add(item);
        }
        else
        {
            recycle.AddRange(items);
        }

        // whole sentences for one file, one directory or several items (so they translate well)
        static string Ask(IReadOnlyList<FileItemViewModel> list, string file, string directory, string many) => list.Count == 1
            ? Loc.F(list[0].Entry.IsDirectory ? directory : file, list[0].Entry.Name)
            : Loc.F(many, list.Count);

        // Configuration → Confirmations
        if (recycle.Count > 0 && Settings.ConfirmRecycle &&
            !await _host.ConfirmAsync("Delete", Ask(recycle,
                "Do you want to move the file \"{0}\" to the Recycle Bin?",
                "Do you want to move the directory \"{0}\" to the Recycle Bin?",
                "Do you want to move the {0} selected items to the Recycle Bin?"), "Delete"))
            return;
        if (remove.Count > 0)
        {
            if (Settings.Confirm.PermanentDelete &&
                !await _host.ConfirmAsync("Delete", Ask(remove,
                    "Do you want to permanently delete the file \"{0}\"?\nThis cannot be undone.",
                    "Do you want to permanently delete the directory \"{0}\"?\nThis cannot be undone.",
                    "Do you want to permanently delete the {0} selected items?\nThis cannot be undone."), "Delete"))
                return;
            var hiddenFiles = remove.Where(i => !i.Entry.IsDirectory && (i.Entry.IsHidden || i.Entry.IsSystem)).ToList();
            if (hiddenFiles.Count > 0 && Settings.Confirm.HiddenSystemFileDelete &&
                !await _host.ConfirmAsync("Delete", Ask(hiddenFiles,
                    "The file \"{0}\" is a hidden or system file. Delete it anyway?",
                    "The file \"{0}\" is a hidden or system file. Delete it anyway?",
                    "{0} of the files are hidden or system files. Delete them anyway?"), "Delete"))
                return;
            var hiddenDirs = remove.Where(i => i.Entry.IsDirectory && (i.Entry.IsHidden || i.Entry.IsSystem)).ToList();
            if (hiddenDirs.Count > 0 && Settings.Confirm.HiddenSystemDirectoryDelete &&
                !await _host.ConfirmAsync("Delete", Ask(hiddenDirs,
                    "The directory \"{0}\" is a hidden or system directory. Delete it anyway?",
                    "The directory \"{0}\" is a hidden or system directory. Delete it anyway?",
                    "{0} of the directories are hidden or system directories. Delete them anyway?"), "Delete"))
                return;
            if (Settings.Confirm.NonEmptyDirectoryDelete)
            {
                var nonEmpty = remove.Where(i => i.Entry.IsDirectory && HasContent(i.Entry.FullPath)).ToList();
                if (nonEmpty.Count > 0 &&
                    !await _host.ConfirmAsync("Delete", nonEmpty.Count == 1
                        ? $"The directory \"{nonEmpty[0].Entry.Name}\" is not empty. Delete it with all its content?"
                        : $"{nonEmpty.Count} directories are not empty. Delete them with all their content?", "Delete All"))
                    return;
            }
        }

        int caret = _active.CaretIndex;
        _active.RememberSelection();
        if (remove.Count > 0)
        {
            // runs in the transfer panel; the panels refresh when it finishes
            _active.MarkAll(false);
            var paths = remove.Select(i => i.Entry.FullPath).ToList();
            Transfers.Enqueue(OperationKind.Delete, paths, null, (engine, ct) => engine.DeleteAsync(paths, ct));
        }
        if (recycle.Count > 0)
        {
            // the Recycle Bin is only reachable through the shell (it shows its own progress window)
            await ShellOperations.DeleteAsync(_host.WindowHandle, recycle.Select(i => i.Entry.FullPath).ToList(), permanent: false);
            await RefreshAllAsync();
            CaretMoveRequested?.Invoke(this, (_active, Math.Min(caret, Math.Max(0, _active.Items.Count - 1))));
        }
    }

    private static bool HasContent(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Confirmations → Drag and Drop operations.</summary>
    public async Task<bool> ConfirmDropAsync(IReadOnlyList<string> paths, string target, bool move)
    {
        if (!Settings.Confirm.DragAndDrop)
            return true;
        string what = paths.Count == 1 ? $"\"{Path.GetFileName(paths[0].TrimEnd('\\'))}\"" : Loc.F("{0} items", paths.Count);
        return await _host.ConfirmAsync(move ? "Move" : "Copy",
            Loc.F(move ? "Do you want to move {0} to {1}?" : "Do you want to copy {0} to {1}?", what, target), move ? "Move" : "Copy");
    }

    /// <summary>Confirmations → "The directory doesn't exist. Do you want to create it?" Creates it when confirmed.</summary>
    public async Task<bool> CreateMissingDirectoryAsync(string path)
    {
        if (!Settings.Confirm.CreateMissingDirectory)
            return true; // navigating there shows the error
        if (!await _host.ConfirmAsync("Change Directory", $"The directory \"{path}\" doesn't exist. Do you want to create it?", "Create"))
            return false;
        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _host.Notify(ex.Message);
            return false;
        }
    }

    /// <summary>F2: rename in place (Salamander quick rename).</summary>
    private async Task RenameAsync()
    {
        FileItemViewModel? item = _active.FocusedItem;
        if (item == null || item.Entry.IsUpDir || item.IsDrive || !_active.IsLocalDisk)
            return;
        await RenameItemAsync(_active, item, allowSubdirectories: false, selectWholeName: Settings.General.RenameSelectsExtension);
    }

    /// <summary>
    /// Edits the item's name in its row and renames it. Problems are reported with a notification, not a
    /// dialog. With <paramref name="allowSubdirectories"/> (a new folder) a typed "a\b" creates the path.
    /// Returns the item's path afterwards (unchanged when cancelled), or null when the rename failed.
    /// </summary>
    private async Task<string?> RenameItemAsync(PaneViewModel pane, FileItemViewModel item, bool allowSubdirectories, bool selectWholeName = false)
    {
        string oldPath = item.Entry.FullPath;
        string? newName = await _host.EditNameInPlaceAsync(pane, item, selectWholeName);
        newName = newName?.Trim().TrimEnd('.', ' ');
        if (string.IsNullOrEmpty(newName) || newName == item.Entry.Name)
            return oldPath;

        bool nested = allowSubdirectories && item.Entry.IsDirectory && newName.IndexOfAny(new[] { '\\', '/' }) >= 0;
        string target;
        if (nested)
        {
            target = PathUtil.Resolve(pane.Path, newName);
        }
        else if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            _host.Notify($"\"{newName}\" contains characters that are not allowed in file names (\\ / : * ? \" < > |).");
            return null;
        }
        else
        {
            target = Path.Combine(pane.Path, newName);
        }

        try
        {
            bool caseOnly = string.Equals(target, oldPath, StringComparison.OrdinalIgnoreCase);
            if (item.Entry.IsDirectory)
            {
                if (nested)
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (caseOnly)
                {
                    string temp = oldPath + ".~rename";
                    Directory.Move(oldPath, temp);
                    Directory.Move(temp, target);
                }
                else
                {
                    Directory.Move(oldPath, target);
                }
            }
            else
            {
                File.Move(oldPath, target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _host.Notify(ex.Message);
            return null;
        }

        // focus the renamed item (for a nested path: the first new directory in this panel)
        string relative = Path.GetRelativePath(pane.Path, target);
        string focus = relative.Split(Path.DirectorySeparatorChar)[0];
        await pane.NavigateAsync(pane.Path, focus, addToHistory: false);
        return target;
    }

    // ---- phase 3B: commands --------------------------------------------------------------

    private const int MaxRecent = 30;

    private static void RememberRecent(List<string> list, string path, int max = MaxRecent)
    {
        if (string.IsNullOrEmpty(path) || path.StartsWith(ThisPcProvider.RootPath, StringComparison.OrdinalIgnoreCase))
            return;
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        if (list.Count > max)
            list.RemoveRange(max, list.Count - max);
    }

    /// <summary>Configuration → History: how many working directories are kept (Alt+F12).</summary>
    public int WorkingDirectoriesLimit => Math.Clamp(Settings.History.WorkingDirectoriesLimit, 1, 100);

    /// <summary>Applies a lowered limit to the list at once.</summary>
    public void TrimWorkingDirectories()
    {
        int max = WorkingDirectoriesLimit;
        if (Settings.WorkingDirectories.Count > max)
            Settings.WorkingDirectories.RemoveRange(max, Settings.WorkingDirectories.Count - max);
    }

    private static DirectoryComparer.Item ToCompareItem(FileItemViewModel i) =>
        new(i.Entry.Name, i.Entry.IsDirectory, i.Entry.Size, i.Entry.LastWrite);

    /// <summary>Ctrl+F10: marks, in both panels, what is missing on the other side or differs.</summary>
    private async Task CompareDirectoriesAsync()
    {
        if (!Left.IsLocalDisk || !Right.IsLocalDisk)
        {
            _host.Notify("Compare Directories needs a directory in both panels.", isError: false);
            return;
        }
        if (string.Equals(Left.Path.TrimEnd('\\'), Right.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            _host.Notify("Both panels show the same directory.", isError: false);
            return;
        }
        CompareOptions? options = await _host.Dialogs.CompareOptionsAsync(Settings.Compare);
        if (options == null)
            return;
        options.ToleranceSeconds = Settings.General.CompareTimeTolerance;
        options.IgnoreDstHours = Settings.General.CompareIgnoreDstHours;
        Settings.Compare = options;

        var left = Left.Items.Where(i => !i.Entry.IsUpDir).Select(ToCompareItem).ToList();
        var right = Right.Items.Where(i => !i.Entry.IsUpDir).Select(ToCompareItem).ToList();
        CompareResult result = await DirectoryComparer.CompareAsync(Left.Path, left, Right.Path, right, options);
        int inLeft = Left.MarkNames(result.Left);
        int inRight = Right.MarkNames(result.Right);
        _host.Notify(result.Identical
            ? "The directories are identical."
            : $"Differences marked: {inLeft} in the left panel, {inRight} in the right panel.", isError: false);
    }

    private UserMenuContext UserMenuContextFor(PaneViewModel pane)
    {
        PaneViewModel other = pane == Left ? Right : Left;
        FileItemViewModel? focused = pane.FocusedItem;
        return new UserMenuContext
        {
            Path = pane.IsLocalDisk ? pane.Path : string.Empty,
            OtherPath = other.IsLocalDisk ? other.Path : string.Empty,
            FullName = focused != null && !focused.Entry.IsUpDir && !focused.IsDrive ? focused.Entry.FullPath : string.Empty,
            Selected = pane.GetOperationItems().Select(i => i.Entry.FullPath).ToList(),
        };
    }

    /// <summary>F9: the user menu at the caret.</summary>
    private void ShowUserMenu(PaneViewModel pane)
    {
        var entries = new List<MenuEntry>();
        foreach (UserMenuItem item in Settings.UserMenu)
        {
            if (item.IsSeparator)
            {
                entries.Add(MenuEntry.Separator);
                continue;
            }
            UserMenuItem command = item;
            entries.Add(new MenuEntry(item.Name, () => RunUserMenuItem(pane, command), "\uE756"));
        }
        if (entries.Count > 0)
            entries.Add(MenuEntry.Separator);
        entries.Add(new MenuEntry(Loc.T("Edit User Menu..."), async () => await EditUserMenuAsync(), "\uE70F"));
        _host.ShowMenu(pane, entries, atCaret: true);
    }

    /// <summary>Runs a user menu command for the active panel (user menu bar).</summary>
    public void RunUserMenuItem(UserMenuItem item) => RunUserMenuItem(_active, item);

    /// <summary>Raised when hot paths or the user menu changed (the window rebuilds its bars).</summary>
    public event EventHandler? QuickBarsChanged;

    private void RunUserMenuItem(PaneViewModel pane, UserMenuItem item)
    {
        UserMenuContext context = UserMenuContextFor(pane);
        string command = UserMenu.Expand(item.Command, context).Trim();
        string arguments = UserMenu.Expand(item.Arguments, context);
        string directory = UserMenu.Expand(string.IsNullOrWhiteSpace(item.InitialDirectory) ? "$(Path)" : item.InitialDirectory, context);
        if (command.Length == 0)
            return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(command, arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = Directory.Exists(directory) ? directory : string.Empty,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            _host.Notify($"{item.Name}: {ex.Message}");
        }
    }

    private async Task EditUserMenuAsync()
    {
        List<UserMenuItem>? edited = await _host.Dialogs.EditUserMenuAsync(Settings.UserMenu);
        if (edited == null)
            return;
        Settings.UserMenu = edited;
        SaveSettings();
        QuickBarsChanged?.Invoke(this, EventArgs.Empty);
    }

    private List<string> _savedSelection = new();

    /// <summary>Ctrl+Shift+F5: remembers the marked names (for any directory).</summary>
    private void SaveSelection(PaneViewModel pane)
    {
        var names = pane.MarkedItems.Select(i => i.Entry.Name).ToList();
        if (names.Count == 0)
        {
            _host.Notify("Nothing is selected.", isError: false);
            return;
        }
        _savedSelection = names;
        _host.Notify(Loc.F("Selection saved: {0} names (Ctrl+Shift+F6 loads it).", names.Count), isError: false);
    }

    /// <summary>Ctrl+Shift+F6: marks the saved names in the active panel.</summary>
    private void LoadSelection(PaneViewModel pane)
    {
        if (_savedSelection.Count == 0)
        {
            _host.Notify("No selection is saved. Ctrl+Shift+F5 saves the selected names.", isError: false);
            return;
        }
        int found = pane.MarkNames(_savedSelection);
        _host.Notify(Loc.F("Selected {0} of {1} saved names.", found, _savedSelection.Count), isError: false);
    }

    private void HideNames(PaneViewModel pane, bool selected)
    {
        int count = pane.HideNames(selected);
        if (count > 0)
            _host.Notify(Loc.F("Names hidden: {0}. Edit → Show Hidden Names brings them back.", count), isError: false);
    }

    private void ShowHiddenNames(PaneViewModel pane)
    {
        if (pane.ShowHiddenNames() == 0)
            _host.Notify("No names are hidden in this panel.", isError: false);
    }

    /// <summary>Ctrl+M: copies a list of the selected items (or of all items) with their details to the clipboard.</summary>
    private void MakeFileList(PaneViewModel pane)
    {
        IReadOnlyList<FileItemViewModel> items = pane.MarkedItems.Count > 0
            ? pane.MarkedItems
            : pane.Items.Where(i => !i.Entry.IsUpDir).ToList();
        if (items.Count == 0)
            return;
        var sb = new System.Text.StringBuilder();
        foreach (FileItemViewModel item in items)
        {
            FileEntry e = item.Entry;
            string size = e.IsDirectory ? (item.DirectorySize is long d ? d.ToString(System.Globalization.CultureInfo.InvariantCulture) : "<DIR>")
                                        : e.Size.ToString(System.Globalization.CultureInfo.InvariantCulture);
            sb.Append(e.Name).Append('\t').Append(size).Append('\t')
              .Append(Formats.Date(e.LastWrite)).Append('\t').Append(Formats.Time(e.LastWrite)).Append('\t')
              .Append(Formats.Attributes(e.Attributes)).AppendLine();
        }
        CopyText(sb.ToString());
        _host.Notify(Loc.F("File list copied to the clipboard: {0} items (tab separated: name, size, date, time, attributes).", items.Count), isError: false);
    }

    /// <summary>Ctrl+S: shortcuts (.lnk) to the files on the clipboard, created in the active panel.</summary>
    private async Task PasteShortcutsAsync(PaneViewModel pane)
    {
        if (!pane.IsLocalDisk)
            return;
        var (paths, _) = await FileClipboard.GetFilesAsync();
        if (paths.Count == 0)
        {
            _host.Notify("There are no files on the clipboard.", isError: false);
            return;
        }
        string? last = null;
        int failed = 0;
        foreach (string target in paths)
        {
            string name = Path.GetFileName(target.TrimEnd('\\'));
            if (string.IsNullOrEmpty(name))
                name = target.TrimEnd('\\').Replace(":", string.Empty); // a drive
            string link = UniquePath(pane.Path, name + " - Shortcut", ".lnk");
            try
            {
                ShellLink.Create(target, link);
                last = link;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }
        if (last != null)
            await pane.NavigateAsync(pane.Path, Path.GetFileName(last), addToHistory: false);
        if (failed > 0)
            _host.Notify(Loc.F("Shortcuts that could not be created: {0}.", failed));
    }

    /// <summary>Files → New: the Explorer "New" menu; the created item is then renamed in place.</summary>
    private async Task NewFromShellMenuAsync(PaneViewModel pane)
    {
        if (!pane.IsLocalDisk)
            return;
        var before = new HashSet<string>(SafeEntries(pane.Path), StringComparer.OrdinalIgnoreCase);
        if (!_host.ShowNewMenu(pane))
            return;
        // the shell creates the item asynchronously: wait for it to appear
        for (int i = 0; i < 30; i++)
        {
            string? created = SafeEntries(pane.Path).FirstOrDefault(e => !before.Contains(e));
            if (created != null)
            {
                if (created.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    await pane.NavigateAsync(pane.Path, Path.GetFileName(created), addToHistory: false); // the Create Shortcut wizard names it
                else
                    await CreatedItemRenameAsync(pane, created, allowSubdirectories: Directory.Exists(created));
                return;
            }
            await Task.Delay(100);
        }
        await pane.RefreshAsync();
    }

    private static IEnumerable<string> SafeEntries(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private async Task NtfsAsync(PaneViewModel pane, NtfsAction action)
    {
        var items = pane.GetOperationItems();
        if (items.Count == 0 || !pane.IsLocalDisk)
            return;
        bool compress = action is NtfsAction.Compress or NtfsAction.Uncompress;
        if (compress ? Settings.Confirm.NtfsCompress : Settings.Confirm.NtfsEncrypt)
        {
            string what = items.Count == 1 ? $"\"{items[0].Entry.Name}\"" : Loc.F("{0} selected items", items.Count);
            bool hasDirectories = items.Any(i => i.Entry.IsDirectory);
            string question = (action, hasDirectories) switch
            {
                (NtfsAction.Compress, false) => "Do you want to compress {0}?",
                (NtfsAction.Compress, true) => "Do you want to compress {0} including the content of directories?",
                (NtfsAction.Uncompress, false) => "Do you want to uncompress {0}?",
                (NtfsAction.Uncompress, true) => "Do you want to uncompress {0} including the content of directories?",
                (NtfsAction.Encrypt, false) => "Do you want to encrypt {0}?",
                (NtfsAction.Encrypt, true) => "Do you want to encrypt {0} including the content of directories?",
                (_, false) => "Do you want to decrypt {0}?",
                _ => "Do you want to decrypt {0} including the content of directories?",
            };
            string button = action switch
            {
                NtfsAction.Compress => "Compress",
                NtfsAction.Uncompress => "Uncompress",
                NtfsAction.Encrypt => "Encrypt",
                _ => "Decrypt",
            };
            if (!await _host.ConfirmAsync("NTFS", Loc.F(question, what), button))
                return;
        }
        var (ok, failed, firstError) = await NtfsOperations.ApplyAsync(items.Select(i => i.Entry.FullPath).ToList(), action);
        await pane.RefreshAsync();
        string done = action switch
        {
            NtfsAction.Compress => "Items compressed: {0}.",
            NtfsAction.Uncompress => "Items uncompressed: {0}.",
            NtfsAction.Encrypt => "Items encrypted: {0}.",
            _ => "Items decrypted: {0}.",
        };
        if (failed == 0)
            _host.Notify(Loc.F(done, ok), isError: false);
        else
            _host.Notify(Loc.F(done, ok) + " " + Loc.F("Failed: {0}. {1}", failed, firstError));
    }

    private void ShowSecurity(PaneViewModel pane)
    {
        var items = pane.GetOperationItems();
        string? path = items.Count > 0 ? items[0].Entry.FullPath : pane.FocusedItem?.Drive?.Root;
        if (path != null)
            ShellOperations.ShowSecurity(_host.WindowHandle, path);
    }

    /// <summary>Ctrl+E: a new e-mail with the selected files attached.</summary>
    private async Task EmailAsync(PaneViewModel pane)
    {
        var files = pane.GetOperationItems().Where(i => !i.Entry.IsDirectory).Select(i => i.Entry.FullPath).ToList();
        if (files.Count == 0)
        {
            _host.Notify("Select the files to send (directories can't be attached).", isError: false);
            return;
        }
        if (Settings.Confirm.Email)
        {
            long total = pane.GetOperationItems().Where(i => !i.Entry.IsDirectory).Sum(i => i.Entry.Size);
            string what = files.Count == 1 ? $"\"{Path.GetFileName(files[0])}\"" : $"{files.Count} files";
            if (!await _host.ConfirmAsync("Email", $"Do you want to email {what}?\nTotal size: {Formats.ShortSize(total)}", "Email"))
                return;
        }
        MapiMail.Result result = await MapiMail.SendFilesAsync(_host.WindowHandle, files);
        if (result is MapiMail.Result.NoMailProgram or MapiMail.Result.Failed)
            _host.Notify("No e-mail program that accepts attachments (Simple MAPI) is set as the default mail app.");
    }

    /// <summary>Alt+F11: recently opened files.</summary>
    private void ShowOpenedFiles(PaneViewModel pane)
    {
        var entries = new List<MenuEntry> { MenuEntry.Header("Opened files") };
        foreach (string file in Settings.OpenedFiles.Take(MaxRecent))
        {
            string path = file;
            entries.Add(new MenuEntry(path, async () =>
            {
                string? dir = Path.GetDirectoryName(path);
                if (dir != null && Directory.Exists(dir))
                    await pane.NavigateAsync(dir, Path.GetFileName(path));
                if (File.Exists(path))
                {
                    ShellOperations.Open(path, dir);
                    RememberRecent(Settings.OpenedFiles, path);
                }
                else
                {
                    _host.Notify($"{path} no longer exists.");
                }
            }, "\uE8A5"));
        }
        if (entries.Count == 1)
            entries.Add(new MenuEntry("(no files opened yet)", IsEnabled: false));
        _host.ShowMenu(pane, entries, atCaret: false);
    }

    /// <summary>Alt+F12: recently visited directories (both panels).</summary>
    private void ShowWorkingDirectories(PaneViewModel pane)
    {
        var entries = new List<MenuEntry> { MenuEntry.Header("Working directories") };
        foreach (string directory in Settings.WorkingDirectories.Take(WorkingDirectoriesLimit))
        {
            string path = directory;
            entries.Add(new MenuEntry(path, async () => await pane.NavigateAsync(path), "\uE8B7"));
        }
        if (entries.Count == 1)
            entries.Add(new MenuEntry("(none yet)", IsEnabled: false));
        _host.ShowMenu(pane, entries, atCaret: false);
    }

    /// <summary>Ctrl+Shift+F9: the shared directories of this computer.</summary>
    private async Task ShowSharesAsync(PaneViewModel pane)
    {
        IReadOnlyList<NetworkDrives.Share> shares = await NetworkDrives.GetLocalSharesAsync();
        var entries = new List<MenuEntry> { MenuEntry.Header($"Shared on {Environment.MachineName}") };
        foreach (NetworkDrives.Share share in shares)
        {
            string unc = share.UncPath;
            string text = share.Remark.Length > 0 ? $"{share.Name}  —  {share.Remark}" : share.Name;
            entries.Add(new MenuEntry(text, async () => await pane.NavigateAsync(unc), "\uE8CE"));
        }
        if (shares.Count == 0)
            entries.Add(new MenuEntry("(no shared directories)", IsEnabled: false));
        _host.ShowMenu(pane, entries, atCaret: false);
    }

    /// <summary>Commands → Open Folder: special folders (file system ones open in the panel, the others in Explorer).</summary>
    public async Task OpenSpecialFolderAsync(string key)
    {
        string? path = key switch
        {
            "Desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "Documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Downloads" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            "Pictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "Music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "Videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "UserProfile" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Templates" => Environment.GetFolderPath(Environment.SpecialFolder.Templates),
            "SendTo" => Environment.GetFolderPath(Environment.SpecialFolder.SendTo),
            "StartMenu" => Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs" => Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            "Startup" => Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            "Recent" => Environment.GetFolderPath(Environment.SpecialFolder.Recent),
            "Temp" => Path.GetTempPath(),
            "Computer" => ThisPcProvider.RootPath,
            _ => null,
        };
        if (path != null)
        {
            await _active.NavigateAsync(path);
            _host.FocusPane(_active);
            return;
        }
        string? shell = key switch
        {
            "ControlPanel" => "shell:ControlPanelFolder",
            "Network" => "shell:NetworkPlacesFolder",
            "RecycleBin" => "shell:RecycleBinFolder",
            "Fonts" => "shell:Fonts",
            "Printers" => "shell:PrintersFolder",
            _ => null,
        };
        if (shell != null)
            ShellOperations.OpenInExplorer(shell);
    }

    // ---- phase 3 helpers -------------------------------------------------------------------

    /// <summary>Alt+F10 (selection) / Ctrl+Shift+F10 (all directories): sizes go into the Size column, the total into a notification.</summary>
    private async Task CalculateOccupiedSpaceAsync(PaneViewModel pane, IReadOnlyList<FileItemViewModel> items)
    {
        if (!pane.IsLocalDisk || items.Count == 0)
            return;
        OccupiedSpace total = await pane.CalculateSizesAsync(items);
        _host.Notify(Loc.F("Occupied space: {0} ({1} bytes) in {2} files and {3} directories",
                     Formats.ShortSize(total.Bytes), Formats.ExactSize(total.Bytes), total.Files.ToString("N0"), total.Directories.ToString("N0")),
                     isError: false);
    }

    private async Task GoToHotPathAsync(int number)
    {
        string path = Settings.HotPaths[number];
        if (string.IsNullOrEmpty(path))
        {
            _host.Notify($"Hot path {number} isn't set. Ctrl+Shift+{number} makes the current folder hot path {number}.", isError: false);
            return;
        }
        await _active.NavigateAsync(path);
    }

    private void SetHotPath(int number)
    {
        string path = _active.IsThisPc ? ThisPcProvider.RootPath : _active.Path;
        Settings.HotPaths[number] = path;
        SaveSettings();
        QuickBarsChanged?.Invoke(this, EventArgs.Empty);
        _host.Notify($"Hot path {number}: {(_active.IsThisPc ? "This PC" : path)}  (Ctrl+{number} goes there)", isError: false);
    }

    // ---- phase 2 helpers -------------------------------------------------------------------

    private Task ReportProblemsAsync(OperationResult? result)
    {
        if (result != null && result.Failed > 0)
            _host.Notify(Loc.F("Items that could not be processed: {0}.", result.Failed));
        return Task.CompletedTask;
    }

    public Task ShowErrorAsync(string message)
    {
        _host.Notify(message);
        return Task.CompletedTask;
    }

    private Task RefreshAllAsync() => Task.WhenAll(Left.RefreshAsync(), Right.RefreshAsync());

    private async Task FilterAsync()
    {
        string? mask = await _host.Dialogs.FilterAsync(_active.Filter);
        if (mask != null)
            _active.Filter = mask;
    }

    private void OpenWith()
    {
        FileItemViewModel? item = _active.FocusedItem;
        if (item != null && !item.Entry.IsDirectory)
            ShellOperations.OpenWith(_host.WindowHandle, item.Entry.FullPath);
    }

    private async Task ChangeAttributesAsync()
    {
        var items = _active.GetOperationItems();
        if (items.Count == 0)
            return;
        AttributeChange? change = await _host.Dialogs.AttributesAsync(items.Select(i => i.Entry).ToList());
        if (change == null)
            return;
        var result = await NameAndAttributeOperations.ChangeAttributesAsync(items.Select(i => i.Entry.FullPath).ToList(), change, _callbacks);
        await _active.RefreshAsync();
        await ReportProblemsAsync(result);
    }

    private async Task ChangeCaseAsync()
    {
        var items = _active.GetOperationItems();
        if (items.Count == 0)
            return;
        var options = await _host.Dialogs.ChangeCaseAsync(items.Any(i => i.Entry.IsDirectory));
        if (options is not { } o)
            return;
        var result = await NameAndAttributeOperations.ChangeCaseAsync(items.Select(i => i.Entry.FullPath).ToList(),
            o.Mode, o.Part, o.Recursive, _callbacks);
        await _active.RefreshAsync();
        await ReportProblemsAsync(result);
    }

    private async Task GoToShortcutTargetAsync()
    {
        FileItemViewModel? item = _active.FocusedItem;
        if (item == null || item.Entry.IsDirectory)
            return;
        string? target = ShellLink.GetTarget(item.Entry.FullPath);
        if (string.IsNullOrEmpty(target))
        {
            _host.Notify("The focused item is not a shortcut, or its target cannot be resolved.", isError: false);
            return;
        }
        if (Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) && !uri.IsFile)
        {
            ShellOperations.Open(target); // Internet shortcut
            return;
        }
        string? directory = Path.GetDirectoryName(target.TrimEnd('\\'));
        if (directory == null)
            await _active.NavigateAsync(target); // a drive root
        else
            await _active.NavigateAsync(directory, Path.GetFileName(target.TrimEnd('\\')));
    }

    private async Task MarkByMaskAsync(bool mark)
    {
        var answer = await _host.Dialogs.MaskAsync(mark ? "Select" : "Unselect", _lastSelectMask, _lastSelectDirectories);
        if (answer is not { } a)
            return;
        _lastSelectMask = a.Mask;
        _lastSelectDirectories = a.IncludeDirectories;
        _active.MarkByMask(a.Mask, a.IncludeDirectories, mark);
    }

    private void MoveToMarked(PaneViewModel pane, int direction)
    {
        int index = pane.FindMarked(direction);
        if (index >= 0)
            CaretMoveRequested?.Invoke(this, (pane, index));
    }

    private async Task ClipboardSetAsync(bool cut)
    {
        var items = _active.GetOperationItems();
        if (items.Count == 0)
            return;
        await FileClipboard.SetFilesAsync(items.Select(i => i.Entry.FullPath).ToList(), cut);
    }

    private async Task ClipboardPasteAsync()
    {
        if (!_active.IsLocalDisk)
            return;
        var (paths, move) = await FileClipboard.GetFilesAsync();
        if (paths.Count == 0)
            return;
        string target = _active.Path.EndsWith('\\') ? _active.Path : _active.Path + "\\";
        await TransferAsync(paths, target, move);
        if (move)
            Clipboard.Clear(); // cut + paste moves once, like Explorer
    }

    /// <summary>
    /// F7: creates "New Folder" in the panel and lets you name it in place, like Explorer (Esc keeps the
    /// name). Typing a path such as "a\b\c" creates the nested directories, as Salamander's F7 does.
    /// </summary>
    private async Task CreateDirectoryAsync()
    {
        PaneViewModel pane = _active;
        if (!pane.IsLocalDisk)
            return;
        string path = UniquePath(pane.Path, "New Folder", string.Empty);
        Directory.CreateDirectory(path);
        await CreatedItemRenameAsync(pane, path, allowSubdirectories: true);
    }

    /// <summary>Shift+F7: type a path straight into the address bar.</summary>
    private void ChangeDirectory() => _host.EditPath(_active);

    private void ShowProperties()
    {
        var items = _active.GetOperationItems();
        if (items.Count > 0)
            ShellOperations.ShowProperties(_host.WindowHandle, items[0].Entry.FullPath);
        else if (_active.FocusedItem?.Drive is { } drive)
            ShellOperations.ShowProperties(_host.WindowHandle, drive.Root);
    }

    /// <summary>Executes the command line text in the active panel's directory.</summary>
    public async Task RunCommandLineAsync(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return;
        if (Settings.History.CommandLineHistory)
            RememberRecent(Settings.CommandLineHistory, text);
        // "cd <path>" navigates, like Salamander's command line
        if (text.StartsWith("cd ", StringComparison.OrdinalIgnoreCase) || (text.Length == 2 && text[1] == ':'))
        {
            string arg = text.Length == 2 ? text : text[3..];
            if (_active.IsLocalDisk || System.IO.Path.IsPathRooted(arg))
                await _active.NavigateAsync(PathUtil.Resolve(_active.IsLocalDisk ? _active.Path : @"C:\", arg));
            return;
        }
        if (_active.IsLocalDisk)
            ShellOperations.RunCommandLine(_active.Path, text, Settings.General.CloseShellAfterCommand);
    }
}
