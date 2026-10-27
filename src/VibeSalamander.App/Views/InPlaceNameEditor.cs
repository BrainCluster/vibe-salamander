// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using VirtualKey = Windows.System.VirtualKey;
using VibeSalamander.Services;
using VibeSalamander.ViewModels;

namespace VibeSalamander.Views;

/// <summary>
/// Editing a name in place (F2, a new folder, a new file): the row's name turns into an edit box. Enter or a click
/// elsewhere confirms, Esc cancels. One edit at a time per panel. The row templates show the box (x:Load on the
/// item's IsRenaming) and send its KeyDown / GotFocus / LostFocus here.
/// </summary>
internal sealed class InPlaceNameEditor
{
    private readonly ListView _list;
    private readonly Action _focusList;
    private TaskCompletionSource<string?>? _result;
    private TextBox? _box;
    private bool _boxHadFocus;

    /// <param name="list">The panel's file list.</param>
    /// <param name="focusList">Gives the list the keyboard focus back when the edit ends.</param>
    public InPlaceNameEditor(ListView list, Action focusList)
    {
        _list = list;
        _focusList = focusList;
    }

    public bool IsEditing => _box != null;

    private DispatcherQueue Dispatcher => _list.DispatcherQueue;

    /// <summary>Turns the item's name into an edit box; returns the new name, or null when cancelled.</summary>
    /// <param name="selectWholeName">Select the extension too (otherwise only the name before the last dot).</param>
    public async Task<string?> EditAsync(PaneViewModel pane, FileItemViewModel item, bool selectWholeName = false)
    {
        if (IsEditing)
            return null;
        _list.ScrollIntoView(item);
        _list.UpdateLayout();
        if (_list.ContainerFromItem(item) is null)
        {
            // a list that was just reloaded (F7 / Shift+F4 on the new item) may realize the row a pass later
            var layoutPass = new TaskCompletionSource();
            if (Dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => layoutPass.TrySetResult()))
                await layoutPass.Task;
            if (IsEditing)
                return null;
            _list.ScrollIntoView(item);
            _list.UpdateLayout();
        }
        if (_list.ContainerFromItem(item) is not ListViewItem container)
            return null;

        pane.IsEditing = true; // hold back refreshes: rebuilding the rows would end the edit
        item.IsRenaming = true;
        container.UpdateLayout();
        TextBox? box = VisualTree.FindDescendant<TextBox>(container, b => b.Tag as string == "RenameBox");
        if (box == null)
        {
            item.IsRenaming = false;
            pane.IsEditing = false;
            return null;
        }

        _box = box;
        // asynchronous continuations: the code after the await unloads the box (x:Load), which must not
        // happen synchronously inside the box's own KeyDown/LostFocus handler
        _result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _boxHadFocus = false;
        // the row was recycled or the list rebuilt (theme, sort) under the edit: end it instead of waiting forever
        void BoxUnloaded(object sender, RoutedEventArgs e)
        {
            box.Unloaded -= BoxUnloaded;
            if (_box == box && !box.IsLoaded)
                _result?.TrySetResult(null);
        }
        box.Unloaded += BoxUnloaded;
        box.FontSize = UiState.FontSize;
        if (UiState.PanelFont is { } font)
            box.FontFamily = font;
        else
            box.ClearValue(Control.FontFamilyProperty);
        box.Text = item.Entry.Name;
        int dot = item.Entry.IsDirectory || selectWholeName ? -1 : item.Entry.Name.LastIndexOf('.');
        // focus after menus/flyouts have closed and returned their focus
        Dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_box != box)
                return;
            box.Focus(FocusState.Programmatic);
            if (dot > 0)
                box.Select(0, dot);
            else
                box.SelectAll();
        });

        string? result;
        try
        {
            result = await _result.Task;
        }
        finally
        {
            _box = null;
            _result = null;
            item.IsRenaming = false;
        }
        _focusList();
        // refreshes held back during the edit run now (the rename that follows reloads the panel anyway)
        pane.IsEditing = false;
        return result;
    }

    public void BoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender != _box)
            return;
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            _result?.TrySetResult(((TextBox)sender).Text);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            _result?.TrySetResult(null);
        }
        else if (e.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.PageUp or VirtualKey.PageDown)
        {
            e.Handled = true; // keep the caret (and the edit) on this row
        }
    }

    public void BoxGotFocus(object sender)
    {
        if (sender == _box)
            _boxHadFocus = true;
    }

    /// <summary>Clicking elsewhere confirms the new name, like Explorer (only after the box really had the focus).</summary>
    public void BoxLostFocus(object sender)
    {
        if (sender != _box || !_boxHadFocus)
            return;
        var box = (TextBox)sender;
        // Focus that moved into a popup is not "clicking elsewhere": the box's own context menu, or a
        // question from a background transfer. The edit goes on and gets the focus back when it closes.
        if (PopupWithFocus() is { } popup)
        {
            void PopupClosed(object? s, object args)
            {
                popup.Closed -= PopupClosed;
                Dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    if (_box == box && FocusManager.GetFocusedElement(_list.XamlRoot) != box)
                        box.Focus(FocusState.Programmatic);
                });
            }
            popup.Closed += PopupClosed;
            return;
        }
        _result?.TrySetResult(box.Text);
    }

    /// <summary>The open popup (flyout, dialog) that contains the focused element, if any.</summary>
    private Popup? PopupWithFocus()
    {
        XamlRoot? root = _list.XamlRoot;
        if (root == null || FocusManager.GetFocusedElement(root) is not DependencyObject focused)
            return null;
        foreach (Popup popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
            if (popup.Child != null && VisualTree.IsInside(focused, popup.Child))
                return popup;
        return null;
    }
}

/// <summary>Finding elements in the visual tree.</summary>
internal static class VisualTree
{
    /// <summary>The first element below <paramref name="root"/> (depth first) of type T that matches.</summary>
    public static T? FindDescendant<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t && match(t))
                return t;
            if (FindDescendant(child, match) is T found)
                return found;
        }
        return null;
    }

    /// <summary>The element itself or its nearest parent of type T.</summary>
    public static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        for (DependencyObject? d = element; d != null; d = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(d))
            if (d is T t)
                return t;
        return null;
    }

    /// <summary>True when <paramref name="element"/> is <paramref name="container"/> or inside it.</summary>
    public static bool IsInside(DependencyObject element, DependencyObject container)
    {
        for (DependencyObject? d = element; d != null; d = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, container))
                return true;
        return false;
    }
}
