// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.Commands;

namespace VibeSalamander.ViewModels;

/// <summary>
/// What each command does: one handler per <see cref="CommandId"/>, given the active panel when the command
/// started. Handlers are registered by area (navigation, selection, files, tools, the window).
/// </summary>
internal sealed class CommandTable
{
    private readonly Dictionary<CommandId, Func<PaneViewModel, Task>> _handlers = new();

    /// <summary>A command that runs asynchronously.</summary>
    public void Add(CommandId command, Func<PaneViewModel, Task> run)
    {
        if (!_handlers.TryAdd(command, run))
            throw new InvalidOperationException($"The command {command} has two handlers.");
    }

    /// <summary>A command that is done when it returns.</summary>
    public void AddSync(CommandId command, Action<PaneViewModel> run) =>
        Add(command, pane =>
        {
            run(pane);
            return Task.CompletedTask;
        });

    public bool TryGet(CommandId command, out Func<PaneViewModel, Task> run) =>
        _handlers.TryGetValue(command, out run!);

    public bool Handles(CommandId command) => _handlers.ContainsKey(command);
}
