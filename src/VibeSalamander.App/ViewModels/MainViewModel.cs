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

/// <summary>Owns both panels and executes every <see cref="CommandId"/>.</summary>
public sealed partial class MainViewModel : BindableBase
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
        Settings = SettingsStore.Load(SettingsStore.FilePath, out string? settingsProblem);
        SettingsProblem = settingsProblem;
        if (settingsProblem != null)
            Core.Diagnostics.ErrorLog.Error("Settings", settingsProblem);
        Registry = new FileSystemRegistry();
        // plugins: archives open like directories ("C:\\x.zip\\dir"), so their provider goes before the disk's
        Plugins = new PluginManager();
        ArchiveProvider = new ArchiveFileSystemProvider(Plugins);
        Registry.RegisterFirst(ArchiveProvider);
        RemoteProvider = new RemoteFileSystemProvider(Plugins);
        Registry.RegisterFirst(RemoteProvider);
        Icons = new IconCache(dispatcher);
        Thumbnails = new ThumbnailCache(dispatcher);
        Settings.HotPaths ??= new List<string>(); // "HotPaths": null in a hand-edited settings file
        while (Settings.HotPaths.Count < 10)
            Settings.HotPaths.Add(string.Empty);
        Settings.KeyBindings ??= new List<KeyBindingOverride>();
        Settings.Toolbar ??= CommandCatalog.DefaultToolbar();
        KeyMap = KeyMap.Create(Settings.KeyScheme, Settings.KeyBindings);

        Left = new PaneViewModel("Left", Registry, dispatcher, Icons, Settings.Left, () => Settings.ShowHidden, () => Settings);
        Right = new PaneViewModel("Right", Registry, dispatcher, Icons, Settings.Right, () => Settings.ShowHidden, () => Settings);
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
            pane.ArchiveFileOpenRequested += async (_, item) =>
            {
                if (p.IsRemote)
                    await ServerPanels.OpenCopyAsync(p, item, Settings.Viewers);
                else
                    await ArchivePanels.OpenCopyAsync(p, item);
            };
            pane.DiskImageOpenRequested += async (_, path) => await OpenDiskImageAsync(p, path);
            pane.LeavingServer = (leaving, serverPath) => ServerPanels.ConfirmLeaveAsync(leaving, serverPath);
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
                VerifyCopies = Settings.General.VerifyCopies,
            },
            () => Math.Clamp(Settings.General.ParallelTransfers, 1, 8),
            () => Settings.General.TransfersOnePerDisk);
        Transfers.SharedLimit = Math.Max(0, Settings.TransfersSpeedLimit);
        Transfers.ShowGraphs = Settings.TransfersGraphs;
        Transfers.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TransfersViewModel.SharedLimit) or nameof(TransfersViewModel.ShowGraphs))
            {
                Settings.TransfersSpeedLimit = Transfers.SharedLimit;
                Settings.TransfersGraphs = Transfers.ShowGraphs;
                SaveSettings();
            }
        };
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

    /// <summary>The user interface lent to plugins.</summary>
    public IPluginUi PluginUi => _host.PluginUi;

    /// <summary>
    /// An app command that a plugin command answers to (Calculate / Verify Checksums): runs the enabled
    /// plugin's command, or says which plugin is needed.
    /// </summary>
    private async Task RunPluginHostCommandAsync(CommandId command)
    {
        string name = command.ToString();
        foreach (PluginInfo plugin in Plugins.Plugins.Where(p => p.Enabled && p.Error == null && p.Host != null))
        {
            PluginCommand? match = plugin.CommandContributors.SelectMany(c => c.Commands)
                .FirstOrDefault(c => string.Equals(c.HostCommand, name, StringComparison.Ordinal));
            if (match == null)
                continue;
            try
            {
                await match.Execute(plugin.Host!);
            }
            catch (Exception ex)
            {
                Notify($"{plugin.Name}: {ex.Message}");
            }
            return;
        }
        Notify(Loc.F("\"{0}\" needs a plugin that is not enabled (Plugins → Plugins Manager).", CommandCatalog.DisplayName(command)));
    }

    /// <summary>Plugin file systems (FTP, SFTP) in the panels.</summary>
    public RemoteFileSystemProvider RemoteProvider { get; }

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

    /// <summary>What went wrong reading the settings file at start (shown once the window is up), or null.</summary>
    public string? SettingsProblem { get; }
    public FileSystemRegistry Registry { get; }
    public IconCache Icons { get; }
    public ThumbnailCache Thumbnails { get; }
    /// <summary>Shortcuts: Salamander's or Total Commander's defaults plus the user's changes (Configuration → Keyboard).</summary>
    public KeyMap KeyMap { get; private set; }

    /// <summary>Replaces the key map (after the keyboard settings changed) and stores the differences.</summary>
    /// <summary>Starts from another scheme's shortcuts (own changes are dropped: they were made for the other one).</summary>
    public void SetKeyScheme(KeyScheme scheme)
    {
        Settings.KeyScheme = scheme;
        SetKeyMap(KeyMap.CreateDefaults(scheme));
    }

    public void SetKeyMap(KeyMap map)
    {
        KeyMap = map;
        Settings.KeyBindings = map.GetOverrides(Settings.KeyScheme);
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
}
