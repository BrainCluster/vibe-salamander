// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.Operations;

namespace VibeSalamander.Core.Commands;

/// <summary>What a command does in a panel that shows an archive's content.</summary>
public enum ArchivePanelAction
{
    /// <summary>Not an archive matter: the usual command runs.</summary>
    NotHere,
    CopyOut,
    MoveOut,
    Delete,
    PasteIn,
    OpenCopy,

    /// <summary>F4 in an archive that can be changed: edit a copy, and saving puts it back into the archive.</summary>
    EditCopy,

    /// <summary>A change this archive allows only in other ways: say what can be done (copy in and out, delete).</summary>
    ExplainEditable,

    /// <summary>A change a read-only archive doesn't allow: say how to get the files (copy out, unpack).</summary>
    ExplainReadOnly,
}

/// <summary>What a command does in a panel that shows a server (FTP, SFTP).</summary>
public enum ServerPanelAction
{
    NotHere,
    Download,
    DownloadAndDelete,
    Delete,
    CreateDirectory,
    Disconnect,
    Rename,
    View,
    Edit,
    PasteUp,

    /// <summary>Something servers can't do: say what can be done there.</summary>
    Explain,
}

/// <summary>
/// The commands that work differently inside an archive or on a server. F5 / Alt+F6 copy out (download), F3 / F4
/// open a temporary copy; ZIP and TAR archives (editable) also move out, delete and take pasted files; servers
/// also delete, rename, make directories and disconnect (F12).
/// </summary>
public static class PanelCommandRules
{
    public static ArchivePanelAction InArchive(CommandId command, bool editable) => command switch
    {
        CommandId.Copy or CommandId.Unpack => ArchivePanelAction.CopyOut,
        CommandId.Move when editable => ArchivePanelAction.MoveOut,
        CommandId.Delete or CommandId.DeletePermanently when editable => ArchivePanelAction.Delete,
        CommandId.ClipboardPaste when editable => ArchivePanelAction.PasteIn,
        CommandId.Edit when editable => ArchivePanelAction.EditCopy,
        CommandId.View or CommandId.Edit => ArchivePanelAction.OpenCopy,
        CommandId.Move or CommandId.Delete or CommandId.DeletePermanently or CommandId.QuickRename or CommandId.CreateDirectory or
        CommandId.EditNewFile or CommandId.ChangeAttributes or CommandId.ChangeCase or CommandId.ClipboardCut or
        CommandId.ClipboardCopy or CommandId.ClipboardPaste or CommandId.Pack or CommandId.NewMenu =>
            editable ? ArchivePanelAction.ExplainEditable : ArchivePanelAction.ExplainReadOnly,
        _ => ArchivePanelAction.NotHere,
    };

    public static ServerPanelAction OnServer(CommandId command) => command switch
    {
        CommandId.Copy or CommandId.Unpack => ServerPanelAction.Download,
        CommandId.Move => ServerPanelAction.DownloadAndDelete,
        CommandId.Delete or CommandId.DeletePermanently => ServerPanelAction.Delete,
        CommandId.CreateDirectory => ServerPanelAction.CreateDirectory,
        CommandId.DisconnectNetworkDrive => ServerPanelAction.Disconnect,
        CommandId.QuickRename => ServerPanelAction.Rename,
        CommandId.View => ServerPanelAction.View,
        CommandId.Edit => ServerPanelAction.Edit,
        CommandId.ClipboardPaste => ServerPanelAction.PasteUp,
        CommandId.EditNewFile or CommandId.ChangeAttributes or CommandId.ChangeCase or CommandId.ClipboardCut or
        CommandId.ClipboardCopy or CommandId.Pack or CommandId.NewMenu or CommandId.CommandShell or
        CommandId.OpenActiveFolder => ServerPanelAction.Explain,
        _ => ServerPanelAction.NotHere,
    };

    /// <summary>
    /// Copying out of an archive or down from a server asks for the target unless Configuration says not to and the other
    /// panel is a directory on disk (the only kind of target there is).
    /// </summary>
    public static bool AskForTargetOnDisk(bool askSetting, bool otherPanelOnDisk) => askSetting || !otherPanelOnDisk;

    /// <summary>
    /// A move deletes what it moved only when everything arrived: nothing cancelled, failed or skipped.
    /// </summary>
    public static bool Completed(OperationResult result) => !result.Cancelled && result.Failed == 0 && result.Skipped == 0;

    /// <summary>True when <paramref name="path"/> is inside one of the directories (a move must not delete what holds its target).</summary>
    public static bool IsInsideAny(string path, IEnumerable<string> directories) =>
        directories.Any(directory => path.StartsWith(directory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Names typed for servers: "/" separates directories there.</summary>
public static class ServerNames
{
    /// <summary>The directories of a typed path ("a/b", "a\b", "/a//b/") in order; empty when only separators were typed.</summary>
    public static string[] PathParts(string typed) =>
        typed.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Why a new name can't be used for a rename on a server, or null (null for an unchanged or empty name too).</summary>
    public static string? RenameProblem(string? newName) =>
        newName != null && newName.IndexOfAny(new[] { '/', '\\' }) >= 0 ? "The name can't contain '/' or '\\'." : null;
}
