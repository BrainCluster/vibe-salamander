// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.Commands;
using VibeSalamander.Core.Operations;

namespace VibeSalamander.Core.Tests;

/// <summary>Commands inside archives and on servers (Core.Commands.PanelCommandRules, ServerNames).</summary>
public class PanelCommandRulesTests
{
    [Theory]
    [InlineData(CommandId.Copy, false, ArchivePanelAction.CopyOut)]
    [InlineData(CommandId.Unpack, false, ArchivePanelAction.CopyOut)]
    [InlineData(CommandId.View, false, ArchivePanelAction.OpenCopy)]
    [InlineData(CommandId.Edit, true, ArchivePanelAction.OpenCopy)]
    [InlineData(CommandId.Move, true, ArchivePanelAction.MoveOut)]
    [InlineData(CommandId.Delete, true, ArchivePanelAction.Delete)]
    [InlineData(CommandId.DeletePermanently, true, ArchivePanelAction.Delete)]
    [InlineData(CommandId.ClipboardPaste, true, ArchivePanelAction.PasteIn)]
    [InlineData(CommandId.QuickRename, true, ArchivePanelAction.ExplainEditable)]
    [InlineData(CommandId.CreateDirectory, true, ArchivePanelAction.ExplainEditable)]
    [InlineData(CommandId.Move, false, ArchivePanelAction.ExplainReadOnly)]   // 7z, RAR: read-only
    [InlineData(CommandId.Delete, false, ArchivePanelAction.ExplainReadOnly)]
    [InlineData(CommandId.ClipboardPaste, false, ArchivePanelAction.ExplainReadOnly)]
    [InlineData(CommandId.Pack, false, ArchivePanelAction.ExplainReadOnly)]
    [InlineData(CommandId.Refresh, true, ArchivePanelAction.NotHere)]
    [InlineData(CommandId.SelectAll, false, ArchivePanelAction.NotHere)]
    public void Commands_inside_an_archive(CommandId command, bool editable, ArchivePanelAction action) =>
        Assert.Equal(action, PanelCommandRules.InArchive(command, editable));

    [Theory]
    [InlineData(CommandId.Copy, ServerPanelAction.Download)]
    [InlineData(CommandId.Unpack, ServerPanelAction.Download)]
    [InlineData(CommandId.Move, ServerPanelAction.DownloadAndDelete)]
    [InlineData(CommandId.Delete, ServerPanelAction.Delete)]
    [InlineData(CommandId.DeletePermanently, ServerPanelAction.Delete)]
    [InlineData(CommandId.CreateDirectory, ServerPanelAction.CreateDirectory)]
    [InlineData(CommandId.DisconnectNetworkDrive, ServerPanelAction.Disconnect)]
    [InlineData(CommandId.QuickRename, ServerPanelAction.Rename)]
    [InlineData(CommandId.View, ServerPanelAction.View)]
    [InlineData(CommandId.Edit, ServerPanelAction.Edit)]
    [InlineData(CommandId.ClipboardPaste, ServerPanelAction.PasteUp)]
    [InlineData(CommandId.ChangeAttributes, ServerPanelAction.Explain)]
    [InlineData(CommandId.CommandShell, ServerPanelAction.Explain)]
    [InlineData(CommandId.Refresh, ServerPanelAction.NotHere)]
    [InlineData(CommandId.GoBack, ServerPanelAction.NotHere)]
    public void Commands_on_a_server(CommandId command, ServerPanelAction action) =>
        Assert.Equal(action, PanelCommandRules.OnServer(command));

    [Fact]
    public void Every_file_command_is_decided_inside_archives_and_on_servers()
    {
        // the commands that change files must never fall through to the disk's own command in these panels
        CommandId[] fileCommands =
        {
            CommandId.Copy, CommandId.Move, CommandId.Delete, CommandId.DeletePermanently, CommandId.QuickRename,
            CommandId.CreateDirectory, CommandId.EditNewFile, CommandId.ChangeAttributes, CommandId.ChangeCase,
            CommandId.ClipboardCut, CommandId.ClipboardCopy, CommandId.ClipboardPaste, CommandId.Pack, CommandId.NewMenu,
        };
        foreach (CommandId command in fileCommands)
        {
            Assert.NotEqual(ArchivePanelAction.NotHere, PanelCommandRules.InArchive(command, editable: false));
            Assert.NotEqual(ArchivePanelAction.NotHere, PanelCommandRules.InArchive(command, editable: true));
            Assert.NotEqual(ServerPanelAction.NotHere, PanelCommandRules.OnServer(command));
        }
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]  // the other panel isn't a directory on disk: ask
    public void Copying_out_asks_for_a_target_on_disk(bool askSetting, bool otherOnDisk, bool asks) =>
        Assert.Equal(asks, PanelCommandRules.AskForTargetOnDisk(askSetting, otherOnDisk));

    [Fact]
    public void A_move_completes_only_when_everything_arrived()
    {
        Assert.True(PanelCommandRules.Completed(new OperationResult { Succeeded = 3 }));
        Assert.False(PanelCommandRules.Completed(new OperationResult { Failed = 1 }));
        Assert.False(PanelCommandRules.Completed(new OperationResult { Skipped = 1 }));
        Assert.False(PanelCommandRules.Completed(new OperationResult { Cancelled = true }));
    }

    [Fact]
    public void A_path_inside_a_moved_directory()
    {
        Assert.True(PanelCommandRules.IsInsideAny(@"C:\a\b\x.zip", new[] { @"C:\a" }));
        Assert.True(PanelCommandRules.IsInsideAny(@"C:\A\x.zip", new[] { @"c:\a\" }));
        Assert.False(PanelCommandRules.IsInsideAny(@"C:\ab\x.zip", new[] { @"C:\a" })); // a name that only starts the same
        Assert.False(PanelCommandRules.IsInsideAny(@"C:\a", new[] { @"C:\a" }));
    }

    [Fact]
    public void Server_paths_and_names()
    {
        Assert.Equal(new[] { "a", "b" }, ServerNames.PathParts(@"/a//b\"));
        Assert.Equal(new[] { "new" }, ServerNames.PathParts("new"));
        Assert.Empty(ServerNames.PathParts("//"));
        Assert.NotNull(ServerNames.RenameProblem("a/b"));
        Assert.NotNull(ServerNames.RenameProblem(@"a\b"));
        Assert.Null(ServerNames.RenameProblem("a b.txt"));
        Assert.Null(ServerNames.RenameProblem(null));
    }
}
