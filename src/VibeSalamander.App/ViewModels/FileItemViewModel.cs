using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using VibeSalamander.Core;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Formatting;

namespace VibeSalamander.ViewModels;

/// <summary>One row of a panel. The ListView selection is the caret; <see cref="IsMarked"/> is the Salamander selection.</summary>
public sealed class FileItemViewModel : BindableBase
{
    private bool _isMarked;
    private ImageSource? _icon;
    private bool _iconRequested;

    private static readonly ColumnLayout DefaultColumns = new(new Core.Panels.ColumnSettings());

    public FileItemViewModel(FileEntry entry, ColumnLayout? columns = null)
    {
        Entry = entry;
        Columns = columns ?? DefaultColumns;
    }

    public FileEntry Entry { get; }

    /// <summary>The panel's Detailed view column widths (shared by all rows of the panel).</summary>
    public ColumnLayout Columns { get; }

    public DriveEntryInfo? Drive => Entry.Tag as DriveEntryInfo;
    public bool IsDrive => Entry.Tag is DriveEntryInfo;

    public double RowHeight => UiState.RowHeight;

    // ---- file columns ----------------------------------------------------------------------

    /// <summary>Name column: without extension for files (the Ext column shows it), like Salamander.</summary>
    public string Name => Entry.IsUpDir ? ".." : Entry.IsDirectory ? Entry.Name : Entry.BaseName;
    public string Ext => Entry.Extension;
    public string SizeText =>
        Entry.IsUpDir ? "UP-DIR" :
        Entry.IsDirectory ? (_directorySize is long d ? Formats.ExactSize(d) : "DIR") :
        Formats.ExactSize(Entry.Size);

    private long? _directorySize;

    /// <summary>Calculated size of a directory's content (Alt+F10 / Ctrl+Shift+F10); null = not calculated.</summary>
    public long? DirectorySize
    {
        get => _directorySize;
        set
        {
            if (SetProperty(ref _directorySize, value))
            {
                OnPropertyChanged(nameof(SizeText));
                OnPropertyChanged(nameof(TileDetail));
            }
        }
    }

    /// <summary>Name with extension (Brief, Icons, Thumbnails, Tiles views).</summary>
    public string FullName => Entry.IsUpDir ? ".." : Entry.Name;

    /// <summary>Second line of a tile: the kind of item.</summary>
    public string TileType => Entry.IsUpDir ? "Parent directory" :
        Entry.IsDirectory ? "File folder" :
        Entry.Extension.Length > 0 ? Entry.Extension.ToUpperInvariant() + " file" : "File";

    /// <summary>Third line of a tile: size and date.</summary>
    public string TileDetail => Entry.IsUpDir ? string.Empty :
        (Entry.IsDirectory
            ? (_directorySize is long d ? Formats.ShortSize(d) + "  ·  " : string.Empty)
            : Formats.ShortSize(Entry.Size) + "  ·  ") + Formats.Date(Entry.LastWrite);
    public string DateText => Entry.IsUpDir ? string.Empty : Formats.Date(Entry.LastWrite);
    public string TimeText => Entry.IsUpDir ? string.Empty : Formats.Time(Entry.LastWrite);
    public string AttrText => Entry.IsUpDir ? string.Empty : Formats.Attributes(Entry.Attributes);

    // ---- drive columns (This PC) -----------------------------------------------------------

    public string DriveLabel => Drive is { } d ? (!string.IsNullOrEmpty(d.Label) ? d.Label : d.RemoteName) : string.Empty;
    public string DriveType => Drive?.TypeName ?? string.Empty;
    public string DriveTotal => Drive is { HasSize: true } d ? Formats.ShortSize(d.TotalSize) : string.Empty;
    public string DriveFree => Drive is { HasSize: true } d ? Formats.ShortSize(d.FreeSpace) : string.Empty;
    public double UsedPercent => Math.Max(0, Drive?.UsedPercent ?? 0);
    public Visibility UsedVisibility => Drive is { HasSize: true } ? Visibility.Visible : Visibility.Collapsed;
    public string UsedText => Drive is { HasSize: true } d ? d.UsedPercent + " %" : string.Empty;
    public Brush UsedBrush => (Drive?.UsedPercent ?? 0) >= 90 ? UiState.CriticalBrush : UiState.AccentBarBrush;

    // ---- appearance ------------------------------------------------------------------------

    public bool IsMarked
    {
        get => _isMarked;
        set
        {
            if (SetProperty(ref _isMarked, value))
            {
                OnPropertyChanged(nameof(NameBrush));
                OnPropertyChanged(nameof(TextBrush));
            }
        }
    }

    private bool _isRenaming;

    /// <summary>F2: the name and extension labels are replaced by an edit box in the row itself.</summary>
    public bool IsRenaming
    {
        get => _isRenaming;
        set
        {
            if (SetProperty(ref _isRenaming, value))
            {
                OnPropertyChanged(nameof(NameVisibility));
                OnPropertyChanged(nameof(RenameVisibility));
            }
        }
    }

    public Visibility NameVisibility => _isRenaming ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RenameVisibility => _isRenaming ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Hidden/system items are drawn dimmed.</summary>
    public double ItemOpacity => Entry.IsHidden || Entry.IsSystem ? 0.55 : 1.0;

    public Brush NameBrush => _isMarked ? UiState.MarkedBrush : UiState.TextBrush;
    public Brush TextBrush => _isMarked ? UiState.MarkedBrush : UiState.SecondaryTextBrush;

    public string Glyph =>
        Entry.IsUpDir ? "" :          // Up
        IsDrive ? "" :                // HardDrive
        Entry.IsDirectory ? "" :      // Folder
        "";                           // Page

    public Brush GlyphBrush => Entry.IsDirectory && !Entry.IsUpDir && !IsDrive ? UiState.FolderBrush : UiState.SecondaryTextBrush;

    public ImageSource? Icon
    {
        get => _icon;
        private set
        {
            if (SetProperty(ref _icon, value))
                OnPropertyChanged(nameof(GlyphVisibility));
        }
    }

    public Visibility GlyphVisibility => _icon == null ? Visibility.Visible : Visibility.Collapsed;

    // ---- large icons / thumbnails (Icons, Thumbnails, Tiles views) -----------------------------

    private ImageSource? _largeIcon;
    private uint _largeIconSize;
    private uint _largeIconLoadedSize;

    public ImageSource? LargeIcon
    {
        get => _largeIcon;
        private set
        {
            if (SetProperty(ref _largeIcon, value))
                OnPropertyChanged(nameof(LargeGlyphVisibility));
        }
    }

    /// <summary>The big folder/file glyph shown until (or instead of) the thumbnail.</summary>
    public Visibility LargeGlyphVisibility => _largeIcon == null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Called when the item becomes visible in a large view; loads the thumbnail once per size.</summary>
    public void EnsureLargeIcon(Services.ThumbnailCache cache, uint size)
    {
        if (_largeIconSize >= size || Entry.IsUpDir || IsDrive)
            return;
        _largeIconSize = size;
        cache.Request(Entry, size, image =>
        {
            // requests of two sizes (Icons, then Thumbnails) may finish in either order: keep the larger image
            if (image != null && size > _largeIconLoadedSize)
            {
                _largeIconLoadedSize = size;
                LargeIcon = image;
            }
        });
    }

    /// <summary>Called when the row becomes visible; loads the shell icon once.</summary>
    public void EnsureIcon(Services.IconCache cache)
    {
        if (_iconRequested)
            return;
        _iconRequested = true;
        cache.Request(Entry, icon => Icon = icon);
    }

    public override string ToString() => Entry.Name;
}

/// <summary>Shared appearance state (density, brushes) used by item view models.</summary>
public static class UiState
{
    public static double RowHeight { get; set; } = 20;
    public static double FontSize { get; set; } = 12;

    public static Brush TextBrush { get; private set; } = new SolidColorBrush(Colors.Black);
    public static Brush SecondaryTextBrush { get; private set; } = new SolidColorBrush(Colors.DimGray);
    public static Brush MarkedBrush { get; private set; } = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xD1, 0x34, 0x38));
    public static Brush FolderBrush { get; private set; } = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE3, 0xA8, 0x2B));
    public static Brush CriticalBrush { get; private set; } = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE8, 0x48, 0x3E));
    public static Brush AccentBarBrush { get; private set; } = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x26, 0xA0, 0xDA));

    /// <summary>Recreates theme-dependent brushes. Call before (re)building item lists.</summary>
    public static void ApplyTheme(bool dark)
    {
        TextBrush = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0xE6, 0, 0, 0));
        SecondaryTextBrush = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0xA0, 0, 0, 0));
        MarkedBrush = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(255, 0xFF, 0x6B, 0x6B) : Windows.UI.Color.FromArgb(255, 0xC4, 0x2B, 0x1C));
        FolderBrush = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(255, 0xF2, 0xC2, 0x4E) : Windows.UI.Color.FromArgb(255, 0xE3, 0xA8, 0x2B));
    }

    /// <summary>The density last applied (the settings window re-applies only when it changed).</summary>
    public static Core.Settings.Density Density { get; private set; }

    public static void ApplyDensity(Core.Settings.Density density)
    {
        Density = density;
        RowHeight = density == Core.Settings.Density.Compact ? 20 : 28;
        FontSize = density == Core.Settings.Density.Compact ? 12 : 13;
    }
}
