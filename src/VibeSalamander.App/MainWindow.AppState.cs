// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using VibeSalamander.Services;
using VibeSalamander.ViewModels;

namespace VibeSalamander;

// MainWindow: whether the program is ready or busy, for the UI tests (the element "AppState", not displayed).
public sealed partial class MainWindow
{
    private void WatchAppState()
    {
        AppActivity.Changed += UpdateAppState;
        _vm.Left.PropertyChanged += AppState_PaneChanged;
        _vm.Right.PropertyChanged += AppState_PaneChanged;
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
        if (AppState.Text != state)
            AppState.Text = state;
    }
}
