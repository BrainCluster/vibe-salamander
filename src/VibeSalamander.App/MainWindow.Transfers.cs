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

// MainWindow: the transfer panel.
public sealed partial class MainWindow
{
    // ---- transfer panel ------------------------------------------------------------------------

    private const double TransfersSplitterHeight = 6;
    private const double MinTransfersHeight = 80;

    /// <summary>Shows the transfer panel while it has items (unless switched off in Options → Show).</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _transfersCloseTimer;

    /// <summary>
    /// Configuration → Main Window: when nothing is running any more, the finished transfers are cleared
    /// after a few seconds, so the panel closes. Transfers that failed or were cancelled stay (their
    /// messages matter), and so does the panel then.
    /// </summary>
    private void ScheduleTransfersAutoClose()
    {
        // an always visible panel keeps its list as a log: nothing is cleared by itself
        int seconds = _vm.Settings.AlwaysShowTransfers ? 0 : _vm.Settings.TransfersAutoCloseSeconds;
        bool idle = !TransfersView.IsHeld && _vm.Transfers.HasItems && _vm.Transfers.ActiveCount == 0 &&
                    _vm.Transfers.Items.Any(i => i.State == TransferState.Completed); // only failures left: the panel stays
        if (seconds <= 0 || !idle)
        {
            _transfersCloseTimer?.Stop();
            TransfersView.StopCloseCountdown();
            return;
        }
        if (_transfersCloseTimer == null)
        {
            _transfersCloseTimer = DispatcherQueue.CreateTimer();
            _transfersCloseTimer.IsRepeating = false;
            _transfersCloseTimer.Tick += (_, _) =>
            {
                TransfersView.StopCloseCountdown();
                if (_vm.Transfers.ActiveCount == 0)
                    _vm.Transfers.ClearSucceeded();
            };
        }
        _transfersCloseTimer.Interval = TimeSpan.FromSeconds(seconds);
        _transfersCloseTimer.Start(); // restarts the countdown when another transfer ends
        TransfersView.StartCloseCountdown(TimeSpan.FromSeconds(seconds));
    }

    private void UpdateTransfersLayout()
    {
        bool show = _vm.Settings.ShowTransfers && (_vm.Settings.AlwaysShowTransfers || _vm.Transfers.HasItems);
        TransfersView.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        TransfersSplitter.Visibility = show && !TransfersView.IsCollapsed ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            TransfersRow.Height = new GridLength(0);
            TransfersSplitterRow.Height = new GridLength(0);
            return;
        }
        double margin = TransfersView.Margin.Top + TransfersView.Margin.Bottom;
        if (TransfersView.IsCollapsed)
        {
            TransfersRow.Height = new GridLength(TransfersPanel.HeaderHeight + margin);
            TransfersSplitterRow.Height = new GridLength(0);
        }
        else
        {
            TransfersRow.Height = new GridLength(ClampTransfersHeight(_vm.Settings.TransfersHeight) + margin);
            TransfersSplitterRow.Height = new GridLength(TransfersSplitterHeight);
        }
    }

    private double ClampTransfersHeight(double height)
    {
        double max = RootGrid.ActualHeight > 0 ? Math.Max(MinTransfersHeight, RootGrid.ActualHeight * 0.6) : 600;
        return Math.Clamp(height, MinTransfersHeight, max);
    }

    private bool _resizingTransfers;
    private double _resizeStartY;
    private double _resizeStartHeight;

    private void TransfersSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _resizingTransfers = TransfersSplitter.CapturePointer(e.Pointer);
        _resizeStartY = e.GetCurrentPoint(RootGrid).Position.Y;
        _resizeStartHeight = ClampTransfersHeight(_vm.Settings.TransfersHeight);
        e.Handled = true;
    }

    private void TransfersSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizingTransfers)
            return;
        double y = e.GetCurrentPoint(RootGrid).Position.Y;
        _vm.Settings.TransfersHeight = ClampTransfersHeight(_resizeStartHeight - (y - _resizeStartY));
        UpdateTransfersLayout();
    }

    private void TransfersSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _resizingTransfers = false;
        TransfersSplitter.ReleasePointerCapture(e.Pointer);
    }

    private void TransfersSplitter_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _vm.Settings.TransfersHeight = 150;
        UpdateTransfersLayout();
    }

    private bool _closeConfirmed;
    private bool _closePrompting;

    /// <summary>Closing while transfers run asks first, then cancels them.</summary>
    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed || (_vm.Transfers.ActiveCount == 0 && !_vm.Settings.Confirm.CloseApplication))
            return;
        args.Cancel = true;
        await CloseAfterConfirmAsync();
    }

    /// <summary>
    /// Window.Close() does not raise AppWindow.Closing, so File → Exit and the F-key bar come here too:
    /// asks while transfers are active (once, even when the close button is clicked again), then closes.
    /// </summary>
    private async Task CloseAfterConfirmAsync()
    {
        if (_closePrompting)
            return;
        int count = _vm.Transfers.ActiveCount;
        if (count == 0 && _vm.Settings.Confirm.CloseApplication)
        {
            // Configuration → Confirmations → "Do you want to close Vibe Salamander?"
            _closePrompting = true;
            try
            {
                if (!await ConfirmAsync("Exit Vibe Salamander", "Do you want to close Vibe Salamander?", "Close"))
                    return;
            }
            finally
            {
                _closePrompting = false;
            }
        }
        if (count > 0 && !_vm.Settings.Confirm.ExitWithRunningTransfers)
        {
            _vm.Transfers.CancelAll();
        }
        else if (count > 0)
        {
            bool close;
            _closePrompting = true;
            try
            {
                close = await ConfirmAsync("Exit Vibe Salamander",
                    Loc.F(count == 1 ? "{0} transfer is still running or queued.\nCancel it and exit?" : "{0} transfers are still running or queued.\nCancel them and exit?", count),
                    "Cancel and Exit");
            }
            finally
            {
                _closePrompting = false;
            }
            if (!close)
                return;
            _vm.Transfers.CancelAll();
        }
        _closeConfirmed = true;
        Close();
    }
}
