// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using VibeSalamander.Core.Commands;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Panels;
using VibeSalamander.Services;
using VibeSalamander.ViewModels;

namespace VibeSalamander.Views;

// FilePane: the context menus (Vibe Salamander, plugins, Windows) and menus shown at the caret.
public sealed partial class FilePane
{
    // ---- context menu ----------------------------------------------------------------------

    private void List_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (ViewModel == null || _main == null)
            return;
        _main.Activate(ViewModel);
        FileItemViewModel? item = (e.OriginalSource as FrameworkElement)?.DataContext as FileItemViewModel;
        if (item != null)
        {
            int index = IndexOf(item);
            if (index >= 0)
            {
                FileList.SelectedIndex = index;
                // Configuration → Panels: right-click marks (Norton style), Ctrl+right-click opens the menu
                bool ctrl = KeyboardState.IsDown(KeyboardState.Control);
                if (!_main.Settings.Panels.RightClickOpensMenu ? !ctrl : ctrl)
                {
                    ViewModel.ToggleMark(index);
                    e.Handled = true;
                    return;
                }
            }
        }
        var (x, y) = ShellContextMenu.CursorPosition();
        ShowContextMenu(item, e.GetPosition(FileList), x, y);
        e.Handled = true;
    }

    /// <summary>A command menu (user menu, lists) at the caret item, or at the start of the address bar.</summary>
    public void ShowMenu(IReadOnlyList<MenuEntry> entries, bool atCaret)
    {
        if (ViewModel == null)
            return;
        var menu = new MenuFlyout();
        foreach (MenuEntry entry in entries)
        {
            if (entry.IsSeparator)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                continue;
            }
            // entries are data (paths, user menu names); only the fixed ones are translated
            var item = new MenuFlyoutItem { Text = entry.IsEnabled ? entry.Text : Loc.T(entry.Text), IsEnabled = entry.IsEnabled };
            if (entry.Glyph != null)
                item.Icon = new FontIcon { Glyph = entry.Glyph };
            if (entry.Shortcut != null)
                item.KeyboardAcceleratorTextOverride = entry.Shortcut;
            if (entry.Invoke is { } invoke)
                item.Click += (_, _) => invoke();
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => FocusList();

        FileItemViewModel? focused = ViewModel.FocusedItem;
        if (atCaret && focused != null && FileList.ContainerFromItem(focused) is ListViewItem container)
            menu.ShowAt(container, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
            {
                Position = new Windows.Foundation.Point(40, container.ActualHeight),
                ShowMode = Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowMode.Standard,
            });
        else
            menu.ShowAt(HistoryButton, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
            {
                Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight,
                ShowMode = Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowMode.Standard,
            });
    }

    /// <summary>The Explorer "New" menu for this panel's directory, at the caret (or the mouse).</summary>
    public bool ShowNewMenu()
    {
        if (ViewModel == null || _main == null || !ViewModel.IsLocalDisk)
            return false;
        FileItemViewModel? item = ViewModel.FocusedItem;
        int x, y;
        if (item != null && FileList.ContainerFromItem(item) is ListViewItem container && XamlRoot != null)
        {
            var point = container.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(40, container.ActualHeight));
            double scale = XamlRoot.RasterizationScale;
            (x, y) = ShellContextMenu.ClientToScreen(_main.WindowHandle, (int)(point.X * scale), (int)(point.Y * scale));
        }
        else
        {
            (x, y) = ShellContextMenu.CursorPosition();
        }
        bool chosen = ShellContextMenu.ShowNewMenu(_main.WindowHandle, ViewModel.Path, x, y);
        if (!chosen)
            FocusList();
        return chosen;
    }

    /// <summary>Shift+F10 / Menu key: the context menu for the caret item, positioned at the item.</summary>
    public void ShowContextMenuForCaret()
    {
        if (ViewModel == null || _main == null)
            return;
        FileItemViewModel? item = ViewModel.FocusedItem;
        int x = 0, y = 0;
        var local = new Windows.Foundation.Point(40, 24);
        if (item != null && FileList.ContainerFromItem(item) is ListViewItem container && XamlRoot != null)
        {
            var point = container.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(40, container.ActualHeight));
            local = container.TransformToVisual(FileList).TransformPoint(new Windows.Foundation.Point(40, container.ActualHeight));
            double scale = XamlRoot.RasterizationScale;
            (x, y) = ShellContextMenu.ClientToScreen(_main.WindowHandle, (int)(point.X * scale), (int)(point.Y * scale));
        }
        else
        {
            (x, y) = ShellContextMenu.CursorPosition();
        }
        ShowContextMenu(item, local, x, y, fromMouse: false);
    }

    /// <summary>
    /// The Vibe Salamander context menu: the most used commands for the clicked items (or the directory
    /// when the empty area / ".." was clicked), and "Windows context menu" at the bottom for everything
    /// else. Shift (or Configuration → Panels) opens the Windows menu directly.
    /// </summary>
    /// <param name="position">Where to show it, relative to the list.</param>
    /// <summary>
    /// The archive's plugin in its right-click menu ("Archives ▸", "7-Zip ▸"): Unpack and the plugin's commands that fit
    /// the file (Test archive, Archive comment, ...).
    /// </summary>
    private MenuFlyoutSubItem PluginSubmenu(Services.PluginInfo plugin, string path, PaneViewModel pane)
    {
        string pluginGlyph = plugin.IconGlyph is { Length: > 0 } own ? own : "\uF012";
        var sub = new MenuFlyoutSubItem { Text = plugin.Name, Icon = new FontIcon { Glyph = pluginGlyph } };
        var unpack = new MenuFlyoutItem { Text = Loc.T("Unpack..."), Icon = new FontIcon { Glyph = "\uE896" } };
        if (_main!.KeyMap.GetShortcutText(CommandId.Unpack) is { Length: > 0 } unpackKeys)
            unpack.KeyboardAcceleratorTextOverride = unpackKeys;
        unpack.Click += async (_, _) =>
        {
            _main.Activate(pane);
            await _main.ExecuteAsync(CommandId.Unpack);
        };
        sub.Items.Add(unpack);
        foreach (Plugins.PluginCommand command in plugin.CommandContributors.SelectMany(c => c.Commands))
        {
            bool fits;
            try
            {
                fits = command.AppliesTo?.Invoke(path) == true;
            }
            catch (Exception)
            {
                fits = false;
            }
            if (!fits)
                continue;
            if (command.SeparatorBefore && sub.Items[^1] is not MenuFlyoutSeparator)
                sub.Items.Add(new MenuFlyoutSeparator());
            CommandId hostCommand = CommandId.None;
            bool isHostCommand = command.HostCommand != null && Enum.TryParse(command.HostCommand, out hostCommand);
            string glyph = isHostCommand && CommandCatalog.Glyph(hostCommand) is { Length: > 0 } commandGlyph ? commandGlyph : pluginGlyph;
            var entry = new MenuFlyoutItem { Text = Loc.T(command.MenuText), Icon = new FontIcon { Glyph = glyph } };
            if ((isHostCommand ? _main.KeyMap.GetShortcutText(hostCommand) : command.ShortcutText) is { Length: > 0 } keys)
                entry.KeyboardAcceleratorTextOverride = keys;
            Plugins.PluginCommand c = command;
            entry.Click += async (_, _) =>
            {
                _main.Activate(pane);
                try
                {
                    await c.Execute(plugin.Host!);
                }
                catch (Exception ex)
                {
                    _main.Notify($"{plugin.Name}: {ex.Message}");
                }
            };
            sub.Items.Add(entry);
        }
        return sub;
    }

    /// <param name="screenX">Screen position for the Windows menu.</param>
    /// <param name="fromMouse">Shift+right-click opens the Windows menu (Shift+F10 is just the menu key).</param>
    private void ShowContextMenu(FileItemViewModel? item, Windows.Foundation.Point position, int screenX, int screenY, bool fromMouse = true)
    {
        if (ViewModel == null || _main == null)
            return;
        PaneViewModel pane = ViewModel;
        if (!_main.Settings.Panels.VibeContextMenu || (fromMouse && KeyboardState.IsDown(KeyboardState.Shift)))
        {
            ShowShellMenu(item, screenX, screenY);
            return;
        }
        // like Explorer: a right-click on an unmarked item works on that item, not on the marked ones
        // (Shift+F10 and the Menu key work on the marked items: the caret is just where the menu opens)
        if (fromMouse && item != null && !item.IsMarked && !item.Entry.IsUpDir && pane.MarkedItems.Count > 0)
        {
            pane.RememberSelection();
            pane.MarkAll(false);
        }

        ContextMenuFacts facts = ContextMenuFactsFor(pane, item);
        var menu = new MenuFlyout();
        foreach (ContextMenuEntry entry in ContextMenuPlan.For(facts))
        {
            switch (entry.Kind)
            {
                case ContextMenuEntryKind.Command:
                    menu.Items.Add(CommandItem(entry, pane));
                    break;
                case ContextMenuEntryKind.Separator:
                    menu.Items.Add(new MenuFlyoutSeparator());
                    break;
                case ContextMenuEntryKind.ViewSubmenu:
                    menu.Items.Add(ViewSubmenu(pane));
                    break;
                case ContextMenuEntryKind.SortSubmenu:
                    menu.Items.Add(SortSubmenu(pane));
                    break;
                case ContextMenuEntryKind.ArchivePluginSubmenu:
                    if (item != null && _main.Plugins.UnpackerPlugin(item.Entry.FullPath) is { } owner)
                        menu.Items.Add(PluginSubmenu(owner, item.Entry.FullPath, pane));
                    break;
                case ContextMenuEntryKind.RunScript:
                    if (item != null)
                        menu.Items.Add(RunScriptItem(item.Entry.FullPath, facts.Script));
                    break;
                case ContextMenuEntryKind.WindowsMenu:
                    var windows = new MenuFlyoutItem
                    {
                        Text = "Windows context menu",
                        Icon = new FontIcon { Glyph = "\uE712" },
                        KeyboardAcceleratorTextOverride = "Shift+right-click",
                    };
                    windows.Click += (_, _) =>
                    {
                        // after this menu has closed: the Windows menu is modal
                        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ShowShellMenu(item, screenX, screenY));
                    };
                    menu.Items.Add(windows);
                    break;
            }
        }
        if (menu.Items.Count == 0)
            return;
        menu.Closed += (_, _) =>
        {
            if (!IsRenaming)
                FocusList();
        };
        Loc.Translate(menu);
        menu.ShowAt(FileList, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Position = position,
            ShowMode = Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowMode.Standard,
        });
    }

    /// <summary>What the context menu depends on (the menu itself is decided by Core's ContextMenuPlan).</summary>
    private ContextMenuFacts ContextMenuFactsFor(PaneViewModel pane, FileItemViewModel? item)
    {
        bool onItem = item != null && !item.Entry.IsUpDir;
        bool local = pane.IsLocalDisk;
        bool offersWindowsMenu = !pane.IsArchive && (local || item?.Drive != null); // archive items aren't files on disk
        if (item?.Drive is { } drive)
            return new ContextMenuFacts
            {
                Target = ContextMenuTarget.Drive,
                IsMountedImageDrive = Core.Interop.DiskImages.IsMountedImage(drive.Root),
                OffersWindowsMenu = offersWindowsMenu,
            };
        if (onItem && pane.IsRemote)
            return new ContextMenuFacts { Target = ContextMenuTarget.RemoteItem, IsDirectory = item!.Entry.IsDirectory, OffersWindowsMenu = offersWindowsMenu };
        if (onItem && pane.IsArchive)
            return new ContextMenuFacts
            {
                Target = ContextMenuTarget.ArchiveItem,
                IsDirectory = item!.Entry.IsDirectory,
                EditableArchive = _main!.IsEditableArchive(pane),
                OffersWindowsMenu = offersWindowsMenu,
            };
        if (onItem)
        {
            string path = item!.Entry.FullPath;
            bool directory = item.Entry.IsDirectory;
            int count = pane.MarkedItems.Count;
            bool archive = !directory && _main!.ArchiveProvider.CanHandle(path);
            return new ContextMenuFacts
            {
                Target = ContextMenuTarget.Item,
                IsDirectory = directory,
                MarkedCount = count,
                IsArchiveFile = archive,
                IsDiskImage = !directory && !archive && Core.Interop.DiskImages.IsDiskImage(path),
                MountDiskImages = _main!.Settings.General.MountDiskImages,
                Script = !directory && count <= 1 && local ? Core.Interop.ScriptRunner.HostOf(path) : Core.Interop.ScriptHost.None,
                CanUnpack = _main.Plugins.FindUnpacker(path) != null,
                HasArchivePlugin = count <= 1 && archive && _main.Plugins.UnpackerPlugin(path) != null,
                OffersWindowsMenu = offersWindowsMenu,
            };
        }
        string? imageDrive = pane.Path.Length >= 2 && pane.Path[1] == ':' && Core.Interop.DiskImages.IsMountedImage(pane.Path[..2] + "\\")
            ? pane.Path[..2] : null;
        return new ContextMenuFacts
        {
            Target = local ? ContextMenuTarget.LocalFolder : ContextMenuTarget.OtherFolder,
            MountedImageDrive = local ? imageDrive : null,
            IsRemotePanel = pane.IsRemote,
            OffersWindowsMenu = offersWindowsMenu,
        };
    }

    private MenuFlyoutItem CommandItem(ContextMenuEntry entry, PaneViewModel pane)
    {
        var item = new MenuFlyoutItem { Text = entry.Text, Icon = new FontIcon { Glyph = entry.Glyph } };
        if (entry.Bold)
            item.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        if (_main!.KeyMap.GetShortcutText(entry.Command) is { Length: > 0 } keys)
            item.KeyboardAcceleratorTextOverride = keys;
        CommandId command = entry.Command;
        item.Click += async (_, _) =>
        {
            _main.Activate(pane);
            await _main.ExecuteAsync(command);
        };
        return item;
    }

    /// <summary>Runs a script in a console window that stays open.</summary>
    private MenuFlyoutItem RunScriptItem(string script, Core.Interop.ScriptHost host)
    {
        var run = new MenuFlyoutItem
        {
            Text = host == Core.Interop.ScriptHost.PowerShell ? "Run with PowerShell" : "Run in cmd",
            Icon = new FontIcon { Glyph = "\uE756" },
        };
        run.Click += (_, _) =>
        {
            try
            {
                Core.Interop.ScriptRunner.Run(script);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _main?.Notify(Loc.F("\"{0}\" could not be run: {1}", Path.GetFileName(script), ex.Message));
            }
        };
        return run;
    }

    private MenuFlyoutSubItem ViewSubmenu(PaneViewModel pane)
    {
        var sub = new MenuFlyoutSubItem { Text = "View", Icon = new FontIcon { Glyph = "\uE8FD" } };
        (string Text, ViewMode Mode, CommandId Command)[] views =
        {
            ("Brief", ViewMode.Brief, CommandId.ViewBrief),
            ("Detailed", ViewMode.Detailed, CommandId.ViewDetailed),
            ("Icons", ViewMode.Icons, CommandId.ViewIcons),
            ("Thumbnails", ViewMode.Thumbnails, CommandId.ViewThumbnails),
            ("Tiles", ViewMode.Tiles, CommandId.ViewTiles),
        };
        foreach (var (text, mode, command) in views)
        {
            var entry = new RadioMenuFlyoutItem { Text = text, GroupName = "ContextView", IsChecked = pane.ViewMode == mode };
            if (_main?.KeyMap.GetShortcutText(command) is { Length: > 0 } keys)
                entry.KeyboardAcceleratorTextOverride = keys;
            CommandId c = command;
            entry.Click += async (_, _) =>
            {
                if (_main == null)
                    return;
                _main.Activate(pane);
                await _main.ExecuteAsync(c);
            };
            sub.Items.Add(entry);
        }
        return sub;
    }

    private MenuFlyoutSubItem SortSubmenu(PaneViewModel pane)
    {
        var sub = new MenuFlyoutSubItem { Text = "Sort by", Icon = new FontIcon { Glyph = "\uE8CB" } };
        (string Text, SortMode Mode, CommandId Command)[] sorts =
        {
            ("Name", SortMode.Name, CommandId.SortByName),
            ("Extension", SortMode.Extension, CommandId.SortByExtension),
            ("Date", SortMode.Date, CommandId.SortByDate),
            ("Size", SortMode.Size, CommandId.SortBySize),
        };
        foreach (var (text, mode, command) in sorts)
        {
            var entry = new RadioMenuFlyoutItem { Text = text, GroupName = "ContextSort", IsChecked = pane.SortMode == mode };
            if (_main?.KeyMap.GetShortcutText(command) is { Length: > 0 } keys)
                entry.KeyboardAcceleratorTextOverride = keys;
            CommandId c = command;
            entry.Click += async (_, _) =>
            {
                if (_main == null)
                    return;
                _main.Activate(pane);
                await _main.ExecuteAsync(c);
            };
            sub.Items.Add(entry);
        }
        return sub;
    }

    /// <summary>
    /// Explorer context menu for the marked items (when the clicked item is marked), the item itself,
    /// or the current directory (empty area / ".."). "Rename" starts the in-place rename.
    /// </summary>
    private void ShowShellMenu(FileItemViewModel? item, int x, int y)
    {
        if (ViewModel == null || _main == null || ViewModel.IsArchive || ViewModel.IsRemote)
            return; // inside an archive or on a server the items aren't on disk (and Explorer's ZIP folder could change the read-only archive)
        List<string> paths;
        if (item != null && item.IsMarked)
            paths = ViewModel.MarkedItems.Select(i => i.Entry.FullPath).ToList();
        else if (item?.Drive is { } drive)
            paths = new List<string> { drive.Root };
        else if (item != null && !item.Entry.IsUpDir)
            paths = new List<string> { item.Entry.FullPath };
        else if (ViewModel.IsLocalDisk)
            paths = new List<string> { ViewModel.Path };
        else
            return;

        bool shift = KeyboardState.IsDown(KeyboardState.Shift);
        string? verb = ShellContextMenu.Show(_main.WindowHandle, paths, x, y, shift);
        if (verb == "rename" && item != null)
        {
            _ = _main.ExecuteAsync(CommandId.QuickRename); // the rename box takes the focus
            return;
        }
        FocusList();
    }
}
