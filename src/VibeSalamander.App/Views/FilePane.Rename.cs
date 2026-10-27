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

// FilePane: in-place rename (F2). InPlaceNameEditor does the work; the rename box in the row templates sends its events here.
public sealed partial class FilePane
{
    private InPlaceNameEditor? _nameEditor;

    private InPlaceNameEditor NameEditor => _nameEditor ??= new InPlaceNameEditor(FileList, FocusList);

    private bool IsRenaming => _nameEditor?.IsEditing == true;

    /// <summary>Turns the item's name label into an edit box; returns the new name or null when cancelled.</summary>
    public Task<string?> EditNameInPlaceAsync(FileItemViewModel item, bool selectWholeName = false) =>
        ViewModel == null ? Task.FromResult<string?>(null) : NameEditor.EditAsync(ViewModel, item, selectWholeName);

    private void RenameBox_KeyDown(object sender, KeyRoutedEventArgs e) => NameEditor.BoxKeyDown(sender, e);

    private void RenameBox_GotFocus(object sender, RoutedEventArgs e) => NameEditor.BoxGotFocus(sender);

    private void RenameBox_LostFocus(object sender, RoutedEventArgs e) => NameEditor.BoxLostFocus(sender);

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject =>
        VisualTree.FindDescendant(root, match);
}
