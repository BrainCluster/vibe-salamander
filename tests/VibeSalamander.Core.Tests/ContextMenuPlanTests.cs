// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.Commands;
using VibeSalamander.Core.Interop;

namespace VibeSalamander.Core.Tests;

/// <summary>What the right-click menu holds in each situation (Core.Commands.ContextMenuPlan).</summary>
public class ContextMenuPlanTests
{
    private static IReadOnlyList<ContextMenuEntry> Plan(ContextMenuFacts facts) => ContextMenuPlan.For(facts);

    private static CommandId[] Commands(IReadOnlyList<ContextMenuEntry> menu) =>
        menu.Where(e => e.Kind == ContextMenuEntryKind.Command).Select(e => e.Command).ToArray();

    private static string[] Texts(IReadOnlyList<ContextMenuEntry> menu) =>
        menu.Where(e => e.Kind == ContextMenuEntryKind.Command).Select(e => e.Text).ToArray();

    private static readonly ContextMenuFacts AFile = new() { Target = ContextMenuTarget.Item, OffersWindowsMenu = true };

    [Fact]
    public void Separators_never_double_up_or_start_the_menu()
    {
        var situations = new List<ContextMenuFacts>();
        foreach (ContextMenuTarget target in Enum.GetValues<ContextMenuTarget>())
            foreach (bool flag in new[] { false, true })
                situations.Add(new ContextMenuFacts
                {
                    Target = target,
                    IsDirectory = flag,
                    MarkedCount = flag ? 3 : 0,
                    IsArchiveFile = flag,
                    IsDiskImage = !flag,
                    EditableArchive = flag,
                    IsRemotePanel = flag,
                    MountedImageDrive = flag ? "e:" : null,
                    OffersWindowsMenu = flag,
                });
        foreach (ContextMenuFacts facts in situations)
        {
            IReadOnlyList<ContextMenuEntry> menu = Plan(facts);
            Assert.NotEmpty(menu);
            Assert.NotEqual(ContextMenuEntryKind.Separator, menu[0].Kind);
            Assert.NotEqual(ContextMenuEntryKind.Separator, menu[^1].Kind);
            for (int i = 1; i < menu.Count; i++)
                Assert.False(menu[i].Kind == ContextMenuEntryKind.Separator && menu[i - 1].Kind == ContextMenuEntryKind.Separator, $"{facts.Target}: two separators");
            Assert.True(menu.Count(e => e.Bold) <= 1, $"{facts.Target}: one default action at most");
        }
    }

    [Fact]
    public void A_file_has_the_everyday_commands_and_the_windows_menu_last()
    {
        IReadOnlyList<ContextMenuEntry> menu = Plan(AFile);
        Assert.Equal(CommandId.Open, menu[0].Command);
        Assert.True(menu[0].Bold);
        Assert.Equal(new[]
        {
            CommandId.Open, CommandId.OpenWith, CommandId.Edit,
            CommandId.ClipboardCut, CommandId.ClipboardCopy, CommandId.CopyAsShortcut, CommandId.ClipboardPaste,
            CommandId.Copy, CommandId.Move, CommandId.QuickRename, CommandId.Delete,
            CommandId.Pack, CommandId.CopyFullNameAsText, CommandId.Properties,
        }, Commands(menu));
        Assert.Equal(ContextMenuEntryKind.WindowsMenu, menu[^1].Kind);
    }

    [Fact]
    public void A_program_can_run_as_administrator()
    {
        IReadOnlyList<ContextMenuEntry> menu = Plan(AFile with { CanRunAsAdministrator = true });
        Assert.Equal(new[] { CommandId.Open, CommandId.RunAsAdministrator, CommandId.OpenWith }, Commands(menu).Take(3));
        Assert.DoesNotContain(CommandId.RunAsAdministrator, Commands(Plan(AFile)));
        Assert.DoesNotContain(CommandId.RunAsAdministrator, Commands(Plan(AFile with { CanRunAsAdministrator = true, MarkedCount = 3 })));
        Assert.True(Core.Interop.ShellOperations.CanRunAsAdministrator(@"C:\x\setup.EXE"));
        Assert.True(Core.Interop.ShellOperations.CanRunAsAdministrator(@"C:\x\build.cmd"));
        Assert.False(Core.Interop.ShellOperations.CanRunAsAdministrator(@"C:\x\notes.txt"));
    }

    [Fact]
    public void Several_marked_items_say_how_many_and_have_no_rename()
    {
        IReadOnlyList<ContextMenuEntry> menu = Plan(AFile with { MarkedCount = 4 });
        Assert.Contains("Copy 4 items to other panel...", Texts(menu));
        Assert.Contains("Delete 4 items", Texts(menu));
        Assert.Contains("Pack into archive...", Texts(menu));
        Assert.DoesNotContain(CommandId.QuickRename, Commands(menu));
        Assert.DoesNotContain(CommandId.OpenWith, Commands(menu));
    }

    [Fact]
    public void An_archive_opens_like_a_folder_with_its_plugin_menu()
    {
        IReadOnlyList<ContextMenuEntry> menu = Plan(AFile with { IsArchiveFile = true, CanUnpack = true, HasArchivePlugin = true });
        Assert.Equal("Open archive", menu[0].Text);
        Assert.Contains(CommandId.OpenInOtherPanel, Commands(menu));
        Assert.Contains(CommandId.Unpack, Commands(menu));
        Assert.Contains(menu, e => e.Kind == ContextMenuEntryKind.ArchivePluginSubmenu);
        // several marked: no plugin submenu (its commands are for one archive)
        Assert.DoesNotContain(Plan(AFile with { IsArchiveFile = true, HasArchivePlugin = true, MarkedCount = 2 }),
            e => e.Kind == ContextMenuEntryKind.ArchivePluginSubmenu);
    }

    [Fact]
    public void A_disk_image_mounts_first_when_configured()
    {
        Assert.Equal(CommandId.MountImage, Plan(AFile with { IsDiskImage = true, MountDiskImages = true })[0].Command);
        IReadOnlyList<ContextMenuEntry> openFirst = Plan(AFile with { IsDiskImage = true, MountDiskImages = false });
        Assert.Equal(CommandId.Open, openFirst[0].Command);
        Assert.Equal(CommandId.MountImage, openFirst[1].Command);
        Assert.False(openFirst[1].Bold);
    }

    [Fact]
    public void Scripts_can_be_run_but_not_folders()
    {
        Assert.Contains(Plan(AFile with { Script = ScriptHost.PowerShell }), e => e.Kind == ContextMenuEntryKind.RunScript);
        Assert.DoesNotContain(Plan(AFile with { Script = ScriptHost.PowerShell, IsDirectory = true }), e => e.Kind == ContextMenuEntryKind.RunScript);
        Assert.DoesNotContain(Plan(AFile), e => e.Kind == ContextMenuEntryKind.RunScript);
    }

    [Fact]
    public void Inside_an_archive_only_editable_ones_move_and_delete()
    {
        var readOnly = new ContextMenuFacts { Target = ContextMenuTarget.ArchiveItem };
        Assert.Equal(new[] { CommandId.Open, CommandId.Copy, CommandId.CopyNameAsText }, Commands(Plan(readOnly)));
        CommandId[] editable = Commands(Plan(readOnly with { EditableArchive = true }));
        Assert.Contains(CommandId.Move, editable);
        Assert.Contains(CommandId.Delete, editable);
        Assert.DoesNotContain(Plan(readOnly), e => e.Kind == ContextMenuEntryKind.WindowsMenu);
    }

    [Fact]
    public void A_folder_offers_new_paste_view_and_sort()
    {
        IReadOnlyList<ContextMenuEntry> menu = Plan(new ContextMenuFacts { Target = ContextMenuTarget.LocalFolder, OffersWindowsMenu = true });
        Assert.Equal(CommandId.CreateDirectory, menu[0].Command);
        Assert.Contains(menu, e => e.Kind == ContextMenuEntryKind.ViewSubmenu);
        Assert.Contains(menu, e => e.Kind == ContextMenuEntryKind.SortSubmenu);
        Assert.DoesNotContain(CommandId.UnmountImage, Commands(menu));
        Assert.Contains("Unmount E:", Texts(Plan(new ContextMenuFacts { Target = ContextMenuTarget.LocalFolder, MountedImageDrive = "e:" })));
    }

    [Fact]
    public void Drives_and_servers()
    {
        CommandId[] drive = Commands(Plan(new ContextMenuFacts { Target = ContextMenuTarget.Drive, IsMountedImageDrive = true }));
        Assert.Equal(new[] { CommandId.Open, CommandId.OpenInOtherPanel, CommandId.UnmountImage, CommandId.DriveInformation, CommandId.Properties }, drive);

        CommandId[] remoteFile = Commands(Plan(new ContextMenuFacts { Target = ContextMenuTarget.RemoteItem }));
        Assert.Contains(CommandId.View, remoteFile);
        Assert.DoesNotContain(CommandId.ClipboardCopy, remoteFile); // the clipboard is for files on disk
        Assert.DoesNotContain(CommandId.View, Commands(Plan(new ContextMenuFacts { Target = ContextMenuTarget.RemoteItem, IsDirectory = true })));

        CommandId[] serverFolder = Commands(Plan(new ContextMenuFacts { Target = ContextMenuTarget.OtherFolder, IsRemotePanel = true }));
        Assert.Equal(new[] { CommandId.CreateDirectory, CommandId.ClipboardPaste, CommandId.Refresh }, serverFolder);
        Assert.Equal(new[] { CommandId.Refresh }, Commands(Plan(new ContextMenuFacts { Target = ContextMenuTarget.OtherFolder })));
    }

    [Fact]
    public void Paste_shortcut_when_the_clipboard_has_files()
    {
        Assert.DoesNotContain(CommandId.PasteShortcut, Commands(Plan(AFile)));
        CommandId[] withFiles = Commands(Plan(AFile with { ClipboardHasFiles = true }));
        Assert.Equal(withFiles.ToList().IndexOf(CommandId.ClipboardPaste) + 1, withFiles.ToList().IndexOf(CommandId.PasteShortcut));
        var folder = new ContextMenuFacts { Target = ContextMenuTarget.LocalFolder };
        Assert.DoesNotContain(CommandId.PasteShortcut, Commands(Plan(folder)));
        Assert.Contains(CommandId.PasteShortcut, Commands(Plan(folder with { ClipboardHasFiles = true })));
        Assert.Contains("Copy as shortcuts", Texts(Plan(AFile with { MarkedCount = 3 })));
    }

    [Fact]
    public void Shortcut_keys_for_the_shortcuts()
    {
        KeyMap salamander = KeyMap.CreateSalamanderDefaults(), tc = KeyMap.CreateTotalCommanderDefaults();
        Assert.Equal(CommandId.CopyAsShortcut, salamander.Resolve(VK.L, KeyModifiers.Ctrl | KeyModifiers.Shift));
        Assert.Equal(CommandId.PasteShortcut, salamander.Resolve(VK.S, KeyModifiers.Ctrl));
        Assert.Equal(CommandId.CopyAsShortcut, tc.Resolve(VK.L, KeyModifiers.Ctrl | KeyModifiers.Shift));
        Assert.Equal(CommandId.PasteShortcut, tc.Resolve(VK.V, KeyModifiers.Ctrl | KeyModifiers.Shift));
    }
}
