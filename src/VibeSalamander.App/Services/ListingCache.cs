// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.FileSystem;

namespace VibeSalamander.Services;

/// <summary>
/// The last folders the panels showed, so going back, up or into a folder seen a moment ago shows it
/// at once. The panel then reads the folder again in the background and replaces the listing only
/// when something changed. Shared by both panels; local and network folders only.
/// </summary>
public static class ListingCache
{
    private const int Capacity = 48;
    private const int MaxEntries = 20000; // huge folders: not worth the memory

    private static readonly object Lock = new();
    private static readonly LinkedList<(string Key, DirectoryListing Listing)> Order = new();
    private static readonly Dictionary<string, LinkedListNode<(string Key, DirectoryListing Listing)>> Map =
        new(StringComparer.OrdinalIgnoreCase);

    private static string Key(string path, bool showHidden) => (showHidden ? "1|" : "0|") + path.TrimEnd('\\');

    public static DirectoryListing? Get(string path, bool showHidden)
    {
        lock (Lock)
        {
            if (!Map.TryGetValue(Key(path, showHidden), out var node))
                return null;
            Order.Remove(node);
            Order.AddFirst(node);
            return node.Value.Listing;
        }
    }

    public static void Put(string path, bool showHidden, DirectoryListing listing)
    {
        if (listing.Error != null || listing.Entries.Count > MaxEntries)
        {
            Remove(path);
            return;
        }
        string key = Key(path, showHidden);
        lock (Lock)
        {
            if (Map.TryGetValue(key, out var old))
                Order.Remove(old);
            var node = Order.AddFirst((key, listing));
            Map[key] = node;
            while (Order.Count > Capacity && Order.Last is { } last)
            {
                Map.Remove(last.Value.Key);
                Order.RemoveLast();
            }
        }
    }

    /// <summary>Forgets a folder (deleted, or its content changed in a way a refresh must show).</summary>
    public static void Remove(string path)
    {
        lock (Lock)
        {
            foreach (string prefix in new[] { "0|", "1|" })
                if (Map.Remove(prefix + path.TrimEnd('\\'), out var node))
                    Order.Remove(node);
        }
    }

    /// <summary>True when two listings show the same items (names, sizes, dates, attributes).</summary>
    public static bool SameContent(DirectoryListing a, DirectoryListing b)
    {
        if (a.Entries.Count != b.Entries.Count || a.HiddenCount != b.HiddenCount || a.Error != b.Error)
            return false;
        var byName = new Dictionary<string, FileEntry>(a.Entries.Count, StringComparer.Ordinal);
        foreach (FileEntry e in a.Entries)
            byName[e.Name] = e;
        foreach (FileEntry e in b.Entries)
        {
            if (!byName.TryGetValue(e.Name, out FileEntry? other) || other.Size != e.Size || other.LastWrite != e.LastWrite ||
                other.Attributes != e.Attributes || other.IsDirectory != e.IsDirectory)
                return false;
        }
        return true;
    }
}
