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
    private void WatchAppState()
    {
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
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(UpdateAppState);
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
