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

// MainViewModel: tools: compare directories, user menu, selections, file lists, NTFS, sharing, special folders.
public sealed partial class MainViewModel
{
    // ---- phase 3B: commands --------------------------------------------------------------

    private const int MaxRecent = 30;

    private static void RememberRecent(List<string> list, string path, int max = MaxRecent)
    {
        if (string.IsNullOrEmpty(path) || path.StartsWith(ThisPcProvider.RootPath, StringComparison.OrdinalIgnoreCase))
            return;
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        if (list.Count > max)
            list.RemoveRange(max, list.Count - max);
    }

    /// <summary>Configuration → History: how many working directories are kept (Alt+F12).</summary>
    public int WorkingDirectoriesLimit => Math.Clamp(Settings.History.WorkingDirectoriesLimit, 1, 100);

    /// <summary>Applies a lowered limit to the list at once.</summary>
    public void TrimWorkingDirectories()
    {
        int max = WorkingDirectoriesLimit;
        if (Settings.WorkingDirectories.Count > max)
            Settings.WorkingDirectories.RemoveRange(max, Settings.WorkingDirectories.Count - max);
    }

    private static DirectoryComparer.Item ToCompareItem(FileItemViewModel i) =>
        new(i.Entry.Name, i.Entry.IsDirectory, i.Entry.Size, i.Entry.LastWrite);

    /// <summary>Ctrl+F10: marks, in both panels, what is missing on the other side or differs.</summary>
    private async Task CompareDirectoriesAsync()
    {
        if (!Left.IsLocalDisk || !Right.IsLocalDisk)
        {
            _host.Notify("Compare Directories needs a directory in both panels.", isError: false);
            return;
        }
        if (string.Equals(Left.Path.TrimEnd('\\'), Right.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            _host.Notify("Both panels show the same directory.", isError: false);
            return;
        }
        CompareOptions? options = await _host.Dialogs.CompareOptionsAsync(Settings.Compare);
        if (options == null)
            return;
        options.ToleranceSeconds = Settings.General.CompareTimeTolerance;
        options.IgnoreDstHours = Settings.General.CompareIgnoreDstHours;
        Settings.Compare = options;

        var left = Left.Items.Where(i => !i.Entry.IsUpDir).Select(ToCompareItem).ToList();
        var right = Right.Items.Where(i => !i.Entry.IsUpDir).Select(ToCompareItem).ToList();
        CompareResult result = await DirectoryComparer.CompareAsync(Left.Path, left, Right.Path, right, options);
        int inLeft = Left.MarkNames(result.Left);
        int inRight = Right.MarkNames(result.Right);
        _host.Notify(result.Identical
            ? "The directories are identical."
            : $"Differences marked: {inLeft} in the left panel, {inRight} in the right panel.", isError: false);
    }

    private UserMenuContext UserMenuContextFor(PaneViewModel pane)
    {
        PaneViewModel other = pane == Left ? Right : Left;
        FileItemViewModel? focused = pane.FocusedItem;
        return new UserMenuContext
        {
            Path = pane.IsLocalDisk ? pane.Path : string.Empty,
            OtherPath = other.IsLocalDisk ? other.Path : string.Empty,
            FullName = focused != null && !focused.Entry.IsUpDir && !focused.IsDrive ? focused.Entry.FullPath : string.Empty,
            Selected = pane.GetOperationItems().Select(i => i.Entry.FullPath).ToList(),
        };
    }

    /// <summary>F9: the user menu at the caret.</summary>
    private void ShowUserMenu(PaneViewModel pane)
    {
        var entries = new List<MenuEntry>();
        foreach (UserMenuItem item in Settings.UserMenu)
        {
            if (item.IsSeparator)
            {
                entries.Add(MenuEntry.Separator);
                continue;
            }
            UserMenuItem command = item;
            entries.Add(new MenuEntry(item.Name, () => RunUserMenuItem(pane, command), "\uE756"));
        }
        if (entries.Count > 0)
            entries.Add(MenuEntry.Separator);
        entries.Add(new MenuEntry(Loc.T("Edit User Menu..."), async () => await EditUserMenuAsync(), "\uE70F"));
        _host.ShowMenu(pane, entries, atCaret: true);
    }

    /// <summary>Runs a user menu command for the active panel (user menu bar).</summary>
    public void RunUserMenuItem(UserMenuItem item) => RunUserMenuItem(_active, item);

    /// <summary>Raised when hot paths or the user menu changed (the window rebuilds its bars).</summary>
    public event EventHandler? QuickBarsChanged;

    private void RunUserMenuItem(PaneViewModel pane, UserMenuItem item)
    {
        UserMenuContext context = UserMenuContextFor(pane);
        string command = UserMenu.Expand(item.Command, context).Trim();
        string arguments = UserMenu.Expand(item.Arguments, context);
        string directory = UserMenu.Expand(string.IsNullOrWhiteSpace(item.InitialDirectory) ? "$(Path)" : item.InitialDirectory, context);
        if (command.Length == 0)
            return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(command, arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = Directory.Exists(directory) ? directory : string.Empty,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            _host.Notify($"{item.Name}: {ex.Message}");
        }
    }

    private async Task EditUserMenuAsync()
    {
        List<UserMenuItem>? edited = await _host.Dialogs.EditUserMenuAsync(Settings.UserMenu);
        if (edited == null)
            return;
        Settings.UserMenu = edited;
        SaveSettings();
        QuickBarsChanged?.Invoke(this, EventArgs.Empty);
    }

    private List<string> _savedSelection = new();

    /// <summary>Ctrl+Shift+F5: remembers the marked names (for any directory).</summary>
    private void SaveSelection(PaneViewModel pane)
    {
        var names = pane.MarkedItems.Select(i => i.Entry.Name).ToList();
        if (names.Count == 0)
        {
            _host.Notify("Nothing is selected.", isError: false);
            return;
        }
        _savedSelection = names;
        _host.Notify(Loc.F("Selection saved: {0} names (Ctrl+Shift+F6 loads it).", names.Count), isError: false);
    }

    /// <summary>Ctrl+Shift+F6: marks the saved names in the active panel.</summary>
    private void LoadSelection(PaneViewModel pane)
    {
        if (_savedSelection.Count == 0)
        {
            _host.Notify("No selection is saved. Ctrl+Shift+F5 saves the selected names.", isError: false);
            return;
        }
        int found = pane.MarkNames(_savedSelection);
        _host.Notify(Loc.F("Selected {0} of {1} saved names.", found, _savedSelection.Count), isError: false);
    }

    private void HideNames(PaneViewModel pane, bool selected)
    {
        int count = pane.HideNames(selected);
        if (count > 0)
            _host.Notify(Loc.F("Names hidden: {0}. Edit → Show Hidden Names brings them back.", count), isError: false);
    }

    private void ShowHiddenNames(PaneViewModel pane)
    {
        if (pane.ShowHiddenNames() == 0)
            _host.Notify("No names are hidden in this panel.", isError: false);
    }

    /// <summary>Ctrl+M: copies a list of the selected items (or of all items) with their details to the clipboard.</summary>
    private void MakeFileList(PaneViewModel pane)
    {
        IReadOnlyList<FileItemViewModel> items = pane.MarkedItems.Count > 0
            ? pane.MarkedItems
            : pane.Items.Where(i => !i.Entry.IsUpDir).ToList();
        if (items.Count == 0)
            return;
        var sb = new System.Text.StringBuilder();
        foreach (FileItemViewModel item in items)
        {
            FileEntry e = item.Entry;
            string size = e.IsDirectory ? (item.DirectorySize is long d ? d.ToString(System.Globalization.CultureInfo.InvariantCulture) : "<DIR>")
                                        : e.Size.ToString(System.Globalization.CultureInfo.InvariantCulture);
            sb.Append(e.Name).Append('\t').Append(size).Append('\t')
              .Append(Formats.Date(e.LastWrite)).Append('\t').Append(Formats.Time(e.LastWrite)).Append('\t')
              .Append(Formats.Attributes(e.Attributes)).AppendLine();
        }
        CopyText(sb.ToString());
        _host.Notify(Loc.F("File list copied to the clipboard: {0} items (tab separated: name, size, date, time, attributes).", items.Count), isError: false);
    }

    /// <summary>Ctrl+S: shortcuts (.lnk) to the files on the clipboard, created in the active panel.</summary>
    private async Task PasteShortcutsAsync(PaneViewModel pane)
    {
        if (!pane.IsLocalDisk)
            return;
        var (paths, _) = await FileClipboard.GetFilesAsync();
        if (paths.Count == 0)
        {
            _host.Notify("There are no files on the clipboard.", isError: false);
            return;
        }
        string? last = null;
        int failed = 0;
        foreach (string target in paths)
        {
            string name = Path.GetFileName(target.TrimEnd('\\'));
            if (string.IsNullOrEmpty(name))
                name = target.TrimEnd('\\').Replace(":", string.Empty); // a drive
            string link = UniquePath(pane.Path, name + " - Shortcut", ".lnk");
            try
            {
                ShellLink.Create(target, link);
                last = link;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }
        if (last != null)
            await pane.NavigateAsync(pane.Path, Path.GetFileName(last), addToHistory: false);
        if (failed > 0)
            _host.Notify(Loc.F("Shortcuts that could not be created: {0}.", failed));
    }

    /// <summary>Files → New: the Explorer "New" menu; the created item is then renamed in place.</summary>
    private async Task NewFromShellMenuAsync(PaneViewModel pane)
    {
        if (!pane.IsLocalDisk)
            return;
        var before = new HashSet<string>(SafeEntries(pane.Path), StringComparer.OrdinalIgnoreCase);
        if (!_host.ShowNewMenu(pane))
            return;
        // the shell creates the item asynchronously: wait for it to appear
        for (int i = 0; i < 30; i++)
        {
            string? created = SafeEntries(pane.Path).FirstOrDefault(e => !before.Contains(e));
            if (created != null)
            {
                if (created.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    await pane.NavigateAsync(pane.Path, Path.GetFileName(created), addToHistory: false); // the Create Shortcut wizard names it
                else
                    await CreatedItemRenameAsync(pane, created, allowSubdirectories: Directory.Exists(created));
                return;
            }
            await Task.Delay(100);
        }
        await pane.RefreshAsync();
    }

    private static IEnumerable<string> SafeEntries(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private async Task NtfsAsync(PaneViewModel pane, NtfsAction action)
    {
        var items = pane.GetOperationItems();
        if (items.Count == 0 || !pane.IsLocalDisk)
            return;
        bool compress = action is NtfsAction.Compress or NtfsAction.Uncompress;
        if (compress ? Settings.Confirm.NtfsCompress : Settings.Confirm.NtfsEncrypt)
        {
            string what = items.Count == 1 ? $"\"{items[0].Entry.Name}\"" : Loc.F("{0} selected items", items.Count);
            bool hasDirectories = items.Any(i => i.Entry.IsDirectory);
            string question = (action, hasDirectories) switch
            {
                (NtfsAction.Compress, false) => "Do you want to compress {0}?",
                (NtfsAction.Compress, true) => "Do you want to compress {0} including the content of directories?",
                (NtfsAction.Uncompress, false) => "Do you want to uncompress {0}?",
                (NtfsAction.Uncompress, true) => "Do you want to uncompress {0} including the content of directories?",
                (NtfsAction.Encrypt, false) => "Do you want to encrypt {0}?",
                (NtfsAction.Encrypt, true) => "Do you want to encrypt {0} including the content of directories?",
                (_, false) => "Do you want to decrypt {0}?",
                _ => "Do you want to decrypt {0} including the content of directories?",
            };
            string button = action switch
            {
                NtfsAction.Compress => "Compress",
                NtfsAction.Uncompress => "Uncompress",
                NtfsAction.Encrypt => "Encrypt",
                _ => "Decrypt",
            };
            if (!await _host.ConfirmAsync("NTFS", Loc.F(question, what), button))
                return;
        }
        var (ok, failed, firstError) = await NtfsOperations.ApplyAsync(items.Select(i => i.Entry.FullPath).ToList(), action);
        await pane.RefreshAsync();
        string done = action switch
        {
            NtfsAction.Compress => "Items compressed: {0}.",
            NtfsAction.Uncompress => "Items uncompressed: {0}.",
            NtfsAction.Encrypt => "Items encrypted: {0}.",
            _ => "Items decrypted: {0}.",
        };
        if (failed == 0)
            _host.Notify(Loc.F(done, ok), isError: false);
        else
            _host.Notify(Loc.F(done, ok) + " " + Loc.F("Failed: {0}. {1}", failed, firstError));
    }

    private void ShowSecurity(PaneViewModel pane)
    {
        var items = pane.GetOperationItems();
        string? path = items.Count > 0 ? items[0].Entry.FullPath : pane.FocusedItem?.Drive?.Root;
        if (path != null)
            ShellOperations.ShowSecurity(_host.WindowHandle, path);
    }

    /// <summary>Ctrl+E: a new e-mail with the selected files attached.</summary>
    private async Task EmailAsync(PaneViewModel pane)
    {
        var files = pane.GetOperationItems().Where(i => !i.Entry.IsDirectory).Select(i => i.Entry.FullPath).ToList();
        if (files.Count == 0)
        {
            _host.Notify("Select the files to send (directories can't be attached).", isError: false);
            return;
        }
        if (Settings.Confirm.Email)
        {
            long total = pane.GetOperationItems().Where(i => !i.Entry.IsDirectory).Sum(i => i.Entry.Size);
            string what = files.Count == 1 ? $"\"{Path.GetFileName(files[0])}\"" : $"{files.Count} files";
            if (!await _host.ConfirmAsync("Email", $"Do you want to email {what}?\nTotal size: {Formats.ShortSize(total)}", "Email"))
                return;
        }
        MapiMail.Result result = await MapiMail.SendFilesAsync(_host.WindowHandle, files);
        if (result is MapiMail.Result.NoMailProgram or MapiMail.Result.Failed)
            _host.Notify("No e-mail program that accepts attachments (Simple MAPI) is set as the default mail app.");
    }

    /// <summary>Alt+F11: recently opened files.</summary>
    private void ShowOpenedFiles(PaneViewModel pane)
    {
        var entries = new List<MenuEntry> { MenuEntry.Header("Opened files") };
        foreach (string file in Settings.OpenedFiles.Take(MaxRecent))
        {
            string path = file;
            entries.Add(new MenuEntry(path, async () =>
            {
                string? dir = Path.GetDirectoryName(path);
                if (dir != null && Directory.Exists(dir))
                    await pane.NavigateAsync(dir, Path.GetFileName(path));
                if (File.Exists(path))
                {
                    ShellOperations.Open(path, dir);
                    RememberRecent(Settings.OpenedFiles, path);
                }
                else
                {
                    _host.Notify($"{path} no longer exists.");
                }
            }, "\uE8A5"));
        }
        if (entries.Count == 1)
            entries.Add(new MenuEntry("(no files opened yet)", IsEnabled: false));
        _host.ShowMenu(pane, entries, atCaret: false);
    }

    /// <summary>Alt+F12: recently visited directories (both panels).</summary>
    private void ShowWorkingDirectories(PaneViewModel pane)
    {
        var entries = new List<MenuEntry> { MenuEntry.Header("Working directories") };
        foreach (string directory in Settings.WorkingDirectories.Take(WorkingDirectoriesLimit))
        {
            string path = directory;
            entries.Add(new MenuEntry(path, async () => await pane.NavigateAsync(path), "\uE8B7"));
        }
        if (entries.Count == 1)
            entries.Add(new MenuEntry("(none yet)", IsEnabled: false));
        _host.ShowMenu(pane, entries, atCaret: false);
    }

    /// <summary>Ctrl+Shift+F9: the shared directories of this computer.</summary>
    private async Task ShowSharesAsync(PaneViewModel pane)
    {
        IReadOnlyList<NetworkDrives.Share> shares = await NetworkDrives.GetLocalSharesAsync();
        var entries = new List<MenuEntry> { MenuEntry.Header($"Shared on {Environment.MachineName}") };
        foreach (NetworkDrives.Share share in shares)
        {
            string unc = share.UncPath;
            string text = share.Remark.Length > 0 ? $"{share.Name}  —  {share.Remark}" : share.Name;
            entries.Add(new MenuEntry(text, async () => await pane.NavigateAsync(unc), "\uE8CE"));
        }
        if (shares.Count == 0)
            entries.Add(new MenuEntry("(no shared directories)", IsEnabled: false));
        _host.ShowMenu(pane, entries, atCaret: false);
    }

    /// <summary>Commands → Open Folder: special folders (file system ones open in the panel, the others in Explorer).</summary>
    public async Task OpenSpecialFolderAsync(string key)
    {
        string? path = key switch
        {
            "Desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "Documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Downloads" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            "Pictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "Music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "Videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "UserProfile" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Templates" => Environment.GetFolderPath(Environment.SpecialFolder.Templates),
            "SendTo" => Environment.GetFolderPath(Environment.SpecialFolder.SendTo),
            "StartMenu" => Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs" => Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            "Startup" => Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            "Recent" => Environment.GetFolderPath(Environment.SpecialFolder.Recent),
            "Temp" => Path.GetTempPath(),
            "Computer" => ThisPcProvider.RootPath,
            _ => null,
        };
        if (path != null)
        {
            await _active.NavigateAsync(path);
            _host.FocusPane(_active);
            return;
        }
        string? shell = key switch
        {
            "ControlPanel" => "shell:ControlPanelFolder",
            "Network" => "shell:NetworkPlacesFolder",
            "RecycleBin" => "shell:RecycleBinFolder",
            "Fonts" => "shell:Fonts",
            "Printers" => "shell:PrintersFolder",
            _ => null,
        };
        if (shell != null)
            ShellOperations.OpenInExplorer(shell);
    }
}
