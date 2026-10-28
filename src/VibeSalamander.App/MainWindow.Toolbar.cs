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

// MainWindow: the top toolbar, hot path bar, user menu bar and the F-key bar.
public sealed partial class MainWindow
{
    // ---- top toolbar, hot path bar, user menu bar (phase 3C: built from the settings) ---------

    private Style? ToolButtonStyle =>
        Application.Current.Resources.TryGetValue("VibeToolButtonStyle", out object? style) ? style as Style : null;

    private FontFamily IconFont =>
        Application.Current.Resources.TryGetValue("VibeIconFont", out object? font) && font is FontFamily f ? f : new FontFamily("Segoe Fluent Icons");

    private string WithShortcut(string text, CommandId command) =>
        _vm.KeyMap.GetShortcutText(command) is string keys ? $"{text} ({keys})" : text;

    /// <summary>The top toolbar from Settings.Toolbar (command names, "-" separators, "ViewMenu").</summary>
    private void BuildToolbar()
    {
        ToolbarPanel.Children.Clear();
        bool sliderAdded = false;
        foreach (string id in _vm.Settings.Toolbar)
        {
            if (id == CommandCatalog.SeparatorId)
            {
                ToolbarPanel.Children.Add(new AppBarSeparator { Padding = new Thickness(2, 0, 2, 0) });
                continue;
            }
            if (id == CommandCatalog.ViewMenuId)
            {
                ToolbarPanel.Children.Add(BuildViewMenuButton());
                continue;
            }
            if (!Enum.TryParse(id, out CommandId command) || command == CommandId.None)
                continue;
            var button = new Button
            {
                Style = ToolButtonStyle,
                Tag = id,
                Content = new FontIcon { Glyph = CommandCatalog.Glyph(command), FontFamily = IconFont, FontSize = 15 },
            };
            ToolTipService.SetToolTip(button, WithShortcut(CommandCatalog.DisplayName(command), command));
            button.Click += Toolbar_Click;
            ToolbarPanel.Children.Add(button);
            if (command == CommandId.ToggleDensity && !sliderAdded)
            {
                ToolbarPanel.Children.Add(BuildThumbnailSlider());
                sliderAdded = true;
            }
        }
        // one slider for both panels: next to the density button, or at the end when that isn't on the toolbar
        if (!sliderAdded)
            ToolbarPanel.Children.Add(BuildThumbnailSlider());
        Loc.Translate(ToolbarPanel);
        UpdateThumbnailSlider();
    }

    private async void Toolbar_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out CommandId command))
        {
            await _vm.ExecuteAsync(command);
            UpdateMenuChecks();
            PaneView(_vm.Active).FocusList();
        }
    }

    // F-key bar: F1..F12 relabelled for the held modifiers (Ctrl, Alt, Shift and combinations)

    private readonly Button[] _fkeyButtons = new Button[12];
    private readonly TextBlock[] _fkeyLabels = new TextBlock[12];
    private readonly Border[] _fkeyCaps = new Border[12];
    private readonly Action?[] _fkeyActions = new Action?[12];
    private KeyModifiers _fkeyModifiers = (KeyModifiers)(-1);

    /// <summary>Short Salamander-style labels for the bottom bar.</summary>
    private static string ShortLabel(CommandId command) => command switch
    {
        CommandId.Help => "Help",
        CommandId.ChangeDriveLeft => "Left",
        CommandId.ChangeDriveRight => "Right",
        CommandId.QuickRename => "Rename",
        CommandId.View => "View",
        CommandId.Edit => "Edit",
        CommandId.EditNewFile => "NewFile",
        CommandId.Copy => "Copy",
        CommandId.Pack => "Pack",
        CommandId.CalculateChecksums => "Checksum",
        CommandId.VerifyChecksums => "Verify",
        CommandId.Unpack => "Unpack",
        CommandId.Move => "Move",
        CommandId.CreateDirectory => "MakeDir",
        CommandId.ChangeDirectory => "ChngDir",
        CommandId.Delete => "Delete",
        CommandId.DeletePermanently => "DelPerm",
        CommandId.Refresh => "Refresh",
        CommandId.MaximizePanel => "Maximize",
        CommandId.SortByName => "Name",
        CommandId.SortByExtension => "Ext",
        CommandId.SortByDate => "Date",
        CommandId.SortBySize => "Size",
        CommandId.SortByAttributes => "Attrs",
        CommandId.Filter => "Filter",
        CommandId.OpenWith => "OpenWith",
        CommandId.ChangeAttributes => "Attrib",
        CommandId.ChangeCase => "Case",
        CommandId.FindFiles => "Find",
        CommandId.ContextMenu => "Context",
        CommandId.OpenActiveFolder => "Explorer",
        CommandId.Properties => "Props",
        CommandId.Exit => "Exit",
        CommandId.DriveInformation => "DrvInfo",
        CommandId.CalculateOccupiedSpace => "DirSize",
        CommandId.CalculateDirectorySizes => "AllSizes",
        CommandId.CompareDirectories => "Compare",
        CommandId.UserMenu => "UsrMenu",
        CommandId.SaveSelection => "SaveSel",
        CommandId.LoadSelection => "LoadSel",
        CommandId.OpenedFilesList => "Files",
        CommandId.WorkingDirectoriesList => "Dirs",
        CommandId.ConnectNetworkDrive => "NetCon",
        CommandId.ConnectFtp => "FTP",
        CommandId.DiskMap => "DiskMap",
        CommandId.TestArchive => "Test",
        CommandId.MakeSelfExtractingArchive => "SFX",
        CommandId.ArchiveComment => "Comment",
        CommandId.DisconnectNetworkDrive => "NetDis",
        CommandId.SharedDirectories => "Shares",
        CommandId.MainMenu => "Menu",
        CommandId.ViewBrief => "Brief",
        CommandId.ViewDetailed => "Full",
        CommandId.FindDuplicates => "Dupes",
        CommandId.Duplicate => "Dupl",
        CommandId.MultiRename => "MRename",
        CommandId.HotPathList => "HotPaths",
        CommandId.CommandHistory => "History",
        _ => command.ToString(),
    };

    private static string ModifierText(KeyModifiers mods)
    {
        var parts = new List<string>();
        if ((mods & KeyModifiers.Ctrl) != 0) parts.Add("Ctrl");
        if ((mods & KeyModifiers.Alt) != 0) parts.Add("Alt");
        if ((mods & KeyModifiers.Shift) != 0) parts.Add("Shift");
        return string.Join("+", parts);
    }

    private void BuildFunctionKeyBar()
    {
        for (int i = 0; i < 12; i++)
        {
            FKeyBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var caption = new TextBlock { Text = "F" + (i + 1), FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
            var cap = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x24, 0x80, 0x80, 0x80)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 0, 4, 1),
                MinWidth = 24,
                Child = caption,
            };
            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            panel.Children.Add(cap);
            panel.Children.Add(label);
            var button = new Button
            {
                Content = panel,
                Style = Application.Current.Resources.TryGetValue("VibeFKeyButtonStyle", out object? style) ? style as Style : null,
            };
            int index = i;
            button.Click += (_, _) => _fkeyActions[index]?.Invoke();
            Grid.SetColumn(button, i);
            FKeyBar.Children.Add(button);
            _fkeyButtons[i] = button;
            _fkeyLabels[i] = label;
            _fkeyCaps[i] = cap;
        }
        UpdateFunctionKeyBar(KeyModifiers.None);
    }

    /// <summary>Relabels F1..F12 for the given modifiers from the key map (keys without a command are blank).</summary>
    private void UpdateFunctionKeyBar(KeyModifiers mods)
    {
        if (mods == _fkeyModifiers || _fkeyButtons[0] == null)
            return;
        _fkeyModifiers = mods;
        string prefix = ModifierText(mods);
        bool held = mods != KeyModifiers.None;
        var accent = Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out object? a) ? a as Brush : null;
        var neutral = new SolidColorBrush(Windows.UI.Color.FromArgb(0x24, 0x80, 0x80, 0x80));

        for (int i = 0; i < 12; i++)
        {
            int key = VK.F1 + i;
            CommandId command = _vm.KeyMap.Resolve(key, mods);
            string label;
            Action? action = null;
            if (command == CommandId.MainMenu)
            {
                label = "Menu";
                action = FocusMenu;
            }
            else if (command != CommandId.None)
            {
                label = ShortLabel(command);
                CommandId c = command;
                action = async () => { await _vm.ExecuteAsync(c); UpdateMenuChecks(); PaneView(_vm.Active).FocusList(); };
            }
            else if (mods == KeyModifiers.None && key == VK.F10)
            {
                label = "Menu";
                action = FocusMenu;
            }
            else if (mods == KeyModifiers.Alt && key == VK.F4)
            {
                label = "Exit";
                action = Exit;
            }
            else
            {
                label = string.Empty;
            }

            _fkeyLabels[i].Text = Loc.T(label);
            _fkeyActions[i] = action;
            _fkeyButtons[i].IsEnabled = action != null;
            // with a modifier held, the active keys get accent key caps
            bool highlight = held && action != null && accent != null;
            var capText = (TextBlock)_fkeyCaps[i].Child;
            _fkeyCaps[i].Background = highlight ? accent : neutral;
            if (highlight && Application.Current.Resources.TryGetValue("TextOnAccentFillColorPrimaryBrush", out object? fg) && fg is Brush onAccent)
                capText.Foreground = onAccent;
            else
                capText.ClearValue(TextBlock.ForegroundProperty);
            string chord = held ? $"{prefix}+F{i + 1}" : $"F{i + 1}";
            ToolTipService.SetToolTip(_fkeyButtons[i], action != null ? $"{chord}  {label}" : null);
        }
    }

    private void FocusMenu()
    {
        if (MainMenu.Items.Count > 0)
            MainMenu.Items[0].Focus(FocusState.Keyboard);
    }

    private async void Shortcuts_Click(object sender, RoutedEventArgs e)
    {
        var lines = Enum.GetValues<CommandId>()
            .Select(c => (c, _vm.KeyMap.GetShortcutText(c)))
            .Where(t => t.Item2 != null)
            .Select(t => $"{t.Item2,-16} {t.c}");
        await ShowMessageAsync("Keyboard Shortcuts", string.Join("\n", lines));
    }
}
