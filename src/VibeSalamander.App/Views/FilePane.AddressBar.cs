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

// FilePane: the address bar. AddressBar does the work; the XAML's events for it come here.
public sealed partial class FilePane
{
    private void RebuildBreadcrumb() => _addressBar.Rebuild();

    private void UpdateBreadcrumbForeground() => _addressBar.UpdateForeground();

    private void BreadcrumbHost_SizeChanged(object sender, SizeChangedEventArgs e) => _addressBar.UpdateOverflow();

    /// <summary>Address bar in edit mode with the full location selected (empty-area click, Shift+F7).</summary>
    public void BeginPathEdit() => _addressBar.BeginEdit();

    private void PathEditor_KeyDown(object sender, KeyRoutedEventArgs e) => _addressBar.EditorKeyDown(sender, e);

    private void PathEditor_LostFocus(object sender, RoutedEventArgs e) => _addressBar.EndEdit();
}
