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

// MainViewModel: running commands (every CommandId goes through ExecuteAsync).
public sealed partial class MainViewModel
{
    // ---- command dispatch --------------------------------------------------------------------

    public bool CanExecute(CommandId command) => command switch
    {
        CommandId.Copy => _active.IsLocalDisk || _active.IsArchive || _active.IsRemote,
        CommandId.Move or CommandId.Delete or CommandId.DeletePermanently or CommandId.ClipboardPaste =>
            _active.IsLocalDisk || _active.IsRemote || EditableArchive(_active) != null,
        CommandId.QuickRename or CommandId.CreateDirectory when _active.IsRemote => true,
        CommandId.QuickRename or CommandId.CreateDirectory or CommandId.EditNewFile or
        CommandId.ChangeAttributes or CommandId.ChangeCase or CommandId.ClipboardCut or
        CommandId.ClipboardCopy => _active.IsLocalDisk,
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
            if (p.IsRemote && await RemoteCommandAsync(p, command))
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
                case CommandId.CalculateChecksums or CommandId.VerifyChecksums or CommandId.ConnectFtp or CommandId.DiskMap
                    or CommandId.TestArchive or CommandId.MakeSelfExtractingArchive or CommandId.ArchiveComment: await RunPluginHostCommandAsync(command); break;
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
                    ShellOperations.Open("https://github.com/BrainCluster/vibe-salamander");
                    break;
                case CommandId.About: _host.ShowAbout(); break;
                case CommandId.ErrorLog: await ShowErrorLogAsync(p); break;
                case CommandId.Exit: _host.Exit(); break;
            }
        }
        catch (Exception ex)
        {
            Core.Diagnostics.ErrorLog.Exception(CommandCatalog.DisplayName(command), ex); // with the details the message leaves out
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
}
