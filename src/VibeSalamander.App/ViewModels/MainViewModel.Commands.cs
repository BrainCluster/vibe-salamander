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

// MainViewModel: running commands (every CommandId goes through ExecuteAsync and its handler in the CommandTable).
public sealed partial class MainViewModel
{
    // ---- command dispatch --------------------------------------------------------------------

    public bool CanExecute(CommandId command) => command switch
    {
        CommandId.Copy => _active.IsLocalDisk || _active.IsArchive || _active.IsRemote,
        CommandId.Move or CommandId.Delete or CommandId.DeletePermanently or CommandId.ClipboardPaste =>
            _active.IsLocalDisk || _active.IsRemote || ArchivePanelCommands.Editable(_active) != null,
        CommandId.QuickRename or CommandId.CreateDirectory when _active.IsRemote => true,
        CommandId.CopyAsShortcut => _active.HasDiskItems,
        CommandId.Duplicate or CommandId.MultiRename => _active.IsLocalDisk || (_active.HasDiskItems && command == CommandId.MultiRename),
        CommandId.QuickRename or CommandId.CreateDirectory or CommandId.EditNewFile or
        CommandId.ChangeAttributes or CommandId.ChangeCase or CommandId.ClipboardCut or
        CommandId.ClipboardCopy => _active.IsLocalDisk,
        CommandId.GoBack => _active.CanGoBack,
        CommandId.GoForward => _active.CanGoForward,
        _ => true,
    };

    private CommandTable? _commandTable;

    /// <summary>The handler of every command (built on first use).</summary>
    private CommandTable CommandHandlers => _commandTable ??= BuildCommandTable();

    public async Task ExecuteAsync(CommandId command)
    {
        PaneViewModel p = _active;
        try
        {
            // inside an archive or on a server, the plugin's file system handles the file commands first
            if (p.IsArchive && await ArchivePanels.TryHandleAsync(p, command))
                return;
            if (p.IsRemote && await ServerPanels.TryHandleAsync(p, command))
                return;
            if (CommandHandlers.TryGet(command, out Func<PaneViewModel, Task> run))
                await run(p);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.ErrorLog.Exception(CommandCatalog.DisplayName(command), ex); // with the details the message leaves out
            _host.Notify(ex.Message);
        }
        OnPropertyChanged(nameof(WindowTitle));
    }

    private CommandTable BuildCommandTable()
    {
        var table = new CommandTable();
        AddNavigationCommands(table);
        AddViewCommands(table);
        AddSelectionCommands(table);
        AddFileCommands(table);
        AddToolCommands(table);
        AddWindowCommands(table);
        return table;
    }

    /// <summary>Drives, folders, history, hot paths, the panels.</summary>
    private void AddNavigationCommands(CommandTable t)
    {
        t.AddSync(CommandId.ChangeDriveLeft, _ => { Activate(Left); _host.ShowDriveMenu(Left); });
        t.AddSync(CommandId.ChangeDriveRight, _ => { Activate(Right); _host.ShowDriveMenu(Right); });
        t.Add(CommandId.GoBack, p => p.GoBackAsync());
        t.Add(CommandId.GoForward, p => p.GoForwardAsync());
        t.Add(CommandId.ParentDirectory, p => p.GoUpAsync());
        t.Add(CommandId.RootDirectory, p => p.GoToRootAsync());
        t.Add(CommandId.ThisPc, p => p.NavigateAsync(ThisPcProvider.RootPath));
        t.Add(CommandId.PathFromOtherPanel, p => p.NavigateAsync(Inactive.Path));
        t.Add(CommandId.OpenInOtherPanel, _ => OpenInOtherPanelAsync());
        t.Add(CommandId.Refresh, async p =>
        {
            await p.RefreshAsync();
            await Task.WhenAll(Left.LoadDrivesAsync(), Right.LoadDrivesAsync());
        });
        t.AddSync(CommandId.SwitchPanel, _ => { Activate(Inactive); _host.FocusPane(_active); });
        t.Add(CommandId.SwapPanels, _ => SwapPanelsAsync());
        t.AddSync(CommandId.MaximizePanel, p =>
        {
            _maximized = _maximized == null ? p : null;
            _host.SetPanelMaximized(_maximized);
        });
        for (int i = 0; i <= 9; i++)
        {
            int number = i;
            t.Add(CommandId.GoToHotPath0 + number, _ => GoToHotPathAsync(number));
            t.AddSync(CommandId.SetHotPath0 + number, _ => SetHotPath(number));
        }
        t.Add(CommandId.Open, p => p.OpenFocusedAsync());
        t.AddSync(CommandId.ChangeDirectory, _ => ChangeDirectory());
        t.Add(CommandId.GoToShortcutTarget, _ => GoToShortcutTargetAsync());
        t.AddSync(CommandId.OpenActiveFolder, p =>
        {
            if (p.IsLocalDisk)
                ShellOperations.OpenInExplorer(p.Path);
            else
                ShellOperations.OpenInExplorer("shell:MyComputerFolder");
        });
        t.AddSync(CommandId.CommandShell, p =>
        {
            if (p.IsLocalDisk)
                ShellOperations.OpenCommandShell(p.Path);
        });
    }

    /// <summary>Views, sorting, what the panels show.</summary>
    private void AddViewCommands(CommandTable t)
    {
        t.AddSync(CommandId.SortByName, p => p.SetSort(SortMode.Name));
        t.AddSync(CommandId.SortByExtension, p => p.SetSort(SortMode.Extension));
        t.AddSync(CommandId.SortBySize, p => p.SetSort(SortMode.Size));
        t.AddSync(CommandId.SortByDate, p => p.SetSort(SortMode.Date));
        t.AddSync(CommandId.SortByAttributes, p => p.SetSort(SortMode.Attributes));
        t.AddSync(CommandId.ViewBrief, p => p.ViewMode = ViewMode.Brief);
        t.AddSync(CommandId.ViewDetailed, p => p.ViewMode = ViewMode.Detailed);
        t.AddSync(CommandId.ViewIcons, p => p.ViewMode = ViewMode.Icons);
        t.AddSync(CommandId.ViewThumbnails, p => p.ViewMode = ViewMode.Thumbnails);
        t.AddSync(CommandId.ViewTiles, p => p.ViewMode = ViewMode.Tiles);
        t.Add(CommandId.ToggleHiddenFiles, async _ =>
        {
            Settings.ShowHidden = !Settings.ShowHidden;
            await Task.WhenAll(Left.RefreshAsync(), Right.RefreshAsync());
        });
        t.Add(CommandId.Filter, _ => FilterAsync());
        t.AddSync(CommandId.HideSelectedNames, p => HideNames(p, selected: true));
        t.AddSync(CommandId.HideUnselectedNames, p => HideNames(p, selected: false));
        t.AddSync(CommandId.ShowHiddenNames, p => ShowHiddenNames(p));
    }

    /// <summary>Marking (the Salamander selection).</summary>
    private void AddSelectionCommands(CommandTable t)
    {
        t.AddSync(CommandId.ToggleMark, p => p.ToggleMark(p.CaretIndex));
        t.AddSync(CommandId.ToggleMarkAndMoveDown, p =>
        {
            p.ToggleMark(p.CaretIndex);
            MoveCaret(p, +1);
        });
        t.AddSync(CommandId.SelectAll, p => p.MarkAll(true));
        t.AddSync(CommandId.UnselectAll, p => p.MarkAll(false));
        t.AddSync(CommandId.InvertSelection, p => p.InvertMarks());
        t.Add(CommandId.SelectByMask, _ => MarkByMaskAsync(mark: true));
        t.Add(CommandId.UnselectByMask, _ => MarkByMaskAsync(mark: false));
        t.AddSync(CommandId.RestoreSelection, p => p.RestoreSelection());
        t.AddSync(CommandId.SelectSameExtension, p => p.MarkSame(byExtension: true, mark: true));
        t.AddSync(CommandId.UnselectSameExtension, p => p.MarkSame(byExtension: true, mark: false));
        t.AddSync(CommandId.SelectSameName, p => p.MarkSame(byExtension: false, mark: true));
        t.AddSync(CommandId.UnselectSameName, p => p.MarkSame(byExtension: false, mark: false));
        t.AddSync(CommandId.GoToPreviousSelected, p => MoveToMarked(p, -1));
        t.AddSync(CommandId.GoToNextSelected, p => MoveToMarked(p, +1));
        t.AddSync(CommandId.SaveSelection, p => SaveSelection(p));
        t.AddSync(CommandId.LoadSelection, p => LoadSelection(p));
    }

    /// <summary>Opening, copying, moving, deleting, renaming, archives, the clipboard.</summary>
    private void AddFileCommands(CommandTable t)
    {
        t.Add(CommandId.QuickRename, _ => Files.RenameAsync());
        t.AddSync(CommandId.View, _ => ViewFile());
        t.AddSync(CommandId.Edit, _ => EditFile());
        t.Add(CommandId.EditNewFile, _ => Files.EditNewFileAsync());
        t.AddSync(CommandId.OpenWith, _ => Files.OpenWith());
        t.Add(CommandId.Copy, _ => Files.CopyOrMoveAsync(move: false));
        t.Add(CommandId.Move, _ => Files.CopyOrMoveAsync(move: true));
        t.Add(CommandId.Delete, _ => Files.DeleteAsync(permanent: false));
        t.Add(CommandId.DeletePermanently, _ => Files.DeleteAsync(permanent: true));
        t.Add(CommandId.CreateDirectory, _ => Files.CreateDirectoryAsync());
        t.AddSync(CommandId.Properties, _ => Files.ShowProperties());
        t.Add(CommandId.ChangeAttributes, _ => Files.ChangeAttributesAsync());
        t.Add(CommandId.ChangeCase, _ => Files.ChangeCaseAsync());
        t.AddSync(CommandId.ContextMenu, p => _host.ShowContextMenu(p));
        t.AddSync(CommandId.Security, p => ShowSecurity(p));
        t.Add(CommandId.Email, p => EmailAsync(p));
        t.Add(CommandId.NtfsCompress, p => NtfsAsync(p, NtfsAction.Compress));
        t.Add(CommandId.NtfsUncompress, p => NtfsAsync(p, NtfsAction.Uncompress));
        t.Add(CommandId.NtfsEncrypt, p => NtfsAsync(p, NtfsAction.Encrypt));
        t.Add(CommandId.NtfsDecrypt, p => NtfsAsync(p, NtfsAction.Decrypt));

        t.Add(CommandId.ClipboardCut, _ => Files.ClipboardSetAsync(cut: true));
        t.Add(CommandId.ClipboardCopy, _ => Files.ClipboardSetAsync(cut: false));
        t.Add(CommandId.ClipboardPaste, _ => Files.ClipboardPasteAsync());
        t.Add(CommandId.PasteShortcut, p => PasteShortcutsAsync(p));
        t.Add(CommandId.CopyAsShortcut, p => CopyAsShortcutsAsync(p));
        t.Add(CommandId.NewMenu, p => NewFromShellMenuAsync(p));
        t.AddSync(CommandId.CopyFullNameAsText, p => CopyText(string.Join(Environment.NewLine, p.GetOperationItems().Select(i => i.Entry.FullPath))));
        t.AddSync(CommandId.CopyNameAsText, p => CopyText(string.Join(Environment.NewLine, p.GetOperationItems().Select(i => i.Entry.Name))));
        t.AddSync(CommandId.CopyPathAsText, p => CopyText(p.IsLocalDisk ? p.Path : p.DisplayPath));

        t.Add(CommandId.Pack, _ => Archives.PackAsync());
        t.Add(CommandId.Unpack, _ => Archives.UnpackAsync());
        foreach (CommandId pluginCommand in new[]
                 {
                     CommandId.CalculateChecksums, CommandId.VerifyChecksums, CommandId.ConnectFtp, CommandId.DiskMap,
                     CommandId.TestArchive, CommandId.MakeSelfExtractingArchive, CommandId.ArchiveComment,
                 })
        {
            CommandId command = pluginCommand;
            t.Add(command, _ => RunPluginHostCommandAsync(command));
        }
        t.Add(CommandId.MountImage, async p =>
        {
            if (p.FocusedItem is { Entry.IsDirectory: false } diskImage && p.IsLocalDisk && DiskImages.IsDiskImage(diskImage.Entry.FullPath))
                await MountImageAsync(p, diskImage.Entry.FullPath);
        });
        t.Add(CommandId.UnmountImage, async p =>
        {
            if (ImageDriveOf(p) is { } imageRoot)
                await UnmountImageAsync(imageRoot);
            else
                _host.Notify("Focus a mounted disk image (a drive in This PC, or a panel on it) to unmount it.", isError: false);
        });
    }

    /// <summary>Find, compare, sizes, lists, the user menu, network drives.</summary>
    private void AddToolCommands(CommandTable t)
    {
        t.AddSync(CommandId.FindFiles, p => _host.ShowFindWindow(p.IsLocalDisk ? p.Path : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        t.Add(CommandId.CompareDirectories, _ => CompareDirectoriesAsync());
        t.AddSync(CommandId.CompareFiles, _ => CompareFiles());
        t.AddSync(CommandId.FindDuplicates, p => _host.ShowDuplicatesWindow(p.IsLocalDisk ? p.Path : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        t.Add(CommandId.CalculateOccupiedSpace, p => CalculateOccupiedSpaceAsync(p, p.GetOperationItems()));
        t.Add(CommandId.CalculateDirectorySizes, p =>
            CalculateOccupiedSpaceAsync(p, p.Items.Where(i => i.Entry.IsDirectory && !i.Entry.IsUpDir && !i.IsDrive).ToList()));
        t.AddSync(CommandId.DriveInformation, p => _host.ShowDriveInformation(p));
        t.AddSync(CommandId.UserMenu, p => ShowUserMenu(p));
        t.Add(CommandId.EditUserMenu, _ => EditUserMenuAsync());
        t.AddSync(CommandId.MakeFileList, p => MakeFileList(p));
        t.AddSync(CommandId.OpenedFilesList, p => ShowOpenedFiles(p));
        t.AddSync(CommandId.WorkingDirectoriesList, p => ShowWorkingDirectories(p));
        t.Add(CommandId.Duplicate, _ => Files.DuplicateAsync());
        t.AddSync(CommandId.MultiRename, p => OpenMultiRename(p));
        t.Add(CommandId.FlatView, p => FlatViewAsync(p));
        t.AddSync(CommandId.QuickView, _ => _host.ToggleQuickView());
        t.AddSync(CommandId.HotPathList, p => ShowHotPaths(p));
        t.AddSync(CommandId.NameToCommandLine, p =>
        {
            if (p.FocusedItem is { Entry.IsUpDir: false } item)
                _host.AddToCommandLine(item.Entry.Name);
        });
        t.AddSync(CommandId.FullNameToCommandLine, p =>
        {
            if (p.FocusedItem is { Entry.IsUpDir: false } item)
                _host.AddToCommandLine(item.Entry.FullPath);
        });
        t.AddSync(CommandId.PathToCommandLine, p => _host.AddToCommandLine(p.IsLocalDisk ? p.Path.TrimEnd('\\') + "\\" : p.DisplayPath));
        t.AddSync(CommandId.CommandHistory, p => _host.ShowCommandHistory(p));
        t.Add(CommandId.ConnectNetworkDrive, async _ =>
        {
            if (NetworkDrives.ShowConnectDialog(_host.WindowHandle))
                await Task.WhenAll(Left.LoadDrivesAsync(), Right.LoadDrivesAsync());
        });
        t.Add(CommandId.DisconnectNetworkDrive, async _ =>
        {
            if (NetworkDrives.ShowDisconnectDialog(_host.WindowHandle))
                await Task.WhenAll(Left.LoadDrivesAsync(), Right.LoadDrivesAsync());
        });
        t.Add(CommandId.SharedDirectories, p => ShowSharesAsync(p));
    }

    /// <summary>The window, Configuration, help.</summary>
    private void AddWindowCommands(CommandTable t)
    {
        t.AddSync(CommandId.ToggleDensity, _ =>
        {
            Settings.Density = Settings.Density == Density.Compact ? Density.Comfortable : Density.Compact;
            _host.ApplyDensity();
        });
        t.Add(CommandId.AlwaysOnTop, async _ =>
        {
            if (!Settings.AlwaysOnTop && Settings.Confirm.AlwaysOnTop &&
                !await _host.ConfirmAsync("Always on Top", "Do you want to keep the Vibe Salamander window on top of all other windows?", "Turn On"))
                return;
            Settings.AlwaysOnTop = !Settings.AlwaysOnTop;
            _host.ApplyAlwaysOnTop(Settings.AlwaysOnTop);
        });
        t.AddSync(CommandId.Configuration, _ => _host.ShowConfiguration());
        t.AddSync(CommandId.PluginsManager, _ => _host.ShowConfigurationPage("Plugins"));
        t.AddSync(CommandId.Help, _ => ShellOperations.Open("https://github.com/BrainCluster/vibe-salamander/wiki")); // F1, Help → Contents: the wiki
        t.AddSync(CommandId.About, _ => _host.ShowAbout());
        t.Add(CommandId.ErrorLog, p => ShowErrorLogAsync(p));
        t.AddSync(CommandId.Exit, _ => _host.Exit());
    }

    private FileCommands? _fileCommands;
    private ArchiveCommands? _archiveCommands;
    private ArchivePanelCommands? _archivePanels;
    private ServerPanelCommands? _serverPanels;

    /// <summary>Commands in a panel that shows an archive's content (ArchivePanelCommands).</summary>
    internal ArchivePanelCommands ArchivePanels => _archivePanels ??= new ArchivePanelCommands(this, _host);

    /// <summary>Commands in a panel that shows a server (ServerPanelCommands).</summary>
    internal ServerPanelCommands ServerPanels => _serverPanels ??= new ServerPanelCommands(this, _host);

    /// <summary>True when the panel shows a ZIP or TAR that files can be copied into and deleted from.</summary>
    public bool IsEditableArchive(PaneViewModel p) => ArchivePanelCommands.Editable(p) != null;


    /// <summary>Pack and Unpack (ArchiveCommands).</summary>
    internal ArchiveCommands Archives => _archiveCommands ??= new ArchiveCommands(this, _host);


    /// <summary>Copy, move, delete, rename, new folder / file, attributes, case, the clipboard (FileCommands).</summary>
    internal FileCommands Files => _fileCommands ??= new FileCommands(this, _host, _callbacks);

    /// <summary>Copies or moves in the transfer panel (drag and drop, paste); see FileCommands.TransferAsync.</summary>
    public Task TransferAsync(IReadOnlyList<string> sources, string target, bool move) => Files.TransferAsync(sources, target, move);

    /// <summary>Confirmations → Drag and Drop operations.</summary>
    public Task<bool> ConfirmDropAsync(IReadOnlyList<string> paths, string target, bool move) => Files.ConfirmDropAsync(paths, target, move);

    /// <summary>Confirmations → "The directory doesn't exist. Do you want to create it?" Creates it when confirmed.</summary>
    public Task<bool> CreateMissingDirectoryAsync(string path) => Files.CreateMissingDirectoryAsync(path);

    /// <summary>Moves the panel's caret (after deleting into the Recycle Bin, Alt+Up / Alt+Down, ...).</summary>
    internal void RequestCaretMove(PaneViewModel pane, int index) => CaretMoveRequested?.Invoke(this, (pane, index));

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
        // Configuration → Viewers by file mask first, then the built-in viewer (or the Windows app)
        FileItemViewModel? item = _active.FocusedItem;
        if (item != null && !item.Entry.IsDirectory && !item.Entry.IsUpDir && !item.IsDrive)
        {
            if (!RunProgramRule(Settings.Viewers, item))
                ViewWithoutRules(item.Entry.FullPath, SiblingFiles(_active));
            RememberOpened(item.Entry.FullPath);
        }
    }

    private void EditFile()
    {
        FileItemViewModel? item = _active.FocusedItem;
        if (item != null && !item.Entry.IsDirectory && !item.Entry.IsUpDir && !item.IsDrive)
        {
            if (!RunProgramRule(Settings.Editors, item))
                EditWithoutRules(item.Entry.FullPath);
            RememberOpened(item.Entry.FullPath);
        }
    }

    /// <summary>The built-in viewer, or (switched off in Configuration → Viewers) the file's Windows app.</summary>
    internal void ViewWithoutRules(string path, IReadOnlyList<string>? siblings = null)
    {
        if (Settings.Viewer.UseBuiltInViewer)
            _host.OpenViewer(path, siblings ?? new[] { path });
        else
            ShellOperations.Open(path, Path.GetDirectoryName(path));
    }

    /// <summary>The built-in editor, or (switched off in Configuration → Editors) Windows' "edit" for the file.</summary>
    internal void EditWithoutRules(string path, Func<string, Task>? afterSave = null)
    {
        if (Settings.Viewer.UseBuiltInEditor)
            _host.OpenEditor(path, afterSave);
        else
            ShellOperations.Edit(path);
    }

    /// <summary>F4 for a file (from the viewer): Configuration → Editors, then the built-in editor.</summary>
    public void EditFile(string path)
    {
        if (!File.Exists(path))
            return;
        var info = new FileInfo(path);
        RunFoundFile(new FileEntry(info.Name, path, false, info.Length, info.LastWriteTime, info.Attributes), Settings.Editors, () => EditWithoutRules(path));
    }

    /// <summary>The files of a panel on a disk, in its order (the viewer goes through them).</summary>
    private static IReadOnlyList<string> SiblingFiles(PaneViewModel pane) =>
        pane.IsLocalDisk
            ? pane.Items.Where(i => !i.Entry.IsDirectory && !i.Entry.IsUpDir && !i.IsDrive).Select(i => i.Entry.FullPath).ToList()
            : Array.Empty<string>();
}
