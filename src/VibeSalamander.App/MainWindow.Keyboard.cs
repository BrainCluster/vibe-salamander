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

// MainWindow: the keyboard (shortcuts of the key map, the F-key bar's modifiers).
public sealed partial class MainWindow
{
    // ---- keyboard ----------------------------------------------------------------------------

    private static KeyModifiers CurrentModifiers() => Services.KeyboardState.CurrentModifiers();

    private static bool IsModifierKey(Windows.System.VirtualKey key) => key is
        Windows.System.VirtualKey.Control or Windows.System.VirtualKey.LeftControl or Windows.System.VirtualKey.RightControl or
        Windows.System.VirtualKey.Menu or Windows.System.VirtualKey.LeftMenu or Windows.System.VirtualKey.RightMenu or
        Windows.System.VirtualKey.Shift or Windows.System.VirtualKey.LeftShift or Windows.System.VirtualKey.RightShift;

    private void RootGrid_PreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (IsModifierKey(e.Key))
            UpdateFunctionKeyBar(CurrentModifiers());
    }

    private async void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // the bottom bar shows what the F-keys do with the modifiers held right now
        if (IsModifierKey(e.Key))
        {
            UpdateFunctionKeyBar(CurrentModifiers());
            return;
        }
        if (_dialogOpen || Content?.XamlRoot == null)
            return;
        // menus, flyouts and dialogs handle their own keys
        if (VisualTreeHelper.GetOpenPopupsForXamlRoot(Content.XamlRoot).Count > 0)
            return;
        var focused = FocusManager.GetFocusedElement(Content.XamlRoot);
        if (focused is TextBox or MenuBar or MenuBarItem)
            return;

        int key = (int)e.Key;
        KeyModifiers mods = CurrentModifiers();
        FilePane activeView = PaneView(_vm.Active);

        if (key == VK.Back && mods == KeyModifiers.None && activeView.QuickSearchBackspace())
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            _vm.Active.EndQuickSearch();
            e.Handled = true;
            return;
        }

        CommandId command = _vm.KeyMap.Resolve(key, mods);
        // Configuration → Panels: Shift+1…9 goes to the hot paths too
        if (command == CommandId.None && mods == KeyModifiers.Shift && key >= VK.D0 + 1 && key <= VK.D0 + 9 && _vm.Settings.Panels.ShiftDigitGoesToHotPath)
            command = CommandId.GoToHotPath0 + (key - VK.D0);
        if (command == CommandId.None && key == VK.Tab && mods == KeyModifiers.Shift)
            command = CommandId.SwitchPanel;
        if (command == CommandId.None)
            return;

        e.Handled = true;
        _vm.Active.EndQuickSearch();
        if (command == CommandId.MainMenu)
        {
            FocusMenu(); // the menu bar is the window's, not a command of the panels
            return;
        }
        await _vm.ExecuteAsync(command);
        UpdateMenuChecks();
    }

    private async void CommandLine_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            string text = CommandLine.Text;
            CommandLine.Text = string.Empty;
            _historyIndex = -1;
            await _vm.RunCommandLineAsync(text);
            PaneView(_vm.Active).FocusList();
        }
        else if (e.Key is Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down && _vm.Settings.History.CommandLineHistory)
        {
            // Configuration → History: Up / Down recall earlier commands
            List<string> history = _vm.Settings.CommandLineHistory;
            if (history.Count == 0)
                return;
            e.Handled = true;
            _historyIndex = e.Key == Windows.System.VirtualKey.Up
                ? Math.Min(_historyIndex + 1, history.Count - 1)
                : Math.Max(_historyIndex - 1, -1);
            CommandLine.Text = _historyIndex >= 0 ? history[_historyIndex] : string.Empty;
            CommandLine.SelectionStart = CommandLine.Text.Length;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            CommandLine.Text = string.Empty;
            PaneView(_vm.Active).FocusList();
        }
    }
}
