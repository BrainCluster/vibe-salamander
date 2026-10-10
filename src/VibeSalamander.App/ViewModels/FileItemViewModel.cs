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

    /// <summary>
    /// Name column: without extension for files (the Ext column shows it), like Salamander. Directories keep
    /// their whole name unless Configuration → Panels gives them extensions. Shown in the configured case.
    /// </summary>
    public string Name
    {
        get
        {
            if (Entry.IsUpDir)
                return "..";
            string name = Entry.IsDirectory
                ? (UiState.DirectoriesHaveExtensions && DirectoryDot > 0 ? Entry.Name[..DirectoryDot] : Entry.Name)
                : Entry.BaseName;
            return Formats.ApplyCasing(name, Entry.IsDirectory, UiState.NameCasing);
        }
    }

    public string Ext
    {
        get
        {
            string ext = Entry.IsDirectory
                ? (UiState.DirectoriesHaveExtensions && DirectoryDot > 0 ? Entry.Name[(DirectoryDot + 1)..] : string.Empty)
                : Entry.Extension;
            return UiState.NameCasing switch
            {
                Core.Settings.NameCasing.Upper => ext.ToUpperInvariant(),
                Core.Settings.NameCasing.AsStored => ext,
                Core.Settings.NameCasing.LowerFilesUpperDirectories when Entry.IsDirectory => ext.ToUpperInvariant(),
                Core.Settings.NameCasing.MixedIfDos when Entry.Name != Entry.Name.ToUpperInvariant() || Entry.Name.Length > 12 => ext, // not a DOS name: as stored
                _ => ext.ToLowerInvariant(),
            };
        }
    }

    private int DirectoryDot => Entry.Name.LastIndexOf('.');

    public string SizeText =>
        Entry.IsUpDir ? "UP-DIR" :
        Entry.IsDirectory ? (_directorySize is long d ? Formats.Size(d, UiState.SizeFormat) : "DIR") :
        Formats.Size(Entry.Size, UiState.SizeFormat);

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
    public string FullName => Entry.IsUpDir ? ".." : Formats.ApplyCasing(Entry.Name, Entry.IsDirectory, UiState.NameCasing);

    /// <summary>Height of the preview area in the Thumbnails view (Configuration → Appearance).</summary>
    public GridLength ThumbnailRowHeight => new(UiState.ThumbnailSize);

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
    public double ItemOpacity => UiState.DimHiddenItems && (Entry.IsHidden || Entry.IsSystem) ? 0.55 : 1.0;

    private Brush? _highlight;
    private bool _highlightChecked;

    /// <summary>Marked items in the selection color; others in the color of the first matching highlighting rule.</summary>
    public Brush NameBrush
    {
        get
        {
            if (_isMarked)
                return UiState.MarkedBrush;
            if (!_highlightChecked)
            {
                _highlightChecked = true;
                _highlight = Entry.IsUpDir || IsDrive ? null : UiState.HighlightBrush(Entry);
            }
            return _highlight ?? UiState.TextBrush;
        }
    }
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

    /// <summary>Panel font (Configuration → Appearance); null = the system font.</summary>
    public static FontFamily? PanelFont { get; private set; }

    public static Brush TextBrush { get; private set; } = new SolidColorBrush(Colors.Black);
    public static Brush SecondaryTextBrush { get; private set; } = new SolidColorBrush(Colors.DimGray);
    public static Brush MarkedBrush { get; private set; } = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xD1, 0x34, 0x38));
    public static Brush FolderBrush { get; private set; } = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE3, 0xA8, 0x2B));
    public static Brush CriticalBrush { get; private set; } = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE8, 0x48, 0x3E));
    public static Brush AccentBarBrush { get; private set; } = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x26, 0xA0, 0xDA));

    /// <summary>Panel background of the color scheme; null = the theme's.</summary>
    public static Brush? PanelBackground { get; private set; }

    /// <summary>Background of the focused item (caret) of the color scheme; null = the theme's.</summary>
    public static Brush? FocusBackground { get; private set; }

    // ---- display options (Configuration → Panels / Appearance / Colors) -------------------------

    private static Core.Settings.AppSettings? _settings;
    private static bool _dark;
    private static readonly Dictionary<string, Brush> RuleBrushes = new(StringComparer.OrdinalIgnoreCase);

    public static Core.Settings.NameCasing NameCasing => _settings?.Panels.NameCasing ?? Core.Settings.NameCasing.AsStored;
    public static Core.Settings.SizeFormat SizeFormat => _settings?.Panels.SizeFormat ?? Core.Settings.SizeFormat.Bytes;
    public static bool DirectoriesHaveExtensions => _settings?.Panels.DirectoriesHaveExtensions ?? false;
    public static bool DimHiddenItems => _settings?.Appearance.DimHiddenItems ?? true;
    public static int ThumbnailSize => Math.Clamp(_settings?.Appearance.ThumbnailSize ?? 112, 48, 256);
    public static string InfoLineFormat => _settings?.Appearance.InfoLineFormat is { Length: > 0 } f ? f : Core.Settings.AppearanceOptions.DefaultInfoLine;
    public static bool SelectIncludesDirectories => _settings?.Panels.SelectIncludesDirectories ?? false;
    public static Core.Settings.DriveOptions? Drives => _settings?.Drives;

    /// <summary>Sorting options of Configuration → Panels.</summary>
    public static Core.Panels.SortOptions SortOptions => _settings is { } s
        ? new Core.Panels.SortOptions(s.Panels.SortRegional, s.Panels.SortNumerical, s.Panels.SortNewestFirst,
                                      s.Panels.SortDirectoriesByNameOnTimeSort, s.Panels.DirectoriesHaveExtensions)
        : Core.Panels.SortOptions.Default;

    /// <summary>Takes the display options from the settings. Call ApplyTheme / ApplyDensity afterwards.</summary>
    public static void ApplyOptions(Core.Settings.AppSettings settings)
    {
        _settings = settings;
        string family = settings.Appearance.FontFamily?.Trim() ?? string.Empty;
        PanelFont = family.Length > 0 ? new FontFamily(family) : null;
        ApplyDensity(Density);
        ApplyTheme(_dark);
    }

    /// <summary>"#RRGGBB" / "#AARRGGBB" → color; null when empty or invalid.</summary>
    public static Windows.UI.Color? ParseColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        string hex = text.Trim().TrimStart('#');
        if (hex.Length == 6)
            hex = "FF" + hex;
        if (hex.Length != 8 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint v))
            return null;
        return Windows.UI.Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static string ToHex(Windows.UI.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Windows.UI.Color Rgb(uint rgb) => Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    /// <summary>Color scheme names (Configuration → Colors).</summary>
    public static readonly (string Id, string Name)[] Schemes =
    {
        ("Vibe", "Vibe Salamander (follows light / dark)"),
        ("Salamander", "Open Salamander"),
        ("Explorer", "Windows Explorer"),
        ("Norton", "Norton Commander"),
        ("Navigator", "DOS Navigator"),
        ("Custom", "Custom"),
    };

    /// <summary>Recreates theme- and scheme-dependent brushes. Call before (re)building item lists.</summary>
    public static void ApplyTheme(bool dark)
    {
        _dark = dark;
        Windows.UI.Color text = dark ? Windows.UI.Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0xE6, 0, 0, 0);
        Windows.UI.Color secondary = dark ? Windows.UI.Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0xA0, 0, 0, 0);
        Windows.UI.Color marked = dark ? Rgb(0xFF6B6B) : Rgb(0xC42B1C);
        Windows.UI.Color folder = dark ? Rgb(0xF2C24E) : Rgb(0xE3A82B);
        Windows.UI.Color? background = null, focus = null;

        Core.Settings.ColorOptions? colors = _settings?.Colors;
        switch (colors?.Scheme)
        {
            case "Salamander":
                secondary = text;
                marked = dark ? Rgb(0xFF5050) : Rgb(0xFF0000);
                break;
            case "Explorer":
                marked = dark ? Rgb(0x4CA3FF) : Rgb(0x0066CC);
                break;
            case "Norton":
                background = Rgb(0x000080);
                text = Rgb(0x00FFFF);
                secondary = Rgb(0x00C8C8);
                marked = Rgb(0xFFFF00);
                folder = Rgb(0xFFFFFF);
                focus = Rgb(0x008080);
                break;
            case "Navigator":
                background = Rgb(0x0000A8);
                text = Rgb(0xFFFFFF);
                secondary = Rgb(0xA8A8A8);
                marked = Rgb(0xFFFF55);
                folder = Rgb(0x55FFFF);
                focus = Rgb(0x00A8A8);
                break;
            case "Custom":
                text = ParseColor(colors.Text) ?? text;
                secondary = ParseColor(colors.SecondaryText) ?? secondary;
                marked = ParseColor(colors.Selected) ?? marked;
                folder = ParseColor(colors.Folder) ?? folder;
                background = ParseColor(colors.PanelBackground);
                focus = ParseColor(colors.FocusBackground);
                break;
        }
        TextBrush = new SolidColorBrush(text);
        SecondaryTextBrush = new SolidColorBrush(secondary);
        MarkedBrush = new SolidColorBrush(marked);
        FolderBrush = new SolidColorBrush(folder);
        PanelBackground = background is Windows.UI.Color b ? new SolidColorBrush(b) : null;
        FocusBackground = focus is Windows.UI.Color f ? new SolidColorBrush(f) : null;
        RuleBrushes.Clear();
    }

    /// <summary>Name color from the highlighting rules (first match), or null.</summary>
    public static Brush? HighlightBrush(FileEntry entry)
    {
        List<Core.Settings.HighlightRule>? rules = _settings?.Colors.Rules;
        if (rules == null)
            return null;
        foreach (Core.Settings.HighlightRule rule in rules)
        {
            if (!rule.Matches(entry.Name, entry.Attributes, entry.IsDirectory))
                continue;
            if (ParseColor(rule.Color) is not Windows.UI.Color color)
                return null;
            if (!RuleBrushes.TryGetValue(rule.Color, out Brush? brush))
                RuleBrushes[rule.Color] = brush = new SolidColorBrush(color);
            return brush;
        }
        return null;
    }

    /// <summary>The density last applied (the settings window re-applies only when it changed).</summary>
    public static Core.Settings.Density Density { get; private set; }

    public static void ApplyDensity(Core.Settings.Density density)
    {
        Density = density;
        double baseFont = density == Core.Settings.Density.Compact ? 12 : 13;
        double customFont = _settings?.Appearance.FontSize ?? 0;
        FontSize = customFont >= 8 ? Math.Min(customFont, 28) : baseFont;
        double baseRow = density == Core.Settings.Density.Compact ? 20 : 28;
        // bigger fonts need taller rows
        RowHeight = Math.Max(baseRow, Math.Ceiling(FontSize * 1.6));
    }
}
