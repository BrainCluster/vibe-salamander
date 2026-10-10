using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Settings;
using VibeSalamander.ViewModels;

namespace VibeSalamander.Views;

/// <summary>The configuration pages that follow Open Salamander's Configuration dialog.</summary>
public sealed partial class SettingsWindow
{
    // ---- General -----------------------------------------------------------------------------

    private UIElement BuildGeneralPage()
    {
        AppSettings s = _vm.Settings;
        GeneralOptions g = s.General;
        StackPanel page = Page("General");

        page.Children.Add(Toggle("Save configuration on exit",
            "Off: changes are kept only when you choose Options → Save Configuration (or change them here).",
            g.SaveOnExit, v => g.SaveOnExit = v));
        page.Children.Add(Toggle("Clear read-only attribute when copying from CD / DVD",
            "Files on optical discs are always read-only; their copies usually shouldn't be.", g.ClearReadOnlyFromCd, v => g.ClearReadOnlyFromCd = v));
        page.Children.Add(Toggle("Allow only one instance of Vibe Salamander",
            "Starting it again brings the running window to the front. Takes effect at the next start.", g.SingleInstance, v => g.SingleInstance = v));
        page.Children.Add(Toggle("Beep when work finishes in the background",
            "When the last transfer ends while the window isn't active.", g.BeepWhenDoneInBackground, v => g.BeepWhenDoneInBackground = v));
        page.Children.Add(Toggle("Close the console window after a command line command",
            "Off: the window stays open so you can read the output.", g.CloseShellAfterCommand, v => g.CloseShellAfterCommand = v));
        page.Children.Add(Toggle("Always on top", null, s.AlwaysOnTop, v => s.AlwaysOnTop = v));

        page.Children.Add(Section("Compare Directories"));
        page.Children.Add(Number("Ignore time differences up to (seconds)",
            "FAT drives store times in 2-second steps; copies can differ a little.", g.CompareTimeTolerance, 0, 3600, v => g.CompareTimeTolerance = v));
        page.Children.Add(Toggle("Ignore time differences of exactly one or two hours",
            "Daylight saving time, or FAT and NTFS drives in different time zones.", g.CompareIgnoreDstHours, v => g.CompareIgnoreDstHours = v));

        page.Children.Add(Section("Rename"));
        page.Children.Add(Toggle("Select the extension too in Quick Rename (F2)", null, g.RenameSelectsExtension, v => g.RenameSelectsExtension = v));
        page.Children.Add(Toggle("Select the extension too in Edit New File (Shift+F4)", null, g.EditNewSelectsExtension, v => g.EditNewSelectsExtension = v));

        page.Children.Add(Section("Transfers"));
        page.Children.Add(Number("Transfers at the same time",
            "Copy, move and delete operations in the transfer panel; the others wait as Queued.", g.ParallelTransfers, 1, 8, v => g.ParallelTransfers = v));
        return page;
    }

    // ---- Recycle Bin ---------------------------------------------------------------------------

    private UIElement BuildRecycleBinPage()
    {
        AppSettings s = _vm.Settings;
        RecycleOptions r = s.Recycle;
        StackPanel page = Page("Recycle Bin",
            "Choose whether deleted files and directories go to the Recycle Bin or are deleted permanently. " +
            "Shift+Del always deletes permanently.");

        var modes = new RadioButtons();
        modes.Items.Add("Move all deleted files and directories to the Recycle Bin");
        modes.Items.Add("Remove files and directories immediately (permanently) when deleted");
        modes.Items.Add("Move the following files to the Recycle Bin, delete the others permanently:");
        modes.SelectedIndex = (int)r.Mode;
        var masks = new TextBox { Text = r.Masks, Width = 520, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(28, 0, 0, 0), IsEnabled = r.Mode == DeleteMode.RecycleMatching };
        modes.SelectionChanged += (_, _) =>
        {
            if (modes.SelectedIndex < 0)
                return;
            r.Mode = (DeleteMode)modes.SelectedIndex;
            masks.IsEnabled = r.Mode == DeleteMode.RecycleMatching;
            Changed();
        };
        masks.TextChanged += (_, _) =>
        {
            r.Masks = masks.Text;
            Changed();
        };
        var card = new StackPanel { Spacing = 6 };
        card.Children.Add(modes);
        card.Children.Add(masks);
        card.Children.Add(Hint("Masks: '*' any characters, '?' one character, ';' separates masks, '|' starts excluded masks. Directories always go to the Recycle Bin in this mode."));
        page.Children.Add(new Border { Child = card, Style = ThemedStyle("SettingsCardStyle") });

        page.Children.Add(Section("Confirmation"));
        page.Children.Add(Toggle("Confirm moving to the Recycle Bin", "Deleting to the Recycle Bin can be undone, so by default it doesn't ask (like Explorer).",
            s.ConfirmRecycle, v => s.ConfirmRecycle = v));
        page.Children.Add(Hint("Permanent deletes are confirmed according to the Confirmations page."));
        return page;
    }

    // ---- Confirmations -------------------------------------------------------------------------

    private UIElement BuildConfirmationsPage()
    {
        AppSettings s = _vm.Settings;
        ConfirmationOptions c = s.Confirm;
        StackPanel page = Page("Confirmations", "Choose which operations ask before they run, and which messages are shown.");

        page.Children.Add(Section("Confirm on"));
        page.Children.Add(Toggle("File or directory delete (permanent)", "Ignored for items that go to the Recycle Bin (see the Recycle Bin page).", c.PermanentDelete, v => c.PermanentDelete = v));
        page.Children.Add(Toggle("Non-empty directory delete", "Ignored for items that go to the Recycle Bin.", c.NonEmptyDirectoryDelete, v => c.NonEmptyDirectoryDelete = v));
        page.Children.Add(Toggle("File overwrite", "Off: copying and moving replace existing files without asking.", c.FileOverwrite, v => c.FileOverwrite = v));
        page.Children.Add(Toggle("System or hidden file delete", null, c.HiddenSystemFileDelete, v => c.HiddenSystemFileDelete = v));
        page.Children.Add(Toggle("System or hidden directory delete", null, c.HiddenSystemDirectoryDelete, v => c.HiddenSystemDirectoryDelete = v));
        page.Children.Add(Toggle("System or hidden file overwrite", "Asks even when file overwrites don't.", c.HiddenSystemFileOverwrite, v => c.HiddenSystemFileOverwrite = v));
        page.Children.Add(Toggle("NTFS compress or uncompress", null, c.NtfsCompress, v => c.NtfsCompress = v));
        page.Children.Add(Toggle("NTFS encrypt or decrypt", null, c.NtfsEncrypt, v => c.NtfsEncrypt = v));
        page.Children.Add(Toggle("Drag and drop operations", null, c.DragAndDrop, v => c.DragAndDrop = v));
        page.Children.Add(Toggle("Directories merge", "Copying a directory into one with the same name. Off: the content is merged without asking.", c.DirectoryMerge, v => c.DirectoryMerge = v));
        page.Children.Add(Toggle("Copy / move target (F5 / F6)", "Off: files go straight into the other panel's directory.", s.AskCopyMoveTarget, v => s.AskCopyMoveTarget = v));

        page.Children.Add(Section("Show message"));
        page.Children.Add(Toggle("Do you want to close Find?", "When the Find window has results.", c.CloseFind, v => c.CloseFind = v));
        page.Children.Add(Toggle("Do you want to stop searching?", "In Find Files and Directories.", c.StopFind, v => c.StopFind = v));
        page.Children.Add(Toggle("Do you want to create the target path?", "In Copy / Move, when the target directory doesn't exist.", c.CreateTargetPath, v => c.CreateTargetPath = v));
        page.Children.Add(Toggle("Do you want to turn on Always on Top?", null, c.AlwaysOnTop, v => c.AlwaysOnTop = v));
        page.Children.Add(Toggle("Do you want to close Vibe Salamander?", null, c.CloseApplication, v => c.CloseApplication = v));
        page.Children.Add(Toggle("Transfers are still running: cancel them and exit?", "Off: closing cancels running transfers without asking.", c.ExitWithRunningTransfers, v => c.ExitWithRunningTransfers = v));
        page.Children.Add(Toggle("Do you want to email the selected files?", null, c.Email, v => c.Email = v));
        page.Children.Add(Toggle("The directory doesn't exist. Do you want to create it?", "When you type a path in the address bar (or Shift+F7).", c.CreateMissingDirectory, v => c.CreateMissingDirectory = v));
        return page;
    }

    // ---- Panels --------------------------------------------------------------------------------

    private UIElement BuildPanelOptionsPage()
    {
        AppSettings s = _vm.Settings;
        PanelOptions p = s.Panels;
        StackPanel page = Page("Panels");

        page.Children.Add(Toggle("Hide files and directories with the hidden or system attribute", "Ctrl+H", !s.ShowHidden, v =>
        {
            s.ShowHidden = !v;
            _ = Task.WhenAll(_vm.Left.RefreshAsync(), _vm.Right.RefreshAsync());
        }));
        page.Children.Add(Toggle("Include directories in select / unselect operations", "Num +, Num -, Num * (invert) and Select All.",
            p.SelectIncludesDirectories, v => p.SelectIncludesDirectories = v, display: true));
        page.Children.Add(Choice("Show names", null, new[]
        {
            ("As they are stored on the disk", NameCasing.AsStored),
            ("In partially mixed case if they are in DOS format", NameCasing.MixedIfDos),
            ("In lower case; directories in upper case", NameCasing.LowerFilesUpperDirectories),
            ("In partially mixed case", NameCasing.PartiallyMixed),
            ("In lower case", NameCasing.Lower),
            ("In upper case", NameCasing.Upper),
        }, p.NameCasing, v => p.NameCasing = v, display: true));
        page.Children.Add(Choice("Show sizes", null, new[]
        {
            ("In bytes (Salamander default)", SizeFormat.Bytes),
            ("In kilobytes (Windows Explorer default)", SizeFormat.KiloBytes),
            ("In short format (15 B, 3.41 KB, 12.5 MB)", SizeFormat.Short),
        }, p.SizeFormat, v => p.SizeFormat = v, display: true));

        page.Children.Add(Section("Keyboard"));
        page.Children.Add(Toggle("Redirect typing from the panel to the command line",
            "Hold Alt for quick search.", p.TypingToCommandLine, v => p.TypingToCommandLine = v));
        page.Children.Add(Toggle("Use Shift+number to go to a hot path",
            "In addition to Ctrl+number. It conflicts with keyboard layouts that type characters with Shift+number.", p.ShiftDigitGoesToHotPath, v => p.ShiftDigitGoesToHotPath = v));

        page.Children.Add(Section("Mouse"));
        page.Children.Add(Toggle("Right-click opens the context menu",
            "Off: right-click selects / unselects (hold Ctrl for the context menu).", p.RightClickOpensMenu, v => p.RightClickOpensMenu = v));
        page.Children.Add(Toggle("Rename by slowly clicking the name twice", null, p.SlowClickRename, v => p.SlowClickRename = v));
        page.Children.Add(Number("Cancel drag-and-drop operations shorter than (ms)", "Protects against accidental drags.",
            p.DragCancelMilliseconds, 0, 2000, v => p.DragCancelMilliseconds = v));

        page.Children.Add(Section("Sorting"));
        page.Children.Add(Toggle("Sort strings with respect to the regional settings", null, p.SortRegional, v => p.SortRegional = v, display: true));
        page.Children.Add(Toggle("Use numerical sort for numbers contained in names", "\"file2\" before \"file10\".", p.SortNumerical, v => p.SortNumerical = v, display: true));
        page.Children.Add(Toggle("Sort by time: newer items on top", null, p.SortNewestFirst, v => p.SortNewestFirst = v, display: true));
        page.Children.Add(Toggle("Sort by time: sort directories by name", null, p.SortDirectoriesByNameOnTimeSort, v => p.SortDirectoriesByNameOnTimeSort = v, display: true));
        page.Children.Add(Toggle("Treat directories as if they have extensions", "Also shows their extension in the Ext column.",
            p.DirectoriesHaveExtensions, v => p.DirectoriesHaveExtensions = v, display: true));
        return page;
    }

    // ---- Appearance ----------------------------------------------------------------------------

    private static readonly string[] FontChoices =
    {
        string.Empty, "Segoe UI", "Segoe UI Variable Text", "Cascadia Mono", "Cascadia Code", "Consolas", "Lucida Console",
        "Courier New", "Calibri", "Arial", "Tahoma", "Verdana",
    };

    private static string[]? _installedFonts;

    /// <summary>Font families from the registered font files (machine and user), e.g. "Consolas", "JetBrains Mono".</summary>
    private static string[] InstalledFontFamilies()
    {
        if (_installedFonts != null)
            return _installedFonts;
        var families = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] styles =
        {
            "Bold", "Italic", "Oblique", "Light", "ExtraLight", "Extra Light", "UltraLight", "Semilight", "SemiLight",
            "Semibold", "SemiBold", "Demibold", "DemiBold", "ExtraBold", "Extra Bold", "UltraBold", "Black", "Heavy",
            "Medium", "Thin", "Regular", "Normal", "Condensed", "SemiCondensed", "Narrow", "Book", "Retina",
        };
        foreach (Microsoft.Win32.RegistryKey root in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        {
            try
            {
                using Microsoft.Win32.RegistryKey? key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
                if (key == null)
                    continue;
                foreach (string entry in key.GetValueNames())
                {
                    // "Cascadia Mono Bold (TrueType)", "Cambria & Cambria Math (TrueType)"
                    int paren = entry.IndexOf(" (", StringComparison.Ordinal);
                    string names = paren > 0 ? entry[..paren] : entry;
                    foreach (string part in names.Split(" & ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        string name = part;
                        bool trimmed = true;
                        while (trimmed)
                        {
                            trimmed = false;
                            foreach (string style in styles)
                            {
                                if (name.Length > style.Length + 1 && name.EndsWith(" " + style, StringComparison.OrdinalIgnoreCase))
                                {
                                    name = name[..^(style.Length + 1)].TrimEnd();
                                    trimmed = true;
                                }
                            }
                        }
                        if (name.Length > 1 && !name.Contains('.'))
                            families.Add(name);
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // fonts we can't list can still be typed
            }
        }
        return _installedFonts = families.ToArray();
    }

    private UIElement BuildAppearancePage()
    {
        AppSettings s = _vm.Settings;
        AppearanceOptions a = s.Appearance;
        StackPanel page = Page("Appearance");

        page.Children.Add(Choice("Theme", "Dark mode", new[] { ("Use system setting", AppTheme.System), ("Light", AppTheme.Light), ("Dark", AppTheme.Dark) },
            s.Theme, v => s.Theme = v));
        page.Children.Add(Choice("Density", "Compact is Salamander-like; comfortable gives rows more room, like Files.",
            new[] { ("Compact", Density.Compact), ("Comfortable", Density.Comfortable) }, s.Density, v => s.Density = v));
        page.Children.Add(Toggle("Single-click to open an item (point to select)", null, a.SingleClickOpens, v => a.SingleClickOpens = v));
        page.Children.Add(Toggle("Dim hidden and system items", null, a.DimHiddenItems, v => a.DimHiddenItems = v, display: true));
        page.Children.Add(Toggle("Show a maximize / restore button in the address bar", null, a.MaximizeButtonInAddressBar,
            v => a.MaximizeButtonInAddressBar = v, display: true));

        page.Children.Add(Section("Panel font"));
        var font = new ComboBox { MinWidth = 220, IsEditable = true, Text = a.FontFamily };
        string[] fonts = FontChoices.Concat(InstalledFontFamilies().Except(FontChoices, StringComparer.OrdinalIgnoreCase)).ToArray();
        foreach (string f in fonts)
            font.Items.Add(f.Length == 0 ? "(system font)" : f);
        font.SelectedIndex = Array.FindIndex(fonts, f => string.Equals(f, a.FontFamily ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        if (font.SelectedIndex < 0)
            font.Text = a.FontFamily ?? string.Empty;
        void SetFont(string text)
        {
            string value = text == "(system font)" ? string.Empty : text.Trim();
            if (value == a.FontFamily)
                return;
            a.FontFamily = value;
            Changed(display: true);
        }
        font.SelectionChanged += (_, _) =>
        {
            if (font.SelectedItem is string chosen)
                SetFont(chosen);
        };
        font.TextSubmitted += (_, args) => SetFont(args.Text);
        page.Children.Add(Card("Font", "Any installed font; type its name.", font));
        page.Children.Add(Number("Font size", "0 = the default of the density (13 compact, 14 comfortable).", a.FontSize, 0, 28, v => a.FontSize = v, display: true));

        page.Children.Add(Section("Content of the information line"));
        var info = new TextBox { Text = a.InfoLineFormat, Width = 520, HorizontalAlignment = HorizontalAlignment.Left, IsSpellCheckEnabled = false };
        info.TextChanged += (_, _) =>
        {
            // also raised (asynchronously, after _loading) for the initial text: don't rebuild the panels for it
            if (info.Text == a.InfoLineFormat)
                return;
            a.InfoLineFormat = info.Text;
            Changed(display: true);
        };
        var resetInfo = IconButton("\uE777", "Default", "Default");
        resetInfo.Click += (_, _) => info.Text = AppearanceOptions.DefaultInfoLine;
        var infoRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        infoRow.Children.Add(info);
        infoRow.Children.Add(resetInfo);
        page.Children.Add(infoRow);
        page.Children.Add(Hint("Variables: $(Name), $(NamePart), $(Ext), $(Size), $(Date), $(Time), $(Attributes), $(Type), $(Path), $(FullName)."));

        page.Children.Add(Section("Thumbnails"));
        page.Children.Add(Number("Thumbnail size (pixels)", "The Thumbnails view (Alt+4).", a.ThumbnailSize, 48, 256, v => a.ThumbnailSize = v, display: true));
        return page;
    }

    // ---- Colors --------------------------------------------------------------------------------

    private UIElement BuildColorsPage()
    {
        AppSettings s = _vm.Settings;
        ColorOptions c = s.Colors;
        StackPanel page = Page("Colors", "A color scheme for the panels and additional rules for highlighting files and directories.");

        var schemes = UiState.Schemes.Select(x => (x.Name, x.Id)).ToArray();
        page.Children.Add(Choice("Scheme", "Terminal-style schemes (Yazi, Superfile, ...) make the panels dark and color files by type. Custom uses the colors below.",
            schemes, c.Scheme, v =>
        {
            c.Scheme = v;
            // after the change is applied: the preview and the color items (enabled for Custom) follow
            DispatcherQueue.TryEnqueue(() => Render(_currentPage));
        }, display: true));
        page.Children.Add(BuildSchemePreview());
        page.Children.Add(Toggle("File type colors", "Executables, archives, images, audio / video, documents and source code in the scheme's colors.",
            c.FileTypeColors, v =>
            {
                c.FileTypeColors = v;
                DispatcherQueue.TryEnqueue(() => Render(_currentPage));
            }, display: true));

        bool custom = c.Scheme == "Custom";
        bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
        Windows.UI.Color Default(byte r, byte g, byte b) => Windows.UI.Color.FromArgb(255, r, g, b);
        (string Label, string Value, Windows.UI.Color Fallback, Action<string> Set)[] items =
        {
            ("Items in panel", c.Text, dark ? Default(0xF0, 0xF0, 0xF0) : Default(0x1A, 0x1A, 0x1A), v => c.Text = v),
            ("Other columns (size, date, ...)", c.SecondaryText, dark ? Default(0xB0, 0xB0, 0xB0) : Default(0x60, 0x60, 0x60), v => c.SecondaryText = v),
            ("Selected items", c.Selected, dark ? Default(0xFF, 0x6B, 0x6B) : Default(0xC4, 0x2B, 0x1C), v => c.Selected = v),
            ("Folder icons", c.Folder, dark ? Default(0xF2, 0xC2, 0x4E) : Default(0xE3, 0xA8, 0x2B), v => c.Folder = v),
            ("Panel background", c.PanelBackground, dark ? Default(0x20, 0x20, 0x20) : Default(0xFF, 0xFF, 0xFF), v => c.PanelBackground = v),
            ("Focused item background", c.FocusBackground, dark ? Default(0x3A, 0x3A, 0x3A) : Default(0xCC, 0xE4, 0xF7), v => c.FocusBackground = v),
            ("Focused item text", c.FocusText, dark ? Default(0xF0, 0xF0, 0xF0) : Default(0x1A, 0x1A, 0x1A), v => c.FocusText = v),
            ("Directory names", c.DirectoryText, dark ? Default(0xF0, 0xF0, 0xF0) : Default(0x1A, 0x1A, 0x1A), v => c.DirectoryText = v),
        };
        foreach (var (label, value, fallback, set) in items)
        {
            FrameworkElement button = ColorButton(value, fallback, set);
            button.IsHitTestVisible = custom;
            Border card = Card(label, null, button);
            card.Opacity = custom ? 1 : 0.5;
            page.Children.Add(card);
        }

        page.Children.Add(Section("Additional rules for highlighting files and directories"));
        page.Children.Add(Hint("The first rule whose masks match and whose attribute conditions hold gives the name its color. " +
                               "Attribute boxes: checked = must have, unchecked = must not have, indeterminate = don't care."));
        page.Children.Add(BuildHighlightEditor(c));
        return page;
    }

    /// <summary>A few sample rows in the current scheme: directory, cursor bar, marked file, file types.</summary>
    private FrameworkElement BuildSchemePreview()
    {
        var rows = new StackPanel { Padding = new Thickness(8, 6, 8, 6) };
        static FileEntry Sample(string name, bool directory = false) =>
            new(name, @"C:\" + name, directory, 0, DateTime.Now, directory ? FileAttributes.Directory : FileAttributes.Archive);
        (FileEntry Entry, string Size, bool Caret, bool Marked)[] samples =
        {
            (Sample("Projects", true), "<DIR>", false, false),
            (Sample("Music", true), "<DIR>", true, false),
            (Sample("setup.exe"), "4 812 288", false, false),
            (Sample("backup.7z"), "91 554 120", false, true),
            (Sample("photo.jpg"), "2 304 551", false, false),
            (Sample("track01.flac"), "31 220 870", false, false),
            (Sample("report.pdf"), "880 112", false, false),
            (Sample("Program.cs"), "12 044", false, false),
            (Sample("readme"), "1 024", false, false),
        };
        foreach (var (entry, size, caret, marked) in samples)
        {
            Brush name = marked ? UiState.MarkedBrush
                : caret && UiState.FocusTextBrush is { } ft ? ft
                : UiState.HighlightBrush(entry) ?? UiState.TextBrush;
            Brush other = marked ? UiState.MarkedBrush
                : caret && UiState.FocusTextBrush is { } ft2 ? ft2 : UiState.SecondaryTextBrush;
            Brush glyph = caret && UiState.FocusTextBrush is { } ft3 ? ft3
                : entry.IsDirectory ? UiState.FolderBrush : UiState.SecondaryTextBrush;
            var row = new Grid { Height = 22, Padding = new Thickness(6, 0, 6, 0), CornerRadius = new CornerRadius(3) };
            if (caret)
                row.Background = UiState.FocusBackground ?? (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new FontIcon { Glyph = entry.IsDirectory ? "\uE8B7" : "\uE7C3", FontSize = 13, Foreground = glyph };
            var label = new TextBlock { Text = entry.Name, Foreground = name, VerticalAlignment = VerticalAlignment.Center, FontSize = UiState.FontSize };
            var detail = new TextBlock { Text = size, Foreground = other, VerticalAlignment = VerticalAlignment.Center, FontSize = UiState.FontSize };
            if (UiState.PanelFont is { } font)
            {
                label.FontFamily = font;
                detail.FontFamily = font;
            }
            Grid.SetColumn(label, 1);
            Grid.SetColumn(detail, 2);
            row.Children.Add(icon);
            row.Children.Add(label);
            row.Children.Add(detail);
            rows.Children.Add(row);
        }
        var frame = new Border
        {
            Child = rows,
            MaxWidth = 460,
            HorizontalAlignment = HorizontalAlignment.Left,
            Style = ThemedStyle("SettingsFrameStyle"),
            RequestedTheme = UiState.PanelTheme,
        };
        if (UiState.PanelBackground is { } background)
            frame.Background = background;
        else
            frame.Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"];
        return frame;
    }

    private FrameworkElement BuildHighlightEditor(ColorOptions c)
    {
        var list = new ListView { Height = 170, Width = 300, SelectionMode = ListViewSelectionMode.Single };
        var masks = new TextBox { Header = "Masks", Width = 360 };
        CheckBox Attr(string text) => new() { Content = text, IsThreeState = true, MinWidth = 0, Margin = new Thickness(0, 0, 12, 0) };
        CheckBox archive = Attr("Archive"), readOnly = Attr("Read-only"), hidden = Attr("Hidden"), system = Attr("System"),
                 compressed = Attr("Compressed"), encrypted = Attr("Encrypted"), directory = Attr("Directory");
        var attrs = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 120, MaximumRowsOrColumns = 3 };
        foreach (CheckBox box in new[] { archive, readOnly, hidden, system, compressed, encrypted, directory })
            attrs.Children.Add(box);
        var colorHost = new ContentControl();
        var fields = new StackPanel { Spacing = 8 };
        fields.Children.Add(masks);
        fields.Children.Add(attrs);
        var colorRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        colorRow.Children.Add(new TextBlock { Text = "Color", VerticalAlignment = VerticalAlignment.Center });
        colorRow.Children.Add(colorHost);
        fields.Children.Add(colorRow);

        bool loading = false;
        HighlightRule? Selected() => list.SelectedIndex >= 0 && list.SelectedIndex < c.Rules.Count ? c.Rules[list.SelectedIndex] : null;
        string Describe(HighlightRule r) => r.Masks + (r.Directory == true ? "  (directories)" : r.Directory == false ? "  (files)" : string.Empty);

        void Fill(int select)
        {
            loading = true;
            list.Items.Clear();
            foreach (HighlightRule rule in c.Rules)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(UiState.ParseColor(rule.Color) ?? Microsoft.UI.Colors.Gray) });
                row.Children.Add(new TextBlock { Text = Describe(rule), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 240 });
                list.Items.Add(new ListViewItem { Content = row });
            }
            list.SelectedIndex = Math.Min(select, c.Rules.Count - 1);
            loading = false;
            Load();
        }
        void Load()
        {
            loading = true;
            HighlightRule? rule = Selected();
            fields.Opacity = rule == null ? 0.5 : 1;
            fields.IsHitTestVisible = rule != null;
            masks.Text = rule?.Masks ?? string.Empty;
            archive.IsChecked = rule?.Archive;
            readOnly.IsChecked = rule?.ReadOnly;
            hidden.IsChecked = rule?.Hidden;
            system.IsChecked = rule?.System;
            compressed.IsChecked = rule?.Compressed;
            encrypted.IsChecked = rule?.Encrypted;
            directory.IsChecked = rule?.Directory;
            colorHost.Content = rule == null ? null : ColorButton(rule.Color, Microsoft.UI.Colors.SeaGreen, v =>
            {
                rule.Color = v.Length > 0 ? v : "#2E8B57";
                // update only the list's swatch: rebuilding the list (Fill) would replace this color button
                // and close its picker flyout on the first color change
                if (list.SelectedItem is ListViewItem { Content: StackPanel row } && row.Children[0] is Border swatch)
                    swatch.Background = new SolidColorBrush(UiState.ParseColor(rule.Color) ?? Microsoft.UI.Colors.Gray);
            });
            loading = false;
        }
        void Store()
        {
            if (loading || Selected() is not { } rule)
                return;
            rule.Masks = masks.Text;
            rule.Archive = archive.IsChecked;
            rule.ReadOnly = readOnly.IsChecked;
            rule.Hidden = hidden.IsChecked;
            rule.System = system.IsChecked;
            rule.Compressed = compressed.IsChecked;
            rule.Encrypted = encrypted.IsChecked;
            rule.Directory = directory.IsChecked;
            if (list.SelectedItem is ListViewItem { Content: StackPanel row } && row.Children[1] is TextBlock text)
                text.Text = Describe(rule);
            Changed(display: true);
        }
        list.SelectionChanged += (_, _) =>
        {
            if (!loading)
                Load();
        };
        masks.TextChanged += (_, _) =>
        {
            // also raised asynchronously after Load sets the text: nothing changed then
            if (Selected() is { } current && current.Masks == masks.Text)
                return;
            Store();
        };
        foreach (CheckBox box in new[] { archive, readOnly, hidden, system, compressed, encrypted, directory })
        {
            box.Checked += (_, _) => Store();
            box.Unchecked += (_, _) => Store();
            box.Indeterminate += (_, _) => Store();
        }

        var add = IconButton("\uE710", "Add a rule", "Add");
        var remove = IconButton("\uE74D", "Remove the rule");
        var up = IconButton("\uE70E", "Higher priority");
        var down = IconButton("\uE70D", "Lower priority");
        var reset = IconButton("\uE777", "Default rules");
        add.Click += (_, _) =>
        {
            int at = list.SelectedIndex >= 0 ? list.SelectedIndex + 1 : c.Rules.Count;
            c.Rules.Insert(at, new HighlightRule { Masks = "*.*", Color = "#1E90FF" });
            Fill(at);
            Changed(display: true);
        };
        remove.Click += (_, _) =>
        {
            int i = list.SelectedIndex;
            if (i < 0)
                return;
            c.Rules.RemoveAt(i);
            Fill(Math.Min(i, c.Rules.Count - 1));
            Changed(display: true);
        };
        void Move(int delta)
        {
            int i = list.SelectedIndex, j = i + delta;
            if (i < 0 || j < 0 || j >= c.Rules.Count)
                return;
            (c.Rules[i], c.Rules[j]) = (c.Rules[j], c.Rules[i]);
            Fill(j);
            Changed(display: true);
        }
        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(+1);
        reset.Click += (_, _) =>
        {
            c.Rules = HighlightRule.Defaults();
            Fill(0);
            Changed(display: true);
        };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (Button b in new[] { add, remove, up, down, reset })
            tools.Children.Add(b);

        var left = new StackPanel { Spacing = 6 };
        left.Children.Add(new Border { Child = list, Style = ThemedStyle("SettingsFrameStyle") });
        left.Children.Add(tools);
        var layout = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        layout.Children.Add(left);
        layout.Children.Add(fields);
        Fill(0);
        return new Border { Child = layout, Style = ThemedStyle("SettingsCardStyle") };
    }

    // ---- Main Window ---------------------------------------------------------------------------

    private UIElement BuildMainWindowPage()
    {
        AppSettings s = _vm.Settings;
        MainWindowOptions w = s.Window;
        StackPanel page = Page("Main Window");
        page.Children.Add(Choice("Show the current path in the window title as", null, new[]
        {
            ("Directory name only", TitleMode.DirectoryName),
            ("Shortened path", TitleMode.Shortened),
            ("Full path", TitleMode.FullPath),
        }, w.TitleMode, v => w.TitleMode = v));
        page.Children.Add(TextSetting("Custom text before the path in the window title", "For example the computer name, to tell windows apart.",
            w.TitlePrefix, v => w.TitlePrefix = v, 220));

        page.Children.Add(Section("Show"));
        page.Children.Add(Toggle("Top toolbar", null, s.ShowTopToolbar, v => s.ShowTopToolbar = v));
        page.Children.Add(Toggle("Hot path bar", "Buttons for the hot paths (Ctrl+0…9).", s.ShowHotPathBar, v => s.ShowHotPathBar = v));
        page.Children.Add(Toggle("User menu bar", "Buttons for the user menu commands (F9).", s.ShowUserMenuBar, v => s.ShowUserMenuBar = v));
        page.Children.Add(Toggle("Drive bars", null, s.ShowDriveBars, v => s.ShowDriveBars = v));
        page.Children.Add(Toggle("Information line", null, s.ShowInformationLine, v => s.ShowInformationLine = v));
        page.Children.Add(Toggle("Command line", null, s.ShowCommandLine, v => s.ShowCommandLine = v));
        page.Children.Add(Toggle("Bottom toolbar (F1–F12)", null, s.ShowFunctionKeyBar, v => s.ShowFunctionKeyBar = v));
        page.Children.Add(Toggle("Transfer panel", "Shown while it has transfers.", s.ShowTransfers, v => s.ShowTransfers = v));
        return page;
    }

    // ---- Drives --------------------------------------------------------------------------------

    private UIElement BuildDrivesPage()
    {
        AppSettings s = _vm.Settings;
        DriveOptions d = s.Drives;
        StackPanel page = Page("Drives", "Automatic refresh updates a panel when files change. Simple icons are faster on slow drives.");
        foreach (var (title, options) in new[]
                 {
                     ("Removable drives (USB flash drives, memory cards)", d.Removable),
                     ("Fixed drives (local hard disks)", d.Fixed),
                     ("CD and DVD drives", d.CdRom),
                     ("Network drives and shares", d.Network),
                 })
        {
            DriveTypeOptions o = options;
            page.Children.Add(Section(title));
            page.Children.Add(Toggle("Use automatic refresh", null, o.AutoRefresh, v => o.AutoRefresh = v, display: true));
            page.Children.Add(Toggle("Use simple icons", null, o.SimpleIcons, v => o.SimpleIcons = v, display: true));
        }
        page.Children.Add(Toggle("Do not refresh network drives when Vibe Salamander becomes active",
            "Manual refresh (Ctrl+R) can be necessary.", d.NoRefreshOnActivation, v => d.NoRefreshOnActivation = v));

        page.Children.Add(Section("Inaccessible path"));
        var fallback = new TextBox { Text = d.InaccessibleFallback, Width = 360, PlaceholderText = "the nearest existing parent, else This PC" };
        fallback.TextChanged += (_, _) =>
        {
            d.InaccessibleFallback = fallback.Text.Trim();
            Changed();
        };
        var browse = IconButton("\uE8DA", "Browse");
        browse.Click += async (_, _) =>
        {
            string? folder = await _dialogs.PickFolderAsync();
            if (folder != null)
                fallback.Text = folder;
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(fallback);
        row.Children.Add(browse);
        page.Children.Add(Card("If the path in a panel is inaccessible, go to", "When its directory is deleted or its drive removed.", row));
        return page;
    }

    // ---- Change Drive Menu and Drive Bar -------------------------------------------------------

    private UIElement BuildChangeDrivePage()
    {
        AppSettings s = _vm.Settings;
        DriveOptions d = s.Drives;
        StackPanel page = Page("Change Drive Menu and Drive Bar", "Choose the drives shown in the drive bar and in the Change Drive menu (Alt+F1 / Alt+F2).");

        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void Cell(FrameworkElement e, int row, int column)
        {
            Grid.SetRow(e, row);
            Grid.SetColumn(e, column);
            grid.Children.Add(e);
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cell(new TextBlock { Text = "Drive", Opacity = 0.7 }, 0, 0);
        Cell(new TextBlock { Text = "Visible", Opacity = 0.7 }, 0, 1);
        Cell(new TextBlock { Text = "Separator after", Opacity = 0.7 }, 0, 2);
        int r = 1;
        foreach (DriveButtonViewModel drive in _vm.Left.Drives)
        {
            string letter = drive.Letter;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Cell(new TextBlock { Text = drive.Drive.Caption, VerticalAlignment = VerticalAlignment.Center }, r, 0);
            var visible = new CheckBox { IsChecked = !d.HiddenDrives.Contains(letter, StringComparer.OrdinalIgnoreCase), MinWidth = 0 };
            visible.Click += (_, _) =>
            {
                d.HiddenDrives.RemoveAll(x => string.Equals(x, letter, StringComparison.OrdinalIgnoreCase));
                if (visible.IsChecked != true)
                    d.HiddenDrives.Add(letter);
                Changed(display: true);
            };
            var separator = new CheckBox { IsChecked = d.SeparatorAfter.Contains(letter, StringComparer.OrdinalIgnoreCase), MinWidth = 0 };
            separator.Click += (_, _) =>
            {
                d.SeparatorAfter.RemoveAll(x => string.Equals(x, letter, StringComparison.OrdinalIgnoreCase));
                if (separator.IsChecked == true)
                    d.SeparatorAfter.Add(letter);
                Changed(display: true);
            };
            Cell(visible, r, 1);
            Cell(separator, r, 2);
            r++;
        }
        page.Children.Add(new Border { Child = grid, Style = ThemedStyle("SettingsCardStyle") });

        page.Children.Add(Section("Additional items in the Change Drive menu"));
        page.Children.Add(Toggle("Documents", null, d.MenuShowDocuments, v => d.MenuShowDocuments = v));
        page.Children.Add(Toggle("Network", null, d.MenuShowNetwork, v => d.MenuShowNetwork = v));
        page.Children.Add(Toggle("As other panel", "The directory of the other panel.", d.MenuShowOtherPanelPath, v => d.MenuShowOtherPanelPath = v));
        page.Children.Add(Toggle("Hot paths", "Names are set on the Hot Paths page.", d.MenuShowHotPaths, v => d.MenuShowHotPaths = v));
        return page;
    }

    // ---- Viewers and Editors -------------------------------------------------------------------

    private UIElement BuildProgramsPage(bool viewers)
    {
        AppSettings s = _vm.Settings;
        List<ProgramRule> rules = viewers ? s.Viewers : s.Editors;
        StackPanel page = Page(viewers ? "Viewers" : "Editors", viewers
            ? "F3 opens files with the first matching viewer below. Files that match no rule open in their default Windows app (there is no internal viewer)."
            : "F4 opens files with the first matching editor below. Files that match no rule open with the Windows \"Edit\" command (or Notepad).");

        var list = new ListView { Height = 220, Width = 300, SelectionMode = ListViewSelectionMode.Single };
        var masks = new TextBox { Header = "File masks", Width = 380, PlaceholderText = "*.txt;*.log" };
        var command = new TextBox { Header = "Command", Width = 340, PlaceholderText = "notepad.exe, C:\\Tools\\viewer.exe ..." };
        var browse = new Button { Content = "...", VerticalAlignment = VerticalAlignment.Bottom };
        var arguments = new TextBox { Header = "Arguments", Width = 380, PlaceholderText = "\"$(FullName)\"" };
        var directory = new TextBox { Header = "Initial directory", Width = 380, PlaceholderText = "$(Path)" };
        var commandRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        commandRow.Children.Add(command);
        commandRow.Children.Add(browse);
        var fields = new StackPanel { Spacing = 6 };
        fields.Children.Add(masks);
        fields.Children.Add(commandRow);
        fields.Children.Add(arguments);
        fields.Children.Add(directory);
        fields.Children.Add(Hint("Variables as in the user menu: $(FullName), $(Name), $(NamePart), $(ExtPart), $(Path), $(OtherPath)."));

        bool loading = false;
        ProgramRule? Selected() => list.SelectedIndex >= 0 && list.SelectedIndex < rules.Count ? rules[list.SelectedIndex] : null;
        string Describe(ProgramRule r) => $"{r.Masks}  →  {(r.Command.Length > 0 ? Path.GetFileName(r.Command) : "(default app)")}";
        void Fill(int select)
        {
            loading = true;
            list.Items.Clear();
            foreach (ProgramRule rule in rules)
                list.Items.Add(new ListViewItem { Content = new TextBlock { Text = Describe(rule), TextTrimming = TextTrimming.CharacterEllipsis } });
            list.SelectedIndex = Math.Min(select, rules.Count - 1);
            loading = false;
            Load();
        }
        void Load()
        {
            loading = true;
            ProgramRule? rule = Selected();
            fields.Opacity = rule == null ? 0.5 : 1;
            fields.IsHitTestVisible = rule != null;
            masks.Text = rule?.Masks ?? string.Empty;
            command.Text = rule?.Command ?? string.Empty;
            arguments.Text = rule?.Arguments ?? string.Empty;
            directory.Text = rule?.InitialDirectory ?? string.Empty;
            loading = false;
        }
        void Store()
        {
            if (loading || Selected() is not { } rule)
                return;
            rule.Masks = masks.Text;
            rule.Command = command.Text;
            rule.Arguments = arguments.Text;
            rule.InitialDirectory = directory.Text;
            if (list.SelectedItem is ListViewItem { Content: TextBlock text })
                text.Text = Describe(rule);
            Changed();
        }
        list.SelectionChanged += (_, _) =>
        {
            if (!loading)
                Load();
        };
        masks.TextChanged += (_, _) => Store();
        command.TextChanged += (_, _) => Store();
        arguments.TextChanged += (_, _) => Store();
        directory.TextChanged += (_, _) => Store();
        browse.Click += async (_, _) =>
        {
            string? file = await _dialogs.PickFileAsync();
            if (file != null)
                command.Text = file;
        };

        var add = IconButton("\uE710", "Add", "Add");
        var remove = IconButton("\uE74D", "Remove");
        var up = IconButton("\uE70E", "Higher priority");
        var down = IconButton("\uE70D", "Lower priority");
        add.Click += (_, _) =>
        {
            int at = list.SelectedIndex >= 0 ? list.SelectedIndex + 1 : rules.Count;
            rules.Insert(at, new ProgramRule { Masks = viewers ? "*.txt;*.log" : "*.txt", Command = "notepad.exe" });
            Fill(at);
            Changed();
        };
        remove.Click += (_, _) =>
        {
            int i = list.SelectedIndex;
            if (i < 0)
                return;
            rules.RemoveAt(i);
            Fill(Math.Min(i, rules.Count - 1));
            Changed();
        };
        void Move(int delta)
        {
            int i = list.SelectedIndex, j = i + delta;
            if (i < 0 || j < 0 || j >= rules.Count)
                return;
            (rules[i], rules[j]) = (rules[j], rules[i]);
            Fill(j);
            Changed();
        }
        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(+1);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (Button b in new[] { add, remove, up, down })
            tools.Children.Add(b);

        var left = new StackPanel { Spacing = 6 };
        left.Children.Add(new TextBlock { Text = "File masks priority list", Style = Application.Current.Resources["BodyStrongTextBlockStyle"] as Style });
        left.Children.Add(new Border { Child = list, Style = ThemedStyle("SettingsFrameStyle") });
        left.Children.Add(tools);
        var layout = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        layout.Children.Add(left);
        layout.Children.Add(fields);
        Fill(0);
        page.Children.Add(new Border { Child = layout, Style = ThemedStyle("SettingsCardStyle") });
        return page;
    }

    // ---- History -------------------------------------------------------------------------------

    private UIElement BuildHistoryPage()
    {
        AppSettings s = _vm.Settings;
        HistoryOptions h = s.History;
        StackPanel page = Page("History", "Choose whether recently used values are kept with the configuration.");
        page.Children.Add(Toggle("Save history", "The list of opened files (Alt+F11).", h.SaveHistory, v => h.SaveHistory = v));
        page.Children.Add(Toggle("Save the list of working directories", "Alt+F12 and the drop-down arrow of the address bar.", h.SaveWorkingDirectories, v => h.SaveWorkingDirectories = v));

        page.Children.Add(Section("Command line"));
        page.Children.Add(Hint("The command line history can contain passwords, so it has its own options."));
        page.Children.Add(Toggle("Enable command line history", "Up / Down in the command line recall earlier commands.", h.CommandLineHistory, v => h.CommandLineHistory = v));
        page.Children.Add(Toggle("Save command line history", null, h.SaveCommandLineHistory, v => h.SaveCommandLineHistory = v));

        page.Children.Add(Section("Clear"));
        var clear = IconButton("\uE74D", "Delete all history", "Clear History...");
        clear.Click += async (_, _) =>
        {
            ContentDialog confirm = _dialogs.Create("Clear History",
                new TextBlock { Text = "Delete the lists of opened files and working directories, the command line history and the saved selection?", TextWrapping = TextWrapping.Wrap });
            confirm.PrimaryButtonText = "Clear";
            confirm.CloseButtonText = "Cancel";
            confirm.DefaultButton = ContentDialogButton.Close;
            if (await _dialogs.ShowDialogAsync(confirm) == ContentDialogResult.Primary)
                _vm.ClearHistory();
        };
        page.Children.Add(Card("Clear all history now", $"{s.OpenedFiles.Count} opened files, {s.WorkingDirectories.Count} working directories, {s.CommandLineHistory.Count} commands.", clear));
        return page;
    }

    // ---- Hot Paths (names) ---------------------------------------------------------------------

    /// <summary>The name box of a hot path (shown in the Change Drive menu and on the hot path bar).</summary>
    private TextBox HotPathNameBox(int n)
    {
        AppSettings s = _vm.Settings;
        var name = new TextBox { Text = s.HotPathNames[n], PlaceholderText = "name", Width = 140 };
        name.TextChanged += (_, _) =>
        {
            if (name.Text == s.HotPathNames[n])
                return;
            s.HotPathNames[n] = name.Text;
            Changed();
        };
        return name;
    }
}
