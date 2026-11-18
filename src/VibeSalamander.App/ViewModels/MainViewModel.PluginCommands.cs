// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Services;

namespace VibeSalamander.ViewModels;

// MainViewModel: Plugins → Last command (Ctrl+I), as in Salamander.
public sealed partial class MainViewModel
{
    private (string Name, Func<Task> Run)? _lastPluginCommand;

    /// <summary>The plugin command just chosen from the Plugins menu, for Last command.</summary>
    public void RememberPluginCommand(string name, Func<Task> run) => _lastPluginCommand = (name, run);

    /// <summary>The name of the command Ctrl+I runs again (null: none yet).</summary>
    public string? LastPluginCommandName => _lastPluginCommand?.Name;

    private async Task RunLastPluginCommandAsync()
    {
        if (_lastPluginCommand is not { } last)
        {
            _host.Notify(Loc.T("No plugin command has been run yet: Last command repeats the one chosen last in the Plugins menu."));
            return;
        }
        await last.Run();
    }
}
