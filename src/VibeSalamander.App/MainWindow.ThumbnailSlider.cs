// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using VibeSalamander.Core.Commands;
using VibeSalamander.Core.Panels;
using VibeSalamander.Core.Settings;
using VibeSalamander.Services;
using VibeSalamander.ViewModels;
using VibeSalamander.Views;

namespace VibeSalamander;

// MainWindow: the thumbnail size slider (toolbar and Configuration → Appearance).
public sealed partial class MainWindow
{
    // ---- the thumbnail size slider (toolbar and Configuration → Appearance) --------------------------------

    private Slider? _thumbnailSlider;
    private bool _syncingThumbnailSlider;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _thumbnailSaveTimer;

    /// <summary>The thumbnail size of both panels; on the toolbar while either panel shows thumbnails.</summary>
    private Slider BuildThumbnailSlider()
    {
        Slider slider = ThumbnailSlider.Create(150, size =>
        {
            if (!_syncingThumbnailSlider)
                SetThumbnailSize(size);
        });
        slider.Margin = new Thickness(8, 0, 8, 0);
        slider.IsTabStop = false;
        ToolTipService.SetToolTip(slider, "Thumbnail size");
        _thumbnailSlider = slider;
        return slider;
    }

    private void UpdateThumbnailSlider()
    {
        if (_thumbnailSlider == null)
            return;
        bool thumbnails = _vm.Left.EffectiveViewMode == ViewMode.Thumbnails || _vm.Right.EffectiveViewMode == ViewMode.Thumbnails;
        _thumbnailSlider.Visibility = thumbnails ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetThumbnailSize(int size)
    {
        size = Math.Clamp(size, UiState.MinThumbnailSize, UiState.MaxThumbnailSize);
        if (_vm.Settings.Appearance.ThumbnailSize == size)
            return;
        _vm.Settings.Appearance.ThumbnailSize = size;
        if (_thumbnailSlider != null && (int)Math.Round(_thumbnailSlider.Value) != size)
        {
            _syncingThumbnailSlider = true;
            _thumbnailSlider.Value = size;
            _syncingThumbnailSlider = false;
        }
        LeftPane.ApplyThumbnailSize();
        RightPane.ApplyThumbnailSize();
        // once the slider stops: sharper pictures for larger sizes, and the setting saved
        if (_thumbnailSaveTimer == null)
        {
            _thumbnailSaveTimer = DispatcherQueue.CreateTimer();
            _thumbnailSaveTimer.Interval = TimeSpan.FromMilliseconds(300);
            _thumbnailSaveTimer.IsRepeating = false;
            _thumbnailSaveTimer.Tick += (_, _) =>
            {
                LeftPane.ReloadThumbnails();
                RightPane.ReloadThumbnails();
                _vm.SaveSettings();
            };
        }
        _thumbnailSaveTimer.Stop();
        _thumbnailSaveTimer.Start();
    }

    private Button BuildViewMenuButton()
    {
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        foreach (var (text, command, glyph) in new[]
                 {
                     ("Brief", CommandId.ViewBrief, "\uE8A9"), ("Detailed", CommandId.ViewDetailed, "\uE8FD"),
                     ("Icons", CommandId.ViewIcons, "\uE80A"), ("Thumbnails", CommandId.ViewThumbnails, "\uE91B"),
                     ("Tiles", CommandId.ViewTiles, "\uECA5"),
                 })
        {
            var item = new MenuFlyoutItem
            {
                Text = text,
                Tag = command.ToString(),
                Icon = new FontIcon { Glyph = glyph },
                KeyboardAcceleratorTextOverride = _vm.KeyMap.GetShortcutText(command) ?? string.Empty,
            };
            item.Click += Toolbar_Click;
            menu.Items.Add(item);
        }
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        panel.Children.Add(new FontIcon { Glyph = "\uE8FD", FontFamily = IconFont, FontSize = 15 });
        panel.Children.Add(new FontIcon { Glyph = "\uE70D", FontFamily = IconFont, FontSize = 8 });
        var button = new Button { Style = ToolButtonStyle, Content = panel, Flyout = menu };
        ToolTipService.SetToolTip(button, "View of the active panel (Alt+1…5)");
        return button;
    }

    /// <summary>Hot path bar (set hot paths) and user menu bar (user menu commands).</summary>
    private void BuildQuickBars()
    {
        HotPathBar.Children.Clear();
        var hotPaths = _vm.Settings.HotPaths;
        for (int i = 1; i < hotPaths.Count && i < 10; i++)
        {
            string path = hotPaths[i];
            if (string.IsNullOrEmpty(path))
                continue;
            string custom = i < _vm.Settings.HotPathNames.Count ? _vm.Settings.HotPathNames[i] : string.Empty;
            string label = !string.IsNullOrWhiteSpace(custom) ? custom
                : path == Core.FileSystem.ThisPcProvider.RootPath ? Loc.T("This PC")
                : Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } name ? name : path;
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(new FontIcon { Glyph = "\uE734", FontFamily = IconFont, FontSize = 12 });
            content.Children.Add(new TextBlock { Text = $"{i} {label}", FontSize = 12 });
            var button = new Button { Style = ToolButtonStyle, Content = content, Tag = (CommandId.GoToHotPath0 + i).ToString() };
            ToolTipService.SetToolTip(button, $"{path}  (Ctrl+{i})");
            button.Click += Toolbar_Click;
            HotPathBar.Children.Add(button);
        }

        UserMenuBar.Children.Clear();
        foreach (Core.Tools.UserMenuItem item in _vm.Settings.UserMenu)
        {
            if (item.IsSeparator)
            {
                UserMenuBar.Children.Add(new AppBarSeparator { Padding = new Thickness(2, 0, 2, 0) });
                continue;
            }
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(new FontIcon { Glyph = "\uE756", FontFamily = IconFont, FontSize = 12 });
            content.Children.Add(new TextBlock { Text = item.Name, FontSize = 12 });
            var button = new Button { Style = ToolButtonStyle, Content = content };
            ToolTipService.SetToolTip(button, $"{item.Command} {item.Arguments}".Trim());
            Core.Tools.UserMenuItem command = item;
            button.Click += (_, _) =>
            {
                _vm.RunUserMenuItem(command);
                PaneView(_vm.Active).FocusList();
            };
            UserMenuBar.Children.Add(button);
        }
        ApplyBars();
    }

    /// <summary>Menu shortcut texts follow the (customizable) key map.</summary>
    private void UpdateMenuShortcuts()
    {
        void Walk(IList<MenuFlyoutItemBase> items)
        {
            foreach (MenuFlyoutItemBase item in items)
            {
                if (item is MenuFlyoutSubItem sub)
                    Walk(sub.Items);
                else if (item is MenuFlyoutItem { Tag: string tag } menuItem && Enum.TryParse(tag, out CommandId command) &&
                         command != CommandId.None && !int.TryParse(tag, out _))
                    menuItem.KeyboardAcceleratorTextOverride = _vm.KeyMap.GetShortcutText(command) ??
                        (command == CommandId.Exit ? "Alt+F4" : string.Empty); // Alt+F4 is the system's, not in the key map
            }
        }
        foreach (MenuBarItem top in MainMenu.Items)
            Walk(top.Items);
    }
}
