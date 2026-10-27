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

// FilePane: mouse and keyboard in the list: clicks, marking with Ctrl/Shift, Shift+arrows, quick search.
public sealed partial class FilePane
{
    // ---- mouse options (Configuration → Panels / Appearance) -------------------------------

    private int _caretAtPress = -1;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _slowClickTimer;

    /// <summary>Single click opens (Appearance) or a slow second click on the focused item renames (Panels).</summary>
    private async void List_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (ViewModel == null || _main == null || IsRenaming || e.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse)
            return;
        if (KeyboardState.CurrentModifiers() != KeyModifiers.None)
            return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not FileItemViewModel item)
            return;
        int index = IndexOf(item);
        if (index < 0)
            return;
        if (_main.Settings.Appearance.SingleClickOpens)
        {
            ViewModel.CaretIndex = index;
            await _main.ExecuteAsync(CommandId.Open);
            return;
        }
        if (!_main.Settings.Panels.SlowClickRename || index != _caretAtPress || item.Entry.IsUpDir || item.IsDrive)
            return;
        // like Explorer: only a click on the name itself renames (not the icon, size, date or empty space)
        if (e.OriginalSource is not TextBlock { Tag: "NameText" })
            return;
        // like Explorer: wait out the double-click time; a double-click cancels the rename
        if (_slowClickTimer == null)
        {
            _slowClickTimer = DispatcherQueue.CreateTimer();
            _slowClickTimer.IsRepeating = false;
            _slowClickTimer.Tick += async (_, _) =>
            {
                if (ViewModel != null && _main != null && !IsRenaming && ViewModel.CaretIndex == _slowClickIndex)
                    await _main.ExecuteAsync(CommandId.QuickRename);
            };
        }
        _slowClickIndex = index;
        _slowClickTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(300, GetDoubleClickTime()) + 50);
        _slowClickTimer.Stop();
        _slowClickTimer.Start();
    }

    private int _slowClickIndex = -1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    private void List_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel == null || IsRenaming || !e.GetCurrentPoint(FileList).Properties.IsLeftButtonPressed)
            return;
        _marquee.Pressed(e);
        bool ctrl = KeyboardState.IsDown(KeyboardState.Control);
        bool shift = KeyboardState.IsDown(KeyboardState.Shift);
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not FileItemViewModel item)
            return;
        int index = IndexOf(item);
        if (index < 0)
            return;
        // was the clicked item focused before this click? (the list may already have moved the caret)
        bool movedByThisClick = (DateTime.UtcNow - _caretChangedAt).TotalMilliseconds < 200 && ViewModel.CaretIndex == index;
        _caretAtPress = movedByThisClick ? _previousCaret : ViewModel.CaretIndex;
        if (ctrl && !shift)
        {
            ViewModel.ToggleMark(index); // Ctrl+click: mark / unmark one item
        }
        else if (shift && !ctrl)
        {
            // Shift+click: mark everything from the caret (before the click) to the clicked item
            int anchor = _shiftAnchor >= 0 ? _shiftAnchor : ViewModel.CaretIndex;
            if (anchor >= 0)
                ViewModel.MarkRange(anchor, index, mark: true);
        }
    }

    /// <summary>A click anywhere in the panel activates it; a click in the list area also gives the list the keyboard.</summary>
    private void Pane_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel == null || _main == null)
            return;
        _main.Activate(ViewModel);
        // the empty area below the items isn't focusable by itself: put the focus on the caret item
        if (e.OriginalSource is DependencyObject source && IsInside(source, FileList) && !IsRenaming &&
            FindAncestor<TextBox>(source) == null)
            FocusList();
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject => VisualTree.FindAncestor<T>(element);

    /// <summary>
    /// Shift+Up/Down/PgUp/PgDn/Home/End mark while moving, like Salamander: each item the caret leaves is
    /// marked (or unmarked when it was marked, so going back undoes it).
    /// </summary>
    private void List_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel != null && !IsRenaming && e.Key is (VirtualKey.Left or VirtualKey.Right) &&
            ViewModel.EffectiveViewMode == ViewMode.Detailed && KeyboardState.CurrentModifiers() == KeyModifiers.None)
        {
            e.Handled = true; // one row per line: Left / Right don't move the caret (like a plain list)
            return;
        }
        if (ViewModel == null || IsRenaming || KeyboardState.CurrentModifiers() != KeyModifiers.Shift)
            return;
        ViewMode view = ViewModel.EffectiveViewMode;
        if (view is not (ViewMode.Detailed or ViewMode.Brief))
            return; // in the grid views Up/Down jump rows; Ctrl+click / Shift+click mark there
        int count = ViewModel.Items.Count;
        int caret = ViewModel.CaretIndex;
        if (count == 0 || caret < 0)
            return;
        int page = Math.Max(1, (int)(FileList.ActualHeight / Math.Max(1, UiState.RowHeight)) - 1);
        int target = e.Key switch
        {
            VirtualKey.Up => caret - 1,
            VirtualKey.Down => caret + 1,
            VirtualKey.PageUp => caret - page,
            VirtualKey.PageDown => caret + page,
            VirtualKey.Home => 0,
            VirtualKey.End => count - 1,
            _ => int.MinValue,
        };
        if (target == int.MinValue)
            return;
        e.Handled = true;
        target = Math.Clamp(target, 0, count - 1);
        bool mark = !ViewModel.Items[caret].IsMarked;
        bool jump = e.Key is VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Home or VirtualKey.End;
        if (target == caret)
            ViewModel.MarkRange(caret, caret, mark); // at the first/last item: mark it
        else if (jump && (target == 0 || target == count - 1))
            ViewModel.MarkRange(caret, target, mark); // to the very start/end: including the last one
        else
            ViewModel.MarkRange(caret, target > caret ? target - 1 : target + 1, mark);
        ApplyCaret(target, focus: true);
    }

    private int IndexOf(FileItemViewModel item)
    {
        var items = ViewModel?.Items;
        if (items == null)
            return -1;
        for (int i = 0; i < items.Count; i++)
            if (ReferenceEquals(items[i], item))
                return i;
        return -1;
    }

    // ---- quick search ----------------------------------------------------------------------

    private bool _quickSearchMoving;

    private void List_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        if (ViewModel == null || IsRenaming)
            return; // typing goes to the rename box
        char c = args.Character;
        if (char.IsControl(c))
            return;
        if (c == ' ' && ViewModel.QuickSearch.Length == 0)
            return; // Space marks the item
        bool ctrl = Services.KeyboardState.IsDown(Services.KeyboardState.Control);
        bool alt = Services.KeyboardState.IsDown(Services.KeyboardState.Alt);
        bool toCommandLine = _main?.Settings.Panels.TypingToCommandLine == true;
        if (ctrl || (alt && !toCommandLine))
            return;
        if (c is '+' or '-' or '*' or '/' && ViewModel.QuickSearch.Length == 0)
            return; // numeric keypad commands
        if (toCommandLine && !alt && ViewModel.QuickSearch.Length == 0)
        {
            // Configuration → Panels: typing goes to the command line (Alt+letter quick-searches)
            TypedToCommandLine?.Invoke(this, c);
            args.Handled = true;
            return;
        }

        int index = ViewModel.QuickSearchType(c);
        if (index >= 0)
            MoveCaretForQuickSearch(index);
        args.Handled = true;
    }

    /// <summary>A character typed in the panel that belongs to the command line (Configuration → Panels).</summary>
    public event EventHandler<char>? TypedToCommandLine;

    /// <summary>Backspace while quick searching; returns false when no quick search is active.</summary>
    public bool QuickSearchBackspace()
    {
        if (ViewModel == null || ViewModel.QuickSearch.Length == 0)
            return false;
        int index = ViewModel.QuickSearchBackspace();
        if (index >= 0)
            MoveCaretForQuickSearch(index);
        return true;
    }

    private void MoveCaretForQuickSearch(int index)
    {
        _quickSearchMoving = true;
        try
        {
            ApplyCaret(index, focus: true);
        }
        finally
        {
            _quickSearchMoving = false;
        }
    }
}
