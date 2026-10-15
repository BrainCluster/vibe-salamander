// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using VibeSalamander.Core.FileSystem;

namespace VibeSalamander.Services;

/// <summary>
/// Shell thumbnails for the Icons, Thumbnails and Tiles views: picture/video previews, and the large
/// icon for everything else. Loaded only for visible items, a few at a time, and cached per file version.
/// </summary>
public sealed class ThumbnailCache
{
    /// <summary>
    /// Memory budget for the kept thumbnails (decoded pixels): the least recently used ones go first.
    /// 48 MB is about 190 thumbnails of 256 px or 1300 of 96 px.
    /// </summary>
    private const long BudgetBytes = 48L * 1024 * 1024;
    private const int Parallel = 4;

    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<string, LinkedListNode<(string Key, ImageSource Image, long Bytes)>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<(string Key, ImageSource Image, long Bytes)> _recent = new();
    private long _bytes;
    private readonly Dictionary<string, List<Action<ImageSource?>>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _slots = new(Parallel, Parallel);

    public ThumbnailCache(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    private void Keep(string key, ImageSource image, long bytes)
    {
        if (_cache.Remove(key, out var old))
        {
            _recent.Remove(old);
            _bytes -= old.Value.Bytes;
        }
        _cache[key] = _recent.AddFirst((key, image, bytes));
        _bytes += bytes;
        while (_bytes > BudgetBytes && _recent.Last is { } last && last != _recent.First)
        {
            _recent.RemoveLast();
            _cache.Remove(last.Value.Key);
            _bytes -= last.Value.Bytes;
        }
    }

    /// <summary>Forgets every kept thumbnail (the window was minimized).</summary>
    public void Clear()
    {
        _cache.Clear();
        _recent.Clear();
        _bytes = 0;
    }

    private static string Key(FileEntry entry, uint size) =>
        $"{entry.FullPath}|{size}|{entry.LastWrite.Ticks}|{entry.Size}";

    /// <summary>Calls <paramref name="onLoaded"/> on the UI thread with the thumbnail (null when there is none).</summary>
    public void Request(FileEntry entry, uint size, Action<ImageSource?> onLoaded)
    {
        string key = Key(entry, size);
        if (_cache.TryGetValue(key, out var cached))
        {
            _recent.Remove(cached);
            _recent.AddFirst(cached); // used again: kept longer
            onLoaded(cached.Value.Image);
            return;
        }
        if (_pending.TryGetValue(key, out var waiting))
        {
            waiting.Add(onLoaded);
            return;
        }
        _pending[key] = new List<Action<ImageSource?>> { onLoaded };
        _ = LoadAsync(entry, size, key);
    }

    private async Task LoadAsync(FileEntry entry, uint size, string key)
    {
        ImageSource? image = null;
        await _slots.WaitAsync();
        try
        {
            StorageItemThumbnail? thumbnail = entry.IsDirectory
                ? await (await StorageFolder.GetFolderFromPathAsync(entry.FullPath))
                    .GetThumbnailAsync(ThumbnailMode.SingleItem, size, ThumbnailOptions.ResizeThumbnail)
                : await (await StorageFile.GetFileFromPathAsync(entry.FullPath))
                    .GetThumbnailAsync(ThumbnailMode.SingleItem, size, ThumbnailOptions.ResizeThumbnail);
            if (thumbnail != null)
            {
                using (thumbnail)
                {
                    // BitmapImage belongs to the UI thread; this method runs there (Request is called from the UI,
                    // and the awaits resume on its DispatcherQueueSynchronizationContext). No DecodePixelWidth:
                    // ResizeThumbnail already scales the longest edge to size, and a fixed width would upscale portraits.
                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(thumbnail);
                    image = bitmap;
                }
            }
        }
        catch (Exception)
        {
            // no thumbnail (access denied, offline file, unsupported item): the glyph stays
        }
        finally
        {
            _slots.Release();
        }

        if (image != null)
            Keep(key, image, (long)size * size * 4);
        if (_pending.Remove(key, out var callbacks))
        {
            foreach (var callback in callbacks)
                _dispatcher.TryEnqueue(() => callback(image));
        }
    }
}
