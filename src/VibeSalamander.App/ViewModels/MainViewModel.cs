using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using VibeSalamander.Core;
using VibeSalamander.Core.Commands;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Interop;
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
}

/// <summary>Owns both panels and executes every <see cref="CommandId"/>.</summary>
public sealed class MainViewModel : BindableBase
{
    private readonly IUiHost _host;
    private PaneViewModel _active;
    private PaneViewModel? _maximized;

    public MainViewModel(IUiHost host, DispatcherQueue dispatcher)
    {
        _host = host;
        Settings = SettingsStore.Load();
        Registry = new FileSystemRegistry();
        Icons = new IconCache(dispatcher);
        KeyMap = KeyMap.CreateSalamanderDefaults();

        Left = new PaneViewModel("Left", Registry, dispatcher, Icons, Settings.Left, () => Settings.ShowHidden);
        Right = new PaneViewModel("Right", Registry, dispatcher, Icons, Settings.Right, () => Settings.ShowHidden);
        _active = Settings.RightPanelActive ? Right : Left;
        _active.IsActive = true;
    }

    public AppSettings Settings { get; }
    public FileSystemRegistry Registry { get; }
    public IconCache Icons { get; }
    public KeyMap KeyMap { get; }
    public PaneViewModel Left { get; }
    public PaneViewModel Right { get; }

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
        CommandId.QuickRename or CommandId.CreateDirectory or CommandId.EditNewFile => _active.IsLocalDisk,
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
                case CommandId.ChangeDirectory: await ChangeDirectoryAsync(); break;
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
            await _host.ShowMessageAsync("Vibe Salamander", ex.Message);
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

    private async Task EditNewFileAsync()
    {
        if (!_active.IsLocalDisk)
            return;
        string? name = await _host.PromptAsync("Edit New File", "File name:", "New Text Document.txt", "Create and edit", selectNameWithoutExtension: true);
        if (string.IsNullOrWhiteSpace(name))
            return;
        string path = System.IO.Path.Combine(_active.Path, name.Trim());
        if (!File.Exists(path))
            await File.WriteAllTextAsync(path, string.Empty);
        await _active.NavigateAsync(_active.Path, System.IO.Path.GetFileName(path), addToHistory: false);
        ShellOperations.Edit(path);
    }

    private async Task CopyOrMoveAsync(bool move)
    {
        var items = _active.GetOperationItems();
        if (items.Count == 0 || !_active.IsLocalDisk)
            return;

        string what = items.Count == 1 ? $"\"{items[0].Entry.Name}\"" : $"{items.Count} items";
        string defaultTarget = Inactive.IsLocalDisk ? Inactive.Path : string.Empty;
        string? target = await _host.PromptAsync(move ? "Move/Rename" : "Copy",
                                                 $"{(move ? "Move" : "Copy")} {what} to:", defaultTarget,
                                                 move ? "Move" : "Copy");
        if (string.IsNullOrWhiteSpace(target))
            return;
        target = PathUtil.Resolve(_active.Path, target);
        var sources = items.Select(i => i.Entry.FullPath).ToList();

        // A single item to a path that is not an existing directory copies/moves it under that name
        // (Salamander F6 = move/rename); the shell handles both cases.
        ShellOperations.Result result = move
            ? await ShellOperations.MoveAsync(_host.WindowHandle, sources, target)
            : await ShellOperations.CopyAsync(_host.WindowHandle, sources, target);

        if (result == ShellOperations.Result.Success)
            _active.MarkAll(false);
        await Task.WhenAll(Left.RefreshAsync(), Right.RefreshAsync());
    }

    private async Task DeleteAsync(bool permanent)
    {
        var items = _active.GetOperationItems();
        if (items.Count == 0 || !_active.IsLocalDisk)
            return;
        string what = items.Count == 1
            ? $"{(items[0].Entry.IsDirectory ? "directory" : "file")} \"{items[0].Entry.Name}\""
            : $"{items.Count} selected items";
        string message = permanent
            ? $"Do you want to permanently delete the {what}?\nThis cannot be undone."
            : $"Do you want to move the {what} to the Recycle Bin?";
        if (!await _host.ConfirmAsync("Delete", message, "Delete"))
            return;

        int caret = _active.CaretIndex;
        await ShellOperations.DeleteAsync(_host.WindowHandle, items.Select(i => i.Entry.FullPath).ToList(), permanent);
        await _active.RefreshAsync();
        if (Inactive.IsLocalDisk)
            await Inactive.RefreshAsync();
        CaretMoveRequested?.Invoke(this, (_active, Math.Min(caret, Math.Max(0, _active.Items.Count - 1))));
    }

    private async Task RenameAsync()
    {
        FileItemViewModel? item = _active.FocusedItem;
        if (item == null || item.Entry.IsUpDir || item.IsDrive || !_active.IsLocalDisk)
            return;
        string? newName = await _host.PromptAsync("Quick Rename", $"Rename \"{item.Entry.Name}\" to:", item.Entry.Name, "Rename",
                                                  selectNameWithoutExtension: !item.Entry.IsDirectory);
        if (string.IsNullOrWhiteSpace(newName) || newName == item.Entry.Name)
            return;
        string target = System.IO.Path.Combine(_active.Path, newName.Trim());
        await ShellOperations.RenameAsync(_host.WindowHandle, item.Entry.FullPath, target);
        await _active.NavigateAsync(_active.Path, System.IO.Path.GetFileName(target), addToHistory: false);
    }

    private async Task CreateDirectoryAsync()
    {
        if (!_active.IsLocalDisk)
            return;
        string? name = await _host.PromptAsync("Create Directory", "Directory name:", string.Empty, "Create");
        if (string.IsNullOrWhiteSpace(name))
            return;
        string path = PathUtil.Resolve(_active.Path, name);
        Directory.CreateDirectory(path);
        // focus the new directory (first component when a nested path was typed)
        string relative = System.IO.Path.GetRelativePath(_active.Path, path);
        string first = relative.Split(System.IO.Path.DirectorySeparatorChar)[0];
        await _active.NavigateAsync(_active.Path, first, addToHistory: false);
    }

    private async Task ChangeDirectoryAsync()
    {
        string current = _active.IsLocalDisk ? _active.Path : string.Empty;
        string? input = await _host.PromptAsync("Change Directory", "Path:", current, "Go");
        if (string.IsNullOrWhiteSpace(input))
            return;
        if (input.Trim().Equals("this pc", StringComparison.OrdinalIgnoreCase) || Registry.Find(input) is ThisPcProvider)
        {
            await _active.NavigateAsync(ThisPcProvider.RootPath);
            return;
        }
        string target = PathUtil.Resolve(_active.IsLocalDisk ? _active.Path : Environment.CurrentDirectory, input);
        if (File.Exists(target))
            await _active.NavigateAsync(System.IO.Path.GetDirectoryName(target)!, System.IO.Path.GetFileName(target));
        else
            await _active.NavigateAsync(target);
    }

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
