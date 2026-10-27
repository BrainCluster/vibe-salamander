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

    private void RunFoundFile(FileEntry entry, IEnumerable<ProgramRule> rules, Action fallback)
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
    private bool RunProgramRule(IEnumerable<ProgramRule> rules, FileItemViewModel item, UserMenuContext? fileContext = null)
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

    /// <summary>
    /// Shift+F4: creates "New Text Document.txt" in the panel and lets you name it in place (like Explorer),
    /// then opens it in the editor. Esc keeps the suggested name.
    /// </summary>
    private async Task EditNewFileAsync()
    {
        PaneViewModel pane = _active;
        if (!pane.IsLocalDisk)
            return;
        string path = UniquePath(pane.Path, "New Text Document", ".txt");
        await File.WriteAllTextAsync(path, string.Empty);
        string? final = await CreatedItemRenameAsync(pane, path, allowSubdirectories: false);
        if (final != null && File.Exists(final))
            ShellOperations.Edit(final);
    }

    /// <summary>"name.ext", or "name (2).ext", "name (3).ext"... - the first one that doesn't exist yet.</summary>
    private static string UniquePath(string directory, string name, string extension)
    {
        string path = Path.Combine(directory, name + extension);
        for (int i = 2; File.Exists(path) || Directory.Exists(path); i++)
            path = Path.Combine(directory, $"{name} ({i}){extension}");
        return path;
    }

    /// <summary>Focuses a just created item and starts the in-place rename on it. Returns its final path.</summary>
    private async Task<string?> CreatedItemRenameAsync(PaneViewModel pane, string path, bool allowSubdirectories)
    {
        string name = Path.GetFileName(path);
        await pane.NavigateAsync(pane.Path, name, addToHistory: false);
        if (pane.FocusedItem is not { } focused || !string.Equals(focused.Entry.Name, name, StringComparison.OrdinalIgnoreCase))
            await pane.NavigateAsync(pane.Path, name, addToHistory: false); // an auto refresh (after creating it) replaced that load
        // like Explorer, a name that can't be used (exists already, invalid) puts you back into the edit
        for (int attempt = 0; attempt < 5; attempt++)
        {
            FileItemViewModel? item = pane.FocusedItem;
            if (item == null || !string.Equals(item.Entry.Name, name, StringComparison.OrdinalIgnoreCase))
                return path; // created, but not visible (e.g. filtered out)
            string? final = await RenameItemAsync(pane, item, allowSubdirectories,
                selectWholeName: !item.Entry.IsDirectory && Settings.General.EditNewSelectsExtension);
            if (final != null)
                return final;
            await pane.NavigateAsync(pane.Path, name, addToHistory: false);
        }
        return path;
    }

    private async Task CopyOrMoveAsync(bool move)
    {
        var items = _active.GetOperationItems();
        if (items.Count == 0 || !_active.IsLocalDisk)
            return;

        string what = items.Count == 1 ? $"\"{items[0].Entry.Name}\"" : Loc.F("{0} items", items.Count);
        // the other panel, also when it shows a ZIP or TAR (the files go into the archive)
        bool otherUsable = Inactive.IsLocalDisk || Inactive.IsRemote || EditableArchive(Inactive) != null;
        string defaultTarget = otherUsable ? Inactive.Path : string.Empty;
        if (defaultTarget.Length > 0 && !defaultTarget.EndsWith('\\'))
            defaultTarget += "\\";

        // "Don't show this dialog again": straight into the other panel, unless that isn't a usable
        // target (This PC, or the same directory) - then the dialog is shown anyway
        bool sameDirectory = string.Equals(Inactive.Path.TrimEnd('\\'), _active.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        string target;
        if (!Settings.AskCopyMoveTarget && otherUsable && !sameDirectory)
        {
            target = defaultTarget;
        }
        else
        {
            var answer = await _host.Dialogs.CopyMoveTargetAsync(move, what, defaultTarget);
            _host.FocusPane(_active);
            if (answer is not { } a)
                return;
            target = a.Target;
            if (a.DontAskAgain)
            {
                Settings.AskCopyMoveTarget = false;
                SaveSettings();
            }
        }
        bool endsWithSeparator = target.TrimEnd().EndsWith('\\');
        string resolved = PathUtil.Resolve(_active.Path, target);
        if (RemoteProvider.FileSystemOf(resolved) is { } remote)
        {
            UploadToRemote(items.Select(i => i.Entry.FullPath).ToList(), remote, resolved, move);
            return;
        }
        if (!Directory.Exists(resolved) && ArchiveProvider.Locate(resolved) is { } archive)
        {
            if (EditableArchiveAt(resolved) is { } editable)
                AddToArchive(items.Select(i => i.Entry.FullPath).ToList(), editable, move);
            else
                _host.Notify($"\"{Path.GetFileName(archive.ArchivePath)}\" is open read-only: copy files out with F5, or unpack it with Alt+F6 from its folder.", isError: false);
            return;
        }
        if (endsWithSeparator && !resolved.EndsWith('\\'))
            resolved += "\\"; // "target\" always means "into this directory"
        // Confirmations → "Do you want to create target path?"
        bool intoNewDirectory = (resolved.EndsWith('\\') || items.Count > 1) && !Directory.Exists(resolved);
        if (intoNewDirectory && Settings.Confirm.CreateTargetPath &&
            !await _host.ConfirmAsync(move ? "Move" : "Copy", $"The target directory \"{resolved.TrimEnd('\\')}\" doesn't exist. Do you want to create it?", "Create"))
            return;
        await TransferAsync(items.Select(i => i.Entry.FullPath).ToList(), resolved, move);
    }

    /// <summary>
    /// Copies or moves in the background (F5/F6, paste, drag &amp; drop): the operation is added to the
    /// transfer panel and this returns immediately, so the panels stay usable while it runs.
    /// </summary>
    public Task TransferAsync(IReadOnlyList<string> sources, string target, bool move)
    {
        if (sources.Count == 0)
            return Task.CompletedTask;
        if (RemoteProvider.FileSystemOf(target) is { } remote)
        {
            UploadToRemote(sources, remote, target, move); // dropped or pasted onto an FTP / SFTP panel
            return Task.CompletedTask;
        }
        if (!Directory.Exists(target) && EditableArchiveAt(target) is { } archive)
        {
            AddToArchive(sources, archive, move); // dropped or pasted onto a ZIP / TAR panel
            return Task.CompletedTask;
        }
        // like Salamander, the marks are cleared once the operation is started; Ctrl+W brings them back
        _active.RememberSelection();
        _active.MarkAll(false);
        Transfers.Enqueue(move ? OperationKind.Move : OperationKind.Copy, sources, target,
            (engine, ct) => move ? engine.MoveAsync(sources, target, ct) : engine.CopyAsync(sources, target, ct));
        return Task.CompletedTask;
    }
}
