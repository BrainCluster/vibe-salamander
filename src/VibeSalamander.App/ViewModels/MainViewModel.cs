using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using VibeSalamander.Core;
using VibeSalamander.Core.Commands;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Interop;
using VibeSalamander.Core.Operations;
using VibeSalamander.Core.Panels;
using VibeSalamander.Core.Settings;
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
    void ShowFindWindow(string directory);
    Task<string?> EditNameInPlaceAsync(PaneViewModel pane, FileItemViewModel item);
    void ShowContextMenu(PaneViewModel pane);
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
        Icons = new IconCache(dispatcher);
        KeyMap = KeyMap.CreateSalamanderDefaults();

        Left = new PaneViewModel("Left", Registry, dispatcher, Icons, Settings.Left, () => Settings.ShowHidden);
        Right = new PaneViewModel("Right", Registry, dispatcher, Icons, Settings.Right, () => Settings.ShowHidden);
        _active = Settings.RightPanelActive ? Right : Left;
        _active.IsActive = true;

        Transfers = new TransfersViewModel(dispatcher, host.Dialogs);
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

    /// <summary>Background copy/move/delete queue shown in the transfer panel.</summary>
    public TransfersViewModel Transfers { get; }

    public AppSettings Settings { get; }
    public FileSystemRegistry Registry { get; }
    public IconCache Icons { get; }
    public KeyMap KeyMap { get; }
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

    private static string StartPath(string saved, string fallback)
    {
        if (saved.StartsWith(ThisPcProvider.RootPath, StringComparison.OrdinalIgnoreCase))
            return saved;
        return Directory.Exists(saved) ? saved : fallback;
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

    public void SaveSettings()
    {
        Left.StoreSettings(Settings.Left);
        Right.StoreSettings(Settings.Right);
        Settings.RightPanelActive = _active == Right;
        SettingsStore.Save(Settings);
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
        // Phase 2: internal viewer. Until then F3 opens the file with its associated program.
        FileItemViewModel? item = _active.FocusedItem;
        if (item != null && !item.Entry.IsDirectory)
            ShellOperations.Open(item.Entry.FullPath, _active.Path);
    }

    private void EditFile()
    {
        FileItemViewModel? item = _active.FocusedItem;
        if (item != null && !item.Entry.IsDirectory)
            ShellOperations.Edit(item.Entry.FullPath);
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
            string? final = await RenameItemAsync(pane, item, allowSubdirectories);
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

        string what = items.Count == 1 ? $"\"{items[0].Entry.Name}\"" : $"{items.Count} items";
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

    private async Task DeleteAsync(bool permanent)
    {
        var items = _active.GetOperationItems();
        if (items.Count == 0 || !_active.IsLocalDisk)
            return;
        string what = items.Count == 1
            ? $"{(items[0].Entry.IsDirectory ? "directory" : "file")} \"{items[0].Entry.Name}\""
            : $"{items.Count} selected items";
        // the Recycle Bin can be undone, so (like Explorer) it only asks when switched on in Options;
        // a permanent delete always asks
        if (permanent || Settings.ConfirmRecycle)
        {
            string message = permanent
                ? $"Do you want to permanently delete the {what}?\nThis cannot be undone."
                : $"Do you want to move the {what} to the Recycle Bin?";
            if (!await _host.ConfirmAsync("Delete", message, "Delete"))
                return;
        }

        int caret = _active.CaretIndex;
        var paths = items.Select(i => i.Entry.FullPath).ToList();
        _active.RememberSelection();
        if (permanent)
        {
            // runs in the transfer panel; the panels refresh when it finishes
            _active.MarkAll(false);
            Transfers.Enqueue(OperationKind.Delete, paths, null, (engine, ct) => engine.DeleteAsync(paths, ct));
            return;
        }
        // the Recycle Bin is only reachable through the shell (it shows its own progress window)
        await ShellOperations.DeleteAsync(_host.WindowHandle, paths, permanent: false);
        await RefreshAllAsync();
        CaretMoveRequested?.Invoke(this, (_active, Math.Min(caret, Math.Max(0, _active.Items.Count - 1))));
    }

    /// <summary>F2: rename in place (Salamander quick rename).</summary>
    private async Task RenameAsync()
    {
        FileItemViewModel? item = _active.FocusedItem;
        if (item == null || item.Entry.IsUpDir || item.IsDrive || !_active.IsLocalDisk)
            return;
        await RenameItemAsync(_active, item, allowSubdirectories: false);
    }

    /// <summary>
    /// Edits the item's name in its row and renames it. Problems are reported with a notification, not a
    /// dialog. With <paramref name="allowSubdirectories"/> (a new folder) a typed "a\b" creates the path.
    /// Returns the item's path afterwards (unchanged when cancelled), or null when the rename failed.
    /// </summary>
    private async Task<string?> RenameItemAsync(PaneViewModel pane, FileItemViewModel item, bool allowSubdirectories)
    {
        string oldPath = item.Entry.FullPath;
        string? newName = await _host.EditNameInPlaceAsync(pane, item);
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

    // ---- phase 2 helpers -------------------------------------------------------------------

    private Task ReportProblemsAsync(OperationResult? result)
    {
        if (result != null && result.Failed > 0)
            _host.Notify($"{result.Failed} item{(result.Failed == 1 ? "" : "s")} could not be processed.");
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
        // "cd <path>" navigates, like Salamander's command line
        if (text.StartsWith("cd ", StringComparison.OrdinalIgnoreCase) || (text.Length == 2 && text[1] == ':'))
        {
            string arg = text.Length == 2 ? text : text[3..];
            if (_active.IsLocalDisk || System.IO.Path.IsPathRooted(arg))
                await _active.NavigateAsync(PathUtil.Resolve(_active.IsLocalDisk ? _active.Path : @"C:\", arg));
            return;
        }
        if (_active.IsLocalDisk)
            ShellOperations.RunCommandLine(_active.Path, text);
    }
}
