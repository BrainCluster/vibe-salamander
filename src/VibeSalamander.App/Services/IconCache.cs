// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using VibeSalamander.Core.FileSystem;

namespace VibeSalamander.Services;

/// <summary>
/// Loads Windows shell icons asynchronously and converts them to WinUI bitmaps. Icons are cached per
/// extension; files with their own icons (.exe, .lnk, .ico, …) and drives are cached per path.
/// Must be used from the UI thread; the shell calls run on background threads.
/// </summary>
public sealed class IconCache
{
    private static readonly HashSet<string> PerFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "lnk", "ico", "url", "cur", "ani", "msc", "scr", "appref-ms",
    };

    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Action<ImageSource?>>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _throttle = new(4);

    public IconCache(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>Calls <paramref name="onLoaded"/> (on the UI thread) with the icon for the entry.</summary>
    /// <param name="generic">the file type's icon only (simple icons, archives, servers): nothing is read from the file</param>
    public void Request(FileEntry entry, bool generic, Action<ImageSource?> onLoaded)
    {
        if (entry.IsUpDir)
            return; // the up-dir uses its own glyph

        (string key, string shellPath, bool useAttributes, uint attributes) = GetKey(entry, generic);
        // an .exe or .lnk whose own icon can't be read gets its type's icon (not a different kind of icon)
        var fallback = key.StartsWith("file:", StringComparison.Ordinal) ? GetKey(entry, generic: true) : default;
        if (_cache.TryGetValue(key, out ImageSource? cached))
        {
            onLoaded(cached);
            return;
        }
        if (_pending.TryGetValue(key, out var waiting))
        {
            waiting.Add(onLoaded);
            return;
        }
        _pending[key] = new List<Action<ImageSource?>> { onLoaded };

        _ = Task.Run(async () =>
        {
            await _throttle.WaitAsync().ConfigureAwait(false);
            IconPixels? pixels;
            try
            {
                pixels = LoadPixels(shellPath, useAttributes, attributes);
                if (pixels == null && fallback.shellPath != null)
                    pixels = LoadPixels(fallback.shellPath, fallback.useAttributes, fallback.attributes);
            }
            catch
            {
                pixels = null;
            }
            finally
            {
                _throttle.Release();
            }
            _dispatcher.TryEnqueue(() => Complete(key, pixels));
        });
    }

    private void Complete(string key, IconPixels? pixels)
    {
        ImageSource? source = null;
        if (pixels != null)
        {
            var bitmap = new WriteableBitmap(pixels.Width, pixels.Height);
            using (Stream stream = bitmap.PixelBuffer.AsStream())
                stream.Write(pixels.Bgra, 0, pixels.Bgra.Length);
            bitmap.Invalidate();
            source = bitmap;
        }
        // icons of individual files (.exe, .lnk, ...) are cached per path: a folder with thousands of them
        // must not keep every icon for the rest of the session
        if (_cache.Count > 2000)
            foreach (string old in _cache.Keys.Where(k => k.StartsWith("file:", StringComparison.Ordinal)).ToList())
                _cache.Remove(old);
        _cache[key] = source;
        if (_pending.Remove(key, out var callbacks))
            foreach (var callback in callbacks)
                callback(source);
    }

    private static (string key, string shellPath, bool useAttributes, uint attributes) GetKey(FileEntry entry, bool generic)
    {
        if (entry.Tag is DriveEntryInfo drive)
            return ("drive:" + drive.Root, drive.Root, false, 0);
        if (entry.IsDirectory)
        {
            // special folders keep their own icons only when queried by path; generic folder icon is enough here
            return ("<dir>", "folder", true, FILE_ATTRIBUTE_DIRECTORY);
        }
        if (!generic && PerFileExtensions.Contains(entry.Extension))
            return ("file:" + entry.FullPath, entry.FullPath, false, 0);
        string ext = entry.Extension.Length > 0 ? "." + entry.Extension : "file";
        return ("ext:" + ext, ext, true, FILE_ATTRIBUTE_NORMAL);
    }

    // ---- HICON → BGRA pixels --------------------------------------------------------------

    private sealed record IconPixels(int Width, int Height, byte[] Bgra);

    private static IconPixels? LoadPixels(string path, bool useAttributes, uint attributes)
    {
        var info = new SHFILEINFO();
        uint flags = SHGFI_ICON | SHGFI_SMALLICON;
        if (useAttributes)
            flags |= SHGFI_USEFILEATTRIBUTES;
        IntPtr result = SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;
        try
        {
            return IconToPixels(info.hIcon);
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private static IconPixels? IconToPixels(IntPtr hIcon)
    {
        if (!GetIconInfo(hIcon, out ICONINFO iconInfo))
            return null;
        try
        {
            if (iconInfo.hbmColor == IntPtr.Zero)
                return null; // monochrome icon, not worth handling
            if (GetObject(iconInfo.hbmColor, Marshal.SizeOf<BITMAP>(), out BITMAP bm) == 0)
                return null;
            int width = bm.bmWidth, height = bm.bmHeight;
            byte[] color = GetBits(iconInfo.hbmColor, width, height);
            if (color.Length == 0)
                return null;

            bool hasAlpha = false;
            for (int i = 3; i < color.Length; i += 4)
                if (color[i] != 0) { hasAlpha = true; break; }

            if (!hasAlpha && iconInfo.hbmMask != IntPtr.Zero)
            {
                // old-style icon: build alpha from the AND mask (white = transparent)
                byte[] mask = GetBits(iconInfo.hbmMask, width, height);
                for (int i = 0; i + 3 < color.Length && i + 3 < mask.Length; i += 4)
                    color[i + 3] = mask[i] == 0 ? (byte)255 : (byte)0;
            }

            // WriteableBitmap expects premultiplied BGRA
            for (int i = 0; i + 3 < color.Length; i += 4)
            {
                byte a = color[i + 3];
                if (a == 255) continue;
                color[i] = (byte)(color[i] * a / 255);
                color[i + 1] = (byte)(color[i + 1] * a / 255);
                color[i + 2] = (byte)(color[i + 2] * a / 255);
            }
            return new IconPixels(width, height, color);
        }
        finally
        {
            if (iconInfo.hbmColor != IntPtr.Zero) DeleteObject(iconInfo.hbmColor);
            if (iconInfo.hbmMask != IntPtr.Zero) DeleteObject(iconInfo.hbmMask);
        }
    }

    private static byte[] GetBits(IntPtr hBitmap, int width, int height)
    {
        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0, // BI_RGB
        };
        byte[] bits = new byte[width * height * 4];
        IntPtr hdc = GetDC(IntPtr.Zero);
        try
        {
            int lines = GetDIBits(hdc, hBitmap, 0, (uint)height, bits, ref bmi, 0);
            return lines == 0 ? Array.Empty<byte>() : bits;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    // ---- interop ---------------------------------------------------------------------------

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_SMALLICON = 0x1;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObject(IntPtr h, int c, out BITMAP pv);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, [Out] byte[] lpvBits, ref BITMAPINFOHEADER lpbmi, uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
}
