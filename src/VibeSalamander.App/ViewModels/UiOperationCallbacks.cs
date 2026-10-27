// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using VibeSalamander.Core;
using VibeSalamander.Core.Commands;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Formatting;
using VibeSalamander.Core.Interop;
using VibeSalamander.Core.Operations;
using VibeSalamander.Core.Panels;
using VibeSalamander.Core.Settings;
using VibeSalamander.Core.Tools;
using VibeSalamander.Plugins;
using VibeSalamander.Services;

namespace VibeSalamander.ViewModels;

/// <summary>Engine prompts marshalled to the UI thread.</summary>
internal sealed class UiOperationCallbacks : IOperationCallbacks
{
    private readonly DispatcherQueue _dispatcher;
    private readonly Views.Dialogs _dialogs;

    public UiOperationCallbacks(DispatcherQueue dispatcher, Views.Dialogs dialogs)
    {
        _dispatcher = dispatcher;
        _dialogs = dialogs;
    }

    public Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict) => OnUi(() => _dialogs.ConflictAsync(conflict));

    public Task<ErrorChoice> ResolveErrorAsync(OperationError error) => OnUi(() => _dialogs.ErrorAsync(error));

    private Task<T> OnUi<T>(Func<Task<T>> action)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = _dispatcher.TryEnqueue(async () =>
        {
            try
            {
                tcs.SetResult(await action());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        if (!queued)
            tcs.SetException(new InvalidOperationException("The window is closing."));
        return tcs.Task;
    }
}
