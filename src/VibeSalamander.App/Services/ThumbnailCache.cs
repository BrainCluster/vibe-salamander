// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using VibeSalamander.Core.FileSystem;

namespace VibeSalamander.Services;

/// <summary>
/// Shell thumbnails for the Icons, Thumbnails and Tiles views: picture/video previews, and the large
/// icon for everything else. Loaded only for visible items and cached per file version.
/// <para>
/// Scrolling through thousands of pictures: the newest requests are served first, requests of rows that
/// scrolled away are dropped before they start, and thumbnails Windows already has in its thumbnail cache
/// (the one Explorer uses) are shown for every visible row before any missing one is generated.
/// The shell calls and the pixel conversion run on background threads.
/// </para>
/// </summary>
public sealed class ThumbnailCache
{
    /// <summary>
    /// Memory budget for the kept thumbnails (decoded pixels): the least recently used ones go first.
    /// 48 MB is about 190 thumbnails of 256 px or 1300 of 96 px.
    /// </summary>
    private const long BudgetBytes = 48L * 1024 * 1024;

    /// <summary>Thumbnails loaded at the same time.</summary>
    private static readonly int Parallel = Math.Clamp(Environment.ProcessorCount, 2, 8);

    private readonly Dictionary<string, LinkedListNode<(string Key, ImageSource Image, long Bytes)>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<(string Key, ImageSource Image, long Bytes)> _recent = new();
    private long _bytes;

    /// <summary>Requests waiting or loading, by key.</summary>
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Waiting for the thumbnail cache lookup / for generating; the newest at the end (served first).</summary>
    private readonly List<Job> _lookupQueue = new();
    private readonly List<Job> _generateQueue = new();
    private int _running;

    internal sealed class Job(string key, string path, bool isDirectory, uint size)
    {
        public readonly string Key = key;
        public readonly string Path = path;
        public readonly bool IsDirectory = isDirectory;
        public readonly uint Size = size;
        public readonly List<Ticket> Waiters = new();
        public bool LookedUp;
        public bool Running;
        public bool Wanted => Waiters.Exists(w => !w.Cancelled);
    }

    /// <summary>A pending request; <see cref="Cancel"/> it when the row scrolls out of view.</summary>
    public sealed class Ticket
    {
        internal Job? Job;
        internal Action<ImageSource?>? Callback;
        internal bool Cancelled;
    }

    /// <summary>Use from the UI thread of <paramref name="dispatcher"/> (the loads continue there).</summary>
    public ThumbnailCache(DispatcherQueue dispatcher)
    {
        _ = dispatcher;
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

    /// <summary>
    /// Calls <paramref name="onLoaded"/> on the UI thread with the thumbnail (null when there is none).
    /// Returns null when it was called right away (cached), else a ticket for <see cref="Cancel"/>.
    /// Call from the UI thread.
    /// </summary>
    public Ticket? Request(FileEntry entry, uint size, Action<ImageSource?> onLoaded)
    {
        string key = Key(entry, size);
        if (_cache.TryGetValue(key, out var cached))
        {
            _recent.Remove(cached);
            _recent.AddFirst(cached); // used again: kept longer
            onLoaded(cached.Value.Image);
            return null;
        }

        if (_jobs.TryGetValue(key, out Job? job))
        {
            if (!job.Running)
            {
                // wanted again: to the front of its queue
                List<Job> queue = job.LookedUp ? _generateQueue : _lookupQueue;
                queue.Remove(job);
                queue.Add(job);
            }
        }
        else
        {
            job = new Job(key, entry.FullPath, entry.IsDirectory, size);
            _jobs[key] = job;
            _lookupQueue.Add(job);
        }
        var ticket = new Ticket { Job = job, Callback = onLoaded };
        job.Waiters.Add(ticket);
        Pump();
        return ticket;
    }

    /// <summary>The row scrolled away: its thumbnail is not loaded unless it is already loading.</summary>
    public void Cancel(Ticket? ticket)
    {
        if (ticket == null || ticket.Cancelled || ticket.Job is not { } job)
            return;
        ticket.Cancelled = true;
        ticket.Callback = null;
        if (job.Running || job.Wanted)
            return;
        (job.LookedUp ? _generateQueue : _lookupQueue).Remove(job);
        _jobs.Remove(job.Key);
    }

    private void Pump()
    {
        while (_running < Parallel)
        {
            // every visible row gets its cached thumbnail before any missing one is generated
            List<Job> queue = _lookupQueue.Count > 0 ? _lookupQueue : _generateQueue;
            if (queue.Count == 0)
                return;
            Job job = queue[^1];
            queue.RemoveAt(queue.Count - 1);
            job.Running = true;
            _running++;
            _ = RunAsync(job);
        }
    }

    /// <summary>Runs on the UI thread (the awaits resume there); the shell work runs on the thread pool.</summary>
    private async Task RunAsync(Job job)
    {
        bool lookupOnly = !job.LookedUp;
        ImageSource? image = null;
        long bytes = 0;
        try
        {
            ShellThumbnails.Pixels? pixels = await Task.Run(() => ShellThumbnails.Get(job.Path, job.Size, lookupOnly));
            if (pixels != null)
            {
                // the pixels are ready: copying them into the bitmap is all the UI thread does
                var bitmap = new WriteableBitmap(pixels.Width, pixels.Height);
                using (Stream stream = bitmap.PixelBuffer.AsStream())
                    stream.Write(pixels.Bgra, 0, pixels.Bgra.Length);
                bitmap.Invalidate();
                image = bitmap;
                bytes = pixels.Bgra.Length;
            }
            else if (!lookupOnly)
            {
                (image, bytes) = await LoadWinRtAsync(job);
            }
        }
        catch (Exception)
        {
            // no thumbnail (access denied, offline file, unsupported item): the glyph stays
        }
        finally
        {
            job.Running = false;
            _running--;
        }

        if (image == null && lookupOnly)
        {
            // not in Windows' thumbnail cache: generated after the cached ones of the visible rows
            job.LookedUp = true;
            if (job.Wanted)
                _generateQueue.Add(job);
            else
                _jobs.Remove(job.Key);
            Pump();
            return;
        }

        _jobs.Remove(job.Key);
        if (image != null)
            Keep(job.Key, image, bytes);
        foreach (Ticket ticket in job.Waiters)
        {
            if (!ticket.Cancelled && ticket.Callback is { } callback)
                callback(image);
            ticket.Job = null;
            ticket.Callback = null;
        }
        Pump();
    }

    /// <summary>The slower WinRT way, for items the shell image factory could not handle.</summary>
    private static async Task<(ImageSource?, long)> LoadWinRtAsync(Job job)
    {
        StorageItemThumbnail? thumbnail = job.IsDirectory
            ? await (await StorageFolder.GetFolderFromPathAsync(job.Path))
                .GetThumbnailAsync(ThumbnailMode.SingleItem, job.Size, ThumbnailOptions.ResizeThumbnail)
            : await (await StorageFile.GetFileFromPathAsync(job.Path))
                .GetThumbnailAsync(ThumbnailMode.SingleItem, job.Size, ThumbnailOptions.ResizeThumbnail);
        if (thumbnail == null)
            return (null, 0);
        using (thumbnail)
        {
            // no DecodePixelWidth: ResizeThumbnail already scales the longest edge to size
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(thumbnail);
            return (bitmap, (long)job.Size * job.Size * 4);
        }
    }
}

/// <summary>Thumbnails straight from the shell (IShellItemImageFactory), as premultiplied BGRA pixels.</summary>
internal static class ShellThumbnails
{
    public sealed record Pixels(int Width, int Height, byte[] Bgra);

    private const int SIIGBF_RESIZETOFIT = 0x00;
    private const int SIIGBF_THUMBNAILONLY = 0x08;
    private const int SIIGBF_INCACHEONLY = 0x10;

    /// <summary>
    /// The thumbnail (or, for items without one, the large icon) fitted into size × size.
    /// <paramref name="cacheOnly"/>: only a thumbnail already in Windows' thumbnail cache (fast), else null.
    /// Runs on a background thread.
    /// </summary>
    public static Pixels? Get(string path, uint size, bool cacheOnly)
    {
        Guid iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out IShellItemImageFactory? factory) < 0 || factory == null)
            return null;
        try
        {
            int flags = cacheOnly ? SIIGBF_INCACHEONLY | SIIGBF_THUMBNAILONLY : SIIGBF_RESIZETOFIT;
            if (factory.GetImage(new SIZE { cx = (int)size, cy = (int)size }, flags, out IntPtr hBitmap) < 0 || hBitmap == IntPtr.Zero)
                return null;
            try
            {
                return ToPixels(hBitmap);
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    private static Pixels? ToPixels(IntPtr hBitmap)
    {
        if (GetObject(hBitmap, Marshal.SizeOf<BITMAP>(), out BITMAP bm) == 0)
            return null;
        int width = bm.bmWidth, height = Math.Abs(bm.bmHeight);
        if (width <= 0 || height <= 0)
            return null;
        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        byte[] bits = new byte[width * height * 4];
        IntPtr hdc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(hdc, hBitmap, 0, (uint)height, bits, ref bmi, 0) == 0)
                return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }

        // photos usually come without alpha (all 0): opaque. Icons come with it, mostly premultiplied already;
        // a color brighter than its alpha means it is not, and WriteableBitmap wants premultiplied pixels.
        bool anyAlpha = false, premultiplied = true;
        for (int i = 0; i < bits.Length; i += 4)
        {
            byte a = bits[i + 3];
            if (a != 0) anyAlpha = true;
            if (bits[i] > a || bits[i + 1] > a || bits[i + 2] > a) premultiplied = false;
        }
        if (!anyAlpha)
        {
            for (int i = 3; i < bits.Length; i += 4)
                bits[i] = 255;
        }
        else if (!premultiplied)
        {
            for (int i = 0; i < bits.Length; i += 4)
            {
                byte a = bits[i + 3];
                if (a == 255) continue;
                bits[i] = (byte)(bits[i] * a / 255);
                bits[i + 1] = (byte)(bits[i + 1] * a / 255);
                bits[i + 2] = (byte)(bits[i + 2] * a / 255);
            }
        }
        return new Pixels(width, height, bits);
    }

    // ---- interop ---------------------------------------------------------------------------

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? ppv);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, out BITMAP lpvObject);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, [Out] byte[] lpvBits, ref BITMAPINFOHEADER lpbi, uint uUsage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
}
