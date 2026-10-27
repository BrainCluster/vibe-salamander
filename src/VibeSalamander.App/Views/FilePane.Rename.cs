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

// FilePane: in-place rename (F2).
public sealed partial class FilePane
{
    // ---- in-place rename (F2) --------------------------------------------------------------

    private TaskCompletionSource<string?>? _renameTcs;
    private TextBox? _renameBox;
    private bool _renameHadFocus;

    private bool IsRenaming => _renameBox != null;

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T t && match(t))
                return t;
            if (FindDescendant(child, match) is T found)
                return found;
        }
        return null;
    }

    /// <summary>Turns the item's name label into an edit box; returns the new name or null when cancelled.</summary>
    public async Task<string?> EditNameInPlaceAsync(FileItemViewModel item, bool selectWholeName = false)
    {
        if (ViewModel == null || IsRenaming)
            return null;
        FileList.ScrollIntoView(item);
        FileList.UpdateLayout();
        if (FileList.ContainerFromItem(item) is null)
        {
            // a list that was just reloaded (F7 / Shift+F4 on the new item) may realize the row a pass later
            var layoutPass = new TaskCompletionSource();
            if (DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => layoutPass.TrySetResult()))
                await layoutPass.Task;
            if (IsRenaming)
                return null;
            FileList.ScrollIntoView(item);
            FileList.UpdateLayout();
        }
        if (FileList.ContainerFromItem(item) is not ListViewItem container)
            return null;

        ViewModel.IsEditing = true; // hold back refreshes: rebuilding the rows would end the edit
        item.IsRenaming = true;
        container.UpdateLayout();
        TextBox? box = FindDescendant<TextBox>(container, b => b.Tag as string == "RenameBox");
        if (box == null)
        {
            item.IsRenaming = false;
            ViewModel.IsEditing = false;
            return null;
        }

        _renameBox = box;
        // asynchronous continuations: the code after the await unloads the box (x:Load), which must not
        // happen synchronously inside the box's own KeyDown/LostFocus handler
        _renameTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _renameHadFocus = false;
        // the row was recycled or the list rebuilt (theme, sort) under the edit: end it instead of waiting forever
        void BoxUnloaded(object sender, RoutedEventArgs e)
        {
            box.Unloaded -= BoxUnloaded;
            if (_renameBox == box && !box.IsLoaded)
                _renameTcs?.TrySetResult(null);
        }
        box.Unloaded += BoxUnloaded;
        box.FontSize = UiState.FontSize;
        if (UiState.PanelFont is { } renameFont)
            box.FontFamily = renameFont;
        else
            box.ClearValue(Control.FontFamilyProperty);
        box.Text = item.Entry.Name;
        int dot = item.Entry.IsDirectory || selectWholeName ? -1 : item.Entry.Name.LastIndexOf('.');
        // focus after menus/flyouts have closed and returned their focus
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_renameBox != box)
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
            result = await _renameTcs.Task;
        }
        finally
        {
            _renameBox = null;
            _renameTcs = null;
            item.IsRenaming = false;
        }
        FocusList();
        // refreshes held back during the edit run now (the rename that follows reloads the panel anyway)
        ViewModel.IsEditing = false;
        return result;
    }

    private void RenameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender != _renameBox)
            return;
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            _renameTcs?.TrySetResult(((TextBox)sender).Text);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            _renameTcs?.TrySetResult(null);
        }
        else if (e.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.PageUp or VirtualKey.PageDown)
        {
            e.Handled = true; // keep the caret (and the edit) on this row
        }
    }

    private void RenameBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender == _renameBox)
            _renameHadFocus = true;
    }

    // Clicking elsewhere confirms the rename, like Explorer (only after the box really had the focus).
    private void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender != _renameBox || !_renameHadFocus)
            return;
        var box = (TextBox)sender;
        // Focus that moved into a popup is not "clicking elsewhere": the box's own context menu, or a
        // question from a background transfer. The edit goes on and gets the focus back when it closes.
        if (PopupWithFocus() is { } popup)
        {
            void PopupClosed(object? s, object args)
            {
                popup.Closed -= PopupClosed;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (_renameBox == box && FocusManager.GetFocusedElement(XamlRoot) != box)
                        box.Focus(FocusState.Programmatic);
                });
            }
            popup.Closed += PopupClosed;
            return;
        }
        _renameTcs?.TrySetResult(box.Text);
    }

    /// <summary>The open popup (flyout, dialog) that contains the focused element, if any.</summary>
    private Microsoft.UI.Xaml.Controls.Primitives.Popup? PopupWithFocus()
    {
        if (XamlRoot == null || FocusManager.GetFocusedElement(XamlRoot) is not DependencyObject focused)
            return null;
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot))
            if (popup.Child != null && IsInside(focused, popup.Child))
                return popup;
        return null;
    }
}
