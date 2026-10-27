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

// MainViewModel: the Find window's results (focus, open, the context menu of found items).
public sealed partial class MainViewModel
{
    // ---- found items (the Find window's context menu) -------------------------------------------

    /// <summary>Open on a found file: with its program (archives and directories open in a panel, see FindWindow).</summary>
    public void OpenFoundFile(FileEntry entry) =>
        RunFoundFile(entry, Array.Empty<ProgramRule>(), () => ShellOperations.Open(entry.FullPath, Path.GetDirectoryName(entry.FullPath)));

    /// <summary>F3 on a found file: Configuration → Viewers, else the associated program.</summary>
    public void ViewFoundFile(FileEntry entry) =>
        RunFoundFile(entry, Settings.Viewers, () => ShellOperations.Open(entry.FullPath, Path.GetDirectoryName(entry.FullPath)));

    /// <summary>F4 on a found file: Configuration → Editors, else the "edit" verb.</summary>
    public void EditFoundFile(FileEntry entry) =>
        RunFoundFile(entry, Settings.Editors, () => ShellOperations.Edit(entry.FullPath));

    internal void RunFoundFile(FileEntry entry, IEnumerable<ProgramRule> rules, Action fallback)
    {
        if (entry.IsDirectory)
            return;
        string directory = Path.GetDirectoryName(entry.FullPath) ?? string.Empty;
        // the rule's $(Path) / $(FullName) are the found file's, not the panel's
        var context = new UserMenuContext
        {
            Path = directory,
            OtherPath = _active.IsLocalDisk ? _active.Path : string.Empty,
            FullName = entry.FullPath,
            Selected = new[] { entry.FullPath },
        };
        try
        {
            if (!RunProgramRule(rules, new FileItemViewModel(entry, _active.Columns), context))
                fallback();
            RememberRecent(Settings.OpenedFiles, entry.FullPath);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            _host.Notify(ex.Message);
        }
    }

    /// <summary>Configuration → Viewers / Editors: the first rule whose masks match starts its program.</summary>
    internal bool RunProgramRule(IEnumerable<ProgramRule> rules, FileItemViewModel item, UserMenuContext? fileContext = null)
    {
        foreach (ProgramRule rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Command) || !new WildcardMask(rule.Masks).IsMatch(item.Entry.Name))
                continue;
            UserMenuContext context = fileContext ?? UserMenuContextFor(_active);
            string directory = UserMenu.Expand(string.IsNullOrWhiteSpace(rule.InitialDirectory) ? "$(Path)" : rule.InitialDirectory, context);
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    UserMenu.Expand(rule.Command, context).Trim(), UserMenu.Expand(rule.Arguments, context))
                {
                    UseShellExecute = true,
                    WorkingDirectory = Directory.Exists(directory) ? directory : string.Empty,
                });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
            {
                _host.Notify($"{rule.Command}: {ex.Message}");
            }
            return true;
        }
        return false;
    }

}
