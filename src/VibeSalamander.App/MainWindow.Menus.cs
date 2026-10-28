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

// MainWindow: the menu bar and the Plugins menu, with their icons.
public sealed partial class MainWindow
{
    // ---- menu, toolbar, F-key bar ------------------------------------------------------------

    private async void Menu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out CommandId command))
            await _vm.ExecuteAsync(command);
        UpdateMenuChecks();
    }

    private void LeftMenu_Click(object sender, RoutedEventArgs e)
    {
        _vm.Activate(_vm.Left);
        Menu_Click(sender, e);
    }

    private void RightMenu_Click(object sender, RoutedEventArgs e)
    {
        _vm.Activate(_vm.Right);
        Menu_Click(sender, e);
    }

    /// <summary>
    /// Plugins menu, like Salamander's: the Plugins Manager, then one submenu per plugin with what it
    /// adds (Pack / Unpack for archivers, its own commands).
    /// </summary>
    private void BuildPluginsMenu()
    {
        PluginsMenu.Items.Clear();
        PluginsMenu.Items.Add(MenuItem("Plugins Manager...", nameof(CommandId.PluginsManager), "\uEA86"));
        var loaded = _vm.Plugins.Plugins.Where(p => p.Enabled && p.Error == null).ToList();
        if (loaded.Count > 0)
            PluginsMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (Services.PluginInfo plugin in loaded)
        {
            string pluginGlyph = PluginGlyph(plugin);
            var sub = new MenuFlyoutSubItem { Text = plugin.Name, Icon = new FontIcon { Glyph = pluginGlyph } };
            if (plugin.Archivers.Any(a => a.PackFormats.Count > 0))
                sub.Items.Add(MenuItem("Pack...", nameof(CommandId.Pack), "\uE7B8"));
            if (plugin.Archivers.Any(a => a.UnpackExtensions.Count > 0))
                sub.Items.Add(MenuItem("Unpack...", nameof(CommandId.Unpack), "\uE896"));
            foreach (Plugins.PluginCommand command in plugin.CommandContributors.SelectMany(c => c.Commands))
            {
                if (command.SeparatorBefore && sub.Items.Count > 0 && sub.Items[^1] is not MenuFlyoutSeparator)
                    sub.Items.Add(new MenuFlyoutSeparator());
                CommandId hostCommand = CommandId.None;
                bool isHostCommand = command.HostCommand != null && Enum.TryParse(command.HostCommand, out hostCommand);
                string glyph = isHostCommand && CommandCatalog.Glyph(hostCommand) is { Length: > 0 } commandGlyph ? commandGlyph : pluginGlyph;
                var item = new MenuFlyoutItem { Text = command.MenuText, Icon = new FontIcon { Glyph = glyph } };
                // a command answering to an app command shows that command's (customizable) key
                string? keys = isHostCommand ? _vm.KeyMap.GetShortcutText(hostCommand) : command.ShortcutText;
                if (!string.IsNullOrEmpty(keys))
                    item.KeyboardAcceleratorTextOverride = keys;
                Plugins.PluginCommand c = command;
                item.Click += async (_, _) =>
                {
                    try
                    {
                        await c.Execute(plugin.Host!);
                    }
                    catch (Exception ex)
                    {
                        Notify($"{plugin.Name}: {ex.Message}");
                    }
                };
                sub.Items.Add(item);
            }
            if (sub.Items.Count == 0)
                sub.Items.Add(new MenuFlyoutItem { Text = "(nothing to show)", IsEnabled = false });
            PluginsMenu.Items.Add(sub);
        }
        foreach (MenuFlyoutItemBase item in PluginsMenu.Items)
            Loc.Translate(item);
        UpdateMenuShortcuts();
    }

    /// <summary>
    /// Icons of the menu items that aren't app commands (by tag), and of submenus and other items (by their
    /// English text, before the menus are translated).
    /// </summary>
    private static readonly Dictionary<string, string> TagGlyphs = new()
    {
        // Commands → Open Folder
        ["Desktop"] = "\uE8B7", ["Documents"] = "\uE8A5", ["Downloads"] = "\uE896", ["Pictures"] = "\uE91B",
        ["Music"] = "\uE8D6", ["Videos"] = "\uE714", ["UserProfile"] = "\uE77B", ["Computer"] = "\uE7F4",
        ["Temp"] = "\uE81C", ["Recent"] = "\uE81C", ["Templates"] = "\uE8A5", ["SendTo"] = "\uE724",
        ["StartMenu"] = "\uE71D", ["Programs"] = "\uE71D", ["Startup"] = "\uE768", ["ControlPanel"] = "\uE713",
        ["Network"] = "\uE968", ["RecycleBin"] = "\uE74D", ["Fonts"] = "\uE8D2", ["Printers"] = "\uE749",
        // Options (Theme and Show have check marks instead)
        ["Toolbar"] = "\uE771", ["Keyboard"] = "\uE765",
    };

    private static readonly Dictionary<string, string> TextGlyphs = new()
    {
        ["Save Configuration"] = "\uE74E", ["Export Configuration..."] = "\uEDE1", ["Import Configuration..."] = "\uE8B5",
        ["Keyboard Shortcuts"] = "\uE765",
        // submenus
        ["View"] = "\uE8A9", ["Sort By"] = "\uE8CB", ["NTFS"] = "\uEDA2", ["Advanced"] = "\uE712",
        ["Open Folder"] = "\uE838", ["Theme"] = "\uE790", ["Show"] = "\uE7B3",
        ["Find"] = "\uE721", ["Compare"] = "\uE8AB", ["Sizes and Drives"] = "\uEDA2", ["Lists"] = "\uE8FD", ["Network"] = "\uE968",
    };

    /// <summary>Puts an icon before every menu item (and submenu) that has none.</summary>
    private static void ApplyMenuIcons(IEnumerable<MenuFlyoutItemBase> items)
    {
        foreach (MenuFlyoutItemBase entry in items)
        {
            switch (entry)
            {
                case MenuFlyoutSubItem sub:
                    if (sub.Icon == null && TextGlyphs.TryGetValue(sub.Text, out string? subGlyph))
                        sub.Icon = new FontIcon { Glyph = subGlyph };
                    ApplyMenuIcons(sub.Items);
                    break;
                case MenuFlyoutItem item when item.Icon == null:
                    string? glyph = null;
                    if (item.Tag is string tag && TagGlyphs.TryGetValue(tag, out string? byTag))
                        glyph = byTag;
                    else if (item.Tag is string command && Enum.TryParse(command, out CommandId id) && id != CommandId.None)
                        glyph = CommandCatalog.Glyph(id);
                    else if (TextGlyphs.TryGetValue(item.Text, out string? byText))
                        glyph = byText;
                    if (!string.IsNullOrEmpty(glyph))
                        item.Icon = new FontIcon { Glyph = glyph };
                    break;
            }
        }
    }

    /// <summary>A plugin's icon in the Plugins menu: its own, or one for what it adds.</summary>
    private static string PluginGlyph(Services.PluginInfo plugin)
    {
        if (plugin.IconGlyph is { Length: > 0 } own)
            return own;
        if (plugin.Archivers.Count > 0)
            return "\uF012"; // a zipped folder
        if (plugin.FileSystems.Count > 0)
            return "\uE968"; // network
        return "\uEA86"; // a puzzle piece
    }

    private MenuFlyoutItem MenuItem(string text, string tag, string glyph)
    {
        var item = new MenuFlyoutItem { Text = text, Tag = tag, Icon = new FontIcon { Glyph = glyph } };
        item.Click += Menu_Click;
        return item;
    }
}
