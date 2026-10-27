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

// FilePane: drag and drop.
public sealed partial class FilePane
{
    // ---- drag & drop -----------------------------------------------------------------------

    private const string InternalPathsKey = "VibeSalamanderPaths";

    private void List_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (_marquee.IsBusy)
        {
            e.Cancel = true; // dragging from the empty part of a row draws a selection rectangle instead
            return;
        }
        if (ViewModel == null || !ViewModel.IsLocalDisk || IsRenaming)
        {
            e.Cancel = true;
            return;
        }
        // the drag starts as soon as the mouse moves, also when it was already moving at the click;
        // very short drags (Configuration → Panels) are cancelled at the drop instead (TooShortDrag)
        _dragStartedAt = DateTime.UtcNow;
        _slowClickTimer?.Stop();
        // dragging a marked item drags the whole selection (Salamander)
        List<string> paths = DropRules.DraggedPaths(e.Items.OfType<FileItemViewModel>().ToList(), ViewModel.MarkedItems);
        if (paths.Count == 0)
        {
            e.Cancel = true;
            return;
        }
        e.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy |
                                    Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        e.Data.Properties[InternalPathsKey] = paths.ToArray();
        e.Data.SetDataProvider(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                request.SetData(await FileClipboard.ToStorageItemsAsync(paths));
            }
            catch (Exception)
            {
                // the drop target gets no data; nothing else to do
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    /// <summary>When the last drag from a panel started (shared: drags go from one panel to the other).</summary>
    private static DateTime _dragStartedAt;

    /// <summary>
    /// A drag from a panel released sooner than Configuration → Panels → "Cancel drag-and-drop operations
    /// shorter than" after it started: accidental, nothing is dropped.
    /// </summary>
    private bool TooShortDrag(DragEventArgs e) =>
        DropRules.IsTooShort(_main?.Settings.Panels.DragCancelMilliseconds ?? 0, e.DataView.Properties.ContainsKey(InternalPathsKey),
            DateTime.UtcNow - _dragStartedAt);

    /// <summary>Drop target: the directory under the pointer, or the panel's directory.</summary>
    private string? DropTarget(DragEventArgs e)
    {
        // a ZIP or TAR shown in the panel takes files too (they are copied into the archive)
        if (ViewModel == null || _main == null || !(ViewModel.IsLocalDisk || ViewModel.IsRemote || _main.IsEditableArchive(ViewModel)))
            return null;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileItemViewModel { Entry.IsDirectory: true, Entry.IsUpDir: false } dir)
            return dir.Entry.FullPath;
        return ViewModel.Path;
    }

    /// <summary>
    /// Explorer rules: Shift = move, Ctrl = copy, otherwise move within a drive and copy across drives.
    /// Into an archive files are copied unless Shift is held.
    /// </summary>
    private bool DecideMove(DragEventArgs e, string target) =>
        DropRules.IsMove(
            shift: KeyboardState.IsDown(KeyboardState.Shift),
            ctrl: KeyboardState.IsDown(KeyboardState.Control),
            intoArchiveOrServer: ViewModel?.IsArchive == true || ViewModel?.IsRemote == true,
            firstSource: e.DataView.Properties.TryGetValue(InternalPathsKey, out object? value) && value is string[] { Length: > 0 } internalPaths
                ? internalPaths[0] : null,
            target);

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        string? target = DropTarget(e);
        if (target == null || !e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems) || TooShortDrag(e))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
            return;
        }
        bool move = DecideMove(e, target);
        e.AcceptedOperation = move ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move : Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        e.DragUIOverride.Caption = (move ? "Move to " : "Copy to ") + target;
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel == null || _main == null)
            return;
        string? target = DropTarget(e);
        if (target == null || TooShortDrag(e))
            return;
        var deferral = e.GetDeferral();
        try
        {
            IReadOnlyList<string> paths;
            if (e.DataView.Properties.TryGetValue(InternalPathsKey, out object? value) && value is string[] internalPaths)
                paths = internalPaths;
            else
                paths = (await e.DataView.GetStorageItemsAsync()).Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            bool move = DecideMove(e, target);
            // dropping into the directory the items already are in does nothing
            if (DropRules.ChangesNothing(paths, target))
                return;
            _main.Activate(ViewModel);
            if (!await _main.ConfirmDropAsync(paths, target, move))
                return;
            await _main.TransferAsync(paths, target, move);
        }
        catch (Exception ex)
        {
            await _main.ShowErrorAsync(ex.Message);
        }
        finally
        {
            deferral.Complete();
        }
    }
}
