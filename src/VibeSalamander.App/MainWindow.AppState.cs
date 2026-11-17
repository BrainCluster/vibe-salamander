// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using VibeSalamander.Services;
using VibeSalamander.ViewModels;

namespace VibeSalamander;

// MainWindow: whether the program is ready or busy, with open dialogs and running transfers, for the UI tests
// (the element "AppState", with nothing to see).
public sealed partial class MainWindow
{
    /// <summary>The window's queue, taken on the UI thread: the Window's own property can't be read from another one.</summary>
    private Microsoft.UI.Dispatching.DispatcherQueue? _appStateQueue;

    private void WatchAppState()
    {
        _appStateQueue = DispatcherQueue;
        AppActivity.Changed += UpdateAppState;
        _vm.Left.PropertyChanged += AppState_PaneChanged;
        _vm.Right.PropertyChanged += AppState_PaneChanged;
        _vm.Transfers.StateChanged += (_, _) => UpdateAppState();
        Closed += (_, _) => AppActivity.Changed -= UpdateAppState;
        UpdateAppState();
    }

    private void AppState_PaneChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PaneViewModel.IsBusy))
            UpdateAppState();
    }

    private void UpdateAppState()
    {
        // a plugin or a worker can report from another thread
        if (_appStateQueue is not { } queue)
            return;
        if (!queue.HasThreadAccess)
        {
            queue.TryEnqueue(UpdateAppState);
            return;
        }
        string state = _vm.Left.IsBusy || _vm.Right.IsBusy ? "busy: panel" : AppActivity.State;
        if (AppActivity.OpenDialogs > 0)
            state += "; dialog";
        if (_vm.Transfers.ActiveCount > 0)
            state += "; transfers";
        if (AppState.Text != state)
            AppState.Text = state;
    }
}
