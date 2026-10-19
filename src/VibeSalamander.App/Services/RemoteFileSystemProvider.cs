// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.FileSystem;
using VibeSalamander.Plugins;

namespace VibeSalamander.Services;

/// <summary>
/// Plugin file systems in the panels (FTP, SFTP, ...): paths like "sftp://user@host/dir" are listed by
/// the plugin that owns them. ".." at a server's root goes to This PC. Copies, deletes and the other
/// changes run in the transfer panel (MainViewModel, through IPluginFileSystem).
/// </summary>
public sealed class RemoteFileSystemProvider : IFileSystemProvider
{
    private readonly PluginManager _plugins;

    public RemoteFileSystemProvider(PluginManager plugins) => _plugins = plugins;

    public string Id => "remote";

    public bool IsLocalDisk => false;

    public bool CanHandle(string path) => _plugins.FindFileSystem(path) != null;

    /// <summary>The plugin file system of a path, or null.</summary>
    public IPluginFileSystem? FileSystemOf(string path) => _plugins.FindFileSystem(path);

    public string GetDisplayPath(string path)
    {
        try
        {
            return FileSystemOf(path)?.GetDisplayPath(path) ?? path;
        }
        catch (Exception)
        {
            return path;
        }
    }

    /// <summary>"dir" of "sftp://host/a/dir".</summary>
    public static string LastName(string path)
    {
        string trimmed = path.TrimEnd('/');
        int scheme = trimmed.IndexOf("://", StringComparison.Ordinal);
        int slash = trimmed.LastIndexOf('/');
        return slash > scheme + 2 ? trimmed[(slash + 1)..] : trimmed;
    }

    public ParentLocation? GetParent(string path)
    {
        IPluginFileSystem? fs = FileSystemOf(path);
        string? parent;
        try
        {
            parent = fs?.GetParent(path);
        }
        catch (Exception)
        {
            parent = null;
        }
        // the root of the server: out to the drives
        return parent == null ? new ParentLocation(ThisPcProvider.RootPath, null) : new ParentLocation(parent, LastName(path));
    }

    public async Task<DirectoryListing> ListAsync(string path, ListingOptions options, CancellationToken cancellationToken)
    {
        var entries = new List<FileEntry>();
        ParentLocation? parent = GetParent(path);
        if (parent != null)
            entries.Add(FileEntry.CreateUpDir(parent.Path));
        IPluginFileSystem? fs = FileSystemOf(path);
        if (fs == null)
            return new DirectoryListing(path, entries, "The plugin of this location is not enabled.");
        IReadOnlyList<RemoteItem> items;
        try
        {
            items = await Task.Run(() => fs.ListAsync(path, cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DirectoryListing(path, entries, ex.Message);
        }
        int hidden = 0;
        foreach (RemoteItem item in items)
        {
            if (item.IsHidden && !options.ShowHidden)
            {
                hidden++;
                continue;
            }
            FileAttributes attributes = (item.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal) |
                                        (item.IsHidden ? FileAttributes.Hidden : 0) |
                                        (item.IsLink ? FileAttributes.ReparsePoint : 0);
            entries.Add(new FileEntry(item.Name, item.Path, item.IsDirectory, item.IsDirectory ? 0 : item.Size, item.LastWrite,
                attributes, tag: item));
        }
        return new DirectoryListing(path, entries) { HiddenCount = hidden };
    }

    /// <summary>The plugin's item of a panel entry (its listing put it in the tag).</summary>
    public static RemoteItem ItemOf(FileEntry entry) =>
        entry.Tag as RemoteItem ?? new RemoteItem(entry.Name, entry.FullPath, entry.IsDirectory, entry.Size, entry.LastWrite);
}
