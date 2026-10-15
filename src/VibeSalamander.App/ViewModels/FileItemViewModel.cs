// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using VibeSalamander.Core;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Formatting;
using VibeSalamander.Services;

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
    public string TileType => Entry.IsUpDir ? Loc.T("Parent directory") :
        Entry.IsDirectory ? Loc.T("File folder") :
        Entry.Extension.Length > 0 ? Loc.F("{0} file", Entry.Extension.ToUpperInvariant()) : Loc.T("File");

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
            if (_isCaret && UiState.FocusTextBrush is { } focusText)
                return focusText;
            if (!_highlightChecked)
            {
                _highlightChecked = true;
                _highlight = Entry.IsUpDir || IsDrive ? null : UiState.HighlightBrush(Entry);
            }
            return _highlight ?? UiState.TextBrush;
        }
    }
    public Brush TextBrush => _isMarked ? UiState.MarkedBrush
        : _isCaret && UiState.FocusTextBrush is { } focusText ? focusText
        : UiState.SecondaryTextBrush;

    private bool _isCaret;

    /// <summary>The focused row (cursor bar): schemes with a focus text color draw it in that color.</summary>
    public bool IsCaret
    {
        get => _isCaret;
        set
        {
            if (_isCaret == value)
                return;
            _isCaret = value;
            if (UiState.FocusTextBrush != null || !value)
            {
                OnPropertyChanged(nameof(NameBrush));
                OnPropertyChanged(nameof(TextBrush));
                OnPropertyChanged(nameof(GlyphBrush));
            }
        }
    }

    public string Glyph =>
        Entry.IsUpDir ? "" :          // Up
        IsDrive ? "" :                // HardDrive
        Entry.IsDirectory ? "" :      // Folder
        "";                           // Page

    public Brush GlyphBrush => _isCaret && UiState.FocusTextBrush is { } focusText ? focusText
        : Entry.IsDirectory && !Entry.IsUpDir && !IsDrive ? UiState.FolderBrush : UiState.SecondaryTextBrush;

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

    /// <summary>The row scrolled out of view: its thumbnail is let go (the cache keeps the recent ones).</summary>
    public void ReleaseLargeIcon()
    {
        if (_largeIconSize == 0)
            return;
        _largeIconSize = 0;
        _largeIconLoadedSize = 0;
        LargeIcon = null;
    }

    /// <summary>Called when the item becomes visible in a large view; loads the thumbnail once per size.</summary>
    public void EnsureLargeIcon(Services.ThumbnailCache cache, uint size)
    {
        if (_largeIconSize >= size || Entry.IsUpDir || IsDrive)
            return;
        _largeIconSize = size;
        cache.Request(Entry, size, image =>
        {
            // requests of two sizes (Icons, then Thumbnails) may finish in either order: keep the larger image
            // _largeIconSize < size: the row scrolled away (or was released) before this arrived
            if (image != null && size > _largeIconLoadedSize && _largeIconSize >= size)
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
    public static double FontSize { get; set; } = 13;

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

    /// <summary>
    /// A color scheme. Null colors follow the app theme. Type colors: executables, archives, images,
    /// audio / video, documents, source code (as file managers in the terminal color them).
    /// </summary>
    public sealed record ColorScheme(
        string Id, string Name, bool? Dark,
        uint? Background = null, uint? Text = null, uint? Secondary = null, uint? Marked = null,
        uint? FolderIcon = null, uint? DirectoryText = null, uint? Focus = null, uint? FocusText = null,
        uint? Exe = null, uint? Archive = null, uint? Image = null, uint? Media = null, uint? Document = null, uint? Code = null);

    /// <summary>Color schemes (Configuration → Colors). Dark = the panels use the dark theme; null = follow the app.</summary>
    public static readonly ColorScheme[] SchemeList =
    {
        new("Vibe", "Vibe Salamander (follows light / dark)", null),
        new("Explorer", "Windows Explorer", null),
        new("Yazi", "Yazi", true,
            Background: 0x16161E, Text: 0xD5D5D5, Secondary: 0x7A7F8C, Marked: 0xFFD866,
            FolderIcon: 0x61AFEF, DirectoryText: 0x61AFEF, Focus: 0x61AFEF, FocusText: 0x16161E,
            Exe: 0x98C379, Archive: 0xE06C75, Image: 0xE5C07B, Media: 0xC678DD, Document: 0x56B6C2, Code: 0xD19A66),
        new("Superfile", "Superfile (Catppuccin Mocha)", true,
            Background: 0x1E1E2E, Text: 0xCDD6F4, Secondary: 0x7F849C, Marked: 0xFAB387,
            FolderIcon: 0x89B4FA, DirectoryText: 0x89B4FA, Focus: 0x45475A, FocusText: 0xF5E0DC,
            Exe: 0xA6E3A1, Archive: 0xF38BA8, Image: 0xF9E2AF, Media: 0xCBA6F7, Document: 0x94E2D5, Code: 0x89DCEB),
        new("TokyoNight", "Tokyo Night", true,
            Background: 0x1A1B26, Text: 0xC0CAF5, Secondary: 0x565F89, Marked: 0xE0AF68,
            FolderIcon: 0x7AA2F7, DirectoryText: 0x7AA2F7, Focus: 0x283457, FocusText: 0xFFFFFF,
            Exe: 0x9ECE6A, Archive: 0xF7768E, Image: 0xBB9AF7, Media: 0x7DCFFF, Document: 0xFF9E64, Code: 0x73DACA),
        new("Gruvbox", "Gruvbox (ranger / lf)", true,
            Background: 0x282828, Text: 0xEBDBB2, Secondary: 0x928374, Marked: 0xFABD2F,
            FolderIcon: 0x83A598, DirectoryText: 0x83A598, Focus: 0x504945, FocusText: 0xFBF1C7,
            Exe: 0xB8BB26, Archive: 0xFB4934, Image: 0xD3869B, Media: 0x8EC07C, Document: 0xFE8019, Code: 0x8EC07C),
        new("Dracula", "Dracula", true,
            Background: 0x282A36, Text: 0xF8F8F2, Secondary: 0x6272A4, Marked: 0xF1FA8C,
            FolderIcon: 0xBD93F9, DirectoryText: 0xBD93F9, Focus: 0x44475A, FocusText: 0xFFFFFF,
            Exe: 0x50FA7B, Archive: 0xFF5555, Image: 0xFF79C6, Media: 0x8BE9FD, Document: 0xFFB86C, Code: 0x8BE9FD),
        new("Nord", "Nord", true,
            Background: 0x2E3440, Text: 0xD8DEE9, Secondary: 0x7B88A1, Marked: 0xEBCB8B,
            FolderIcon: 0x88C0D0, DirectoryText: 0x88C0D0, Focus: 0x434C5E, FocusText: 0xECEFF4,
            Exe: 0xA3BE8C, Archive: 0xBF616A, Image: 0xB48EAD, Media: 0x8FBCBB, Document: 0xD08770, Code: 0x81A1C1),
        new("Matrix", "Phosphor (green terminal)", true,
            Background: 0x0A0F0A, Text: 0x33FF66, Secondary: 0x1F9940, Marked: 0xEEFF41,
            FolderIcon: 0x99FFAA, DirectoryText: 0x99FFAA, Focus: 0x33FF66, FocusText: 0x0A0F0A),
        // CGA palette: blue panels, light cyan names, white directories, black on cyan cursor bar
        new("Norton", "Norton Commander", true,
            Background: 0x0000AA, Text: 0x55FFFF, Secondary: 0x55FFFF, Marked: 0xFFFF55,
            FolderIcon: 0xFFFFFF, DirectoryText: 0xFFFFFF, Focus: 0x00AAAA, FocusText: 0x000000),
        // DOS Navigator: like NC, plus its file type highlighting (executables, archives)
        new("Navigator", "DOS Navigator", true,
            Background: 0x0000AA, Text: 0x55FFFF, Secondary: 0xAAAAAA, Marked: 0xFFFF55,
            FolderIcon: 0xFFFFFF, DirectoryText: 0xFFFFFF, Focus: 0x00AAAA, FocusText: 0x000000,
            Exe: 0x55FF55, Archive: 0xFF55FF),
        new("Custom", "Custom", null),
    };

    /// <summary>(Id, Name) of the schemes for the settings page.</summary>
    public static (string Id, string Name)[] Schemes => SchemeList.Select(x => (x.Id, x.Name)).ToArray();

    public static ColorScheme CurrentScheme =>
        SchemeList.FirstOrDefault(x => x.Id == _settings?.Colors?.Scheme) ?? SchemeList[0];

    /// <summary>Theme of the panels: dark schemes make the panel chrome (address bar, header) dark too.</summary>
    public static ElementTheme PanelTheme => CurrentScheme.Dark switch
    {
        true => ElementTheme.Dark,
        false => ElementTheme.Light,
        _ => ElementTheme.Default,
    };

    /// <summary>Text of the focused row (cursor bar); null = the normal colors.</summary>
    public static Brush? FocusTextBrush { get; private set; }

    /// <summary>Directory names; null = TextBrush.</summary>
    public static Brush? DirectoryTextBrush { get; private set; }

    private static readonly Dictionary<string, int> TypeOfExtension = BuildTypes();
    private static Brush?[] _typeBrushes = new Brush?[6];

    private static Dictionary<string, int> BuildTypes()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string[] groups =
        {
            "exe com bat cmd ps1 msi msix appx sh vbs scr",
            "zip 7z rar tar gz tgz bz2 xz zst cab iso jar lz lzh arj",
            "png jpg jpeg gif bmp webp tif tiff ico svg heic avif psd raw",
            "mp3 flac wav ogg m4a aac wma opus mp4 mkv avi mov wmv webm m4v mpg mpeg",
            "pdf doc docx xls xlsx ppt pptx odt ods odp rtf txt md epub csv",
            "cs c cpp h hpp py js ts tsx jsx java go rs rb php html css scss json xml yaml yml toml xaml sln csproj sql lua kt swift",
        };
        for (int i = 0; i < groups.Length; i++)
            foreach (string ext in groups[i].Split(' '))
                map.TryAdd(ext, i);
        return map;
    }

    /// <summary>Recreates theme- and scheme-dependent brushes. Call before (re)building item lists.</summary>
    public static void ApplyTheme(bool dark)
    {
        _dark = dark;
        ColorScheme scheme = CurrentScheme;
        if (scheme.Dark is bool fixedDark)
            dark = fixedDark;
        Windows.UI.Color text = dark ? Windows.UI.Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0xE6, 0, 0, 0);
        Windows.UI.Color secondary = dark ? Windows.UI.Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0xA0, 0, 0, 0);
        Windows.UI.Color marked = dark ? Rgb(0xFF6B6B) : Rgb(0xC42B1C);
        Windows.UI.Color folder = dark ? Rgb(0xF2C24E) : Rgb(0xE3A82B);
        Windows.UI.Color? background = null, focus = null, focusText = null, directoryText = null;
        uint?[] types = { dark ? 0x6CCB8Bu : 0x2E9E5Bu, dark ? 0xC39BD3u : 0x9B59B6u, null, null, null, null };

        Core.Settings.ColorOptions? colors = _settings?.Colors;
        switch (scheme.Id)
        {
            case "Vibe":
                break;
            case "Explorer":
                marked = dark ? Rgb(0x4CA3FF) : Rgb(0x0066CC);
                types = new uint?[6];
                break;
            case "Custom":
                text = ParseColor(colors?.Text) ?? text;
                secondary = ParseColor(colors?.SecondaryText) ?? secondary;
                marked = ParseColor(colors?.Selected) ?? marked;
                folder = ParseColor(colors?.Folder) ?? folder;
                background = ParseColor(colors?.PanelBackground);
                focus = ParseColor(colors?.FocusBackground);
                directoryText = ParseColor(colors?.DirectoryText);
                focusText = ParseColor(colors?.FocusText);
                break;
            default:
                if (scheme.Text is uint t) text = Rgb(t);
                if (scheme.Secondary is uint s2) secondary = Rgb(s2);
                if (scheme.Marked is uint m) marked = Rgb(m);
                if (scheme.FolderIcon is uint f) folder = Rgb(f);
                if (scheme.Background is uint b) background = Rgb(b);
                if (scheme.Focus is uint fo) focus = Rgb(fo);
                if (scheme.FocusText is uint ft) focusText = Rgb(ft);
                if (scheme.DirectoryText is uint d) directoryText = Rgb(d);
                types = new[] { scheme.Exe, scheme.Archive, scheme.Image, scheme.Media, scheme.Document, scheme.Code };
                break;
        }
        TextBrush = new SolidColorBrush(text);
        SecondaryTextBrush = new SolidColorBrush(secondary);
        MarkedBrush = new SolidColorBrush(marked);
        FolderBrush = new SolidColorBrush(folder);
        PanelBackground = background is Windows.UI.Color bg ? new SolidColorBrush(bg) : null;
        FocusBackground = focus is Windows.UI.Color fc ? new SolidColorBrush(fc) : null;
        FocusTextBrush = focusText is Windows.UI.Color ftc ? new SolidColorBrush(ftc) : null;
        DirectoryTextBrush = directoryText is Windows.UI.Color dt ? new SolidColorBrush(dt) : null;
        bool useTypes = colors?.FileTypeColors ?? true;
        _typeBrushes = types.Select(x => useTypes && x is uint c ? (Brush?)new SolidColorBrush(Rgb(c)) : null).ToArray();
        RuleBrushes.Clear();
    }

    /// <summary>Name color: the first matching highlighting rule, else the scheme's file type color, else null.</summary>
    public static Brush? HighlightBrush(FileEntry entry)
    {
        List<Core.Settings.HighlightRule>? rules = _settings?.Colors?.Rules;
        if (rules != null)
        {
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
        }
        if (entry.IsDirectory)
            return DirectoryTextBrush;
        string ext = entry.Extension.TrimStart('.');
        return ext.Length > 0 && TypeOfExtension.TryGetValue(ext, out int type) ? _typeBrushes[type] : null;
    }

    /// <summary>The density last applied (the settings window re-applies only when it changed).</summary>
    public static Core.Settings.Density Density { get; private set; }

    public static void ApplyDensity(Core.Settings.Density density)
    {
        Density = density;
        double baseFont = density == Core.Settings.Density.Compact ? 13 : 14;
        double customFont = _settings?.Appearance.FontSize ?? 0;
        FontSize = customFont >= 8 ? Math.Min(customFont, 28) : baseFont;
        double baseRow = density == Core.Settings.Density.Compact ? 20 : 28;
        // bigger fonts need taller rows
        RowHeight = Math.Max(baseRow, Math.Ceiling(FontSize * 1.6));
    }
}
