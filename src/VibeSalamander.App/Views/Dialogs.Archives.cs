// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Formatting;
using VibeSalamander.Core.Operations;
using VibeSalamander.Core.Tools;

namespace VibeSalamander.Views;

// Dialogs: pack and unpack (Alt+F5 / Alt+F6).
public sealed partial class Dialogs
{
    // ---- archives (Alt+F5 / Alt+F6) -----------------------------------------------------------

    /// <summary>A path box with a Browse button (the folder picker keeps the file name when keepFileName).</summary>
    private (Grid Row, TextBox Box) PathRow(string text, bool keepFileName)
    {
        var box = new TextBox { Text = text, MinWidth = 420, IsSpellCheckEnabled = false };
        var browse = new Button { Content = "Browse...", VerticalAlignment = VerticalAlignment.Stretch };
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(box);
        Grid.SetColumn(browse, 1);
        row.Children.Add(browse);
        browse.Click += async (_, _) =>
        {
            string? folder = await PickFolderAsync();
            if (folder == null)
                return;
            string name = keepFileName ? Path.GetFileName(box.Text.Trim()) : string.Empty;
            box.Text = name.Length > 0 ? Path.Combine(folder, name) : (folder.EndsWith('\\') ? folder : folder + "\\");
            box.Focus(FocusState.Programmatic);
            box.SelectionStart = box.Text.Length;
        };
        return (row, box);
    }

    public sealed record PackAnswer(string ArchivePath, string FormatId, string Compression, bool DeleteSources, string? Password = null,
        PackAdvanced? Advanced = null);

    /// <summary>Pack's Advanced part (7-Zip's "Add to archive" options); the strings are the enum names of the plugin contracts.</summary>
    /// <param name="FormatOptions">The formats' own settings ("7z.method" → "LZMA2"): remembered for every format.</param>
    public sealed record PackAdvanced(bool Open, string UpdateMode, string PathMode, string Volumes, string Encryption,
        bool SelfExtracting, bool NtfsTimes, IReadOnlyDictionary<string, string>? FormatOptions = null)
    {
        /// <summary>Bytes of "700M", "4G", "1.5G", "650M - CD", "123456"; 0 = empty; -1 = not a size.</summary>
        public static long ParseVolumeSize(string text)
        {
            string t = text.Trim();
            int dash = t.IndexOf(" -", StringComparison.Ordinal);
            if (dash > 0)
                t = t[..dash];
            t = t.Replace(" ", string.Empty);
            if (t.Length == 0)
                return 0;
            long unit = 1;
            char last = char.ToUpperInvariant(t[^1]);
            if (last is 'K' or 'M' or 'G' or 'T')
            {
                unit = last switch { 'K' => 1L << 10, 'M' => 1L << 20, 'G' => 1L << 30, _ => 1L << 40 };
                t = t[..^1];
            }
            else if (t.EndsWith("B", StringComparison.OrdinalIgnoreCase))
            {
                t = t[..^1];
            }
            if (!double.TryParse(t.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) ||
                value <= 0 || value * unit < 64 * 1024)
                return -1;
            return (long)(value * unit);
        }
    }

    /// <summary>The choices of the Advanced part: (enum name, English text).</summary>
    private static readonly (string Value, string Text)[] UpdateModes =
    {
        ("Replace", "Replace the archive (ask first)"), ("AddAndReplace", "Add and replace files"), ("UpdateAndAdd", "Update and add files"),
        ("Freshen", "Freshen existing files"), ("Synchronize", "Synchronize files"),
    };

    private static readonly (string Value, string Text)[] PathModes =
    {
        ("Relative", "Relative paths"), ("Full", "Full paths (without the drive)"), ("None", "No paths (file names only)"),
    };

    private static readonly (string Value, string Text)[] EncryptionMethods =
    {
        ("Aes256", "AES-256 (strong)"), ("ZipCrypto", "ZipCrypto (Windows Explorer opens it; weak)"),
    };

    private static readonly string[] VolumePresets = { "10M", "100M", "1000M", "650M - CD", "700M - CD", "4092M - FAT", "4480M - DVD", "8128M - DVD DL", "23040M - BD" };

    private static ComboBox Choices(string header, (string Value, string Text)[] choices, string selected)
    {
        var box = new ComboBox { Header = header, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (_, text) in choices)
            box.Items.Add(Services.Loc.T(text));
        box.SelectedIndex = Math.Max(0, Array.FindIndex(choices, c => c.Value == selected));
        return box;
    }

    /// <summary>
    /// Pack (Alt+F5): the archive's path and name (prefilled), its format and compression and "move to
    /// archive", and a password for formats that can be encrypted (typed twice; never remembered).
    /// Changing the format changes the extension in the name. Null when cancelled.
    /// </summary>
    public async Task<PackAnswer?> PackAsync(string what, string defaultPath, IReadOnlyList<Plugins.ArchiveFormat> formats,
        string formatId, string compression, bool deleteSources, PackAdvanced advanced)
    {
        var (row, box) = PathRow(defaultPath, keepFileName: true);
        var format = new ComboBox { MinWidth = 200, Header = "Format" };
        foreach (Plugins.ArchiveFormat f in formats)
            format.Items.Add(Services.Loc.T(f.DisplayName));
        int selected = Math.Max(0, formats.ToList().FindIndex(f => string.Equals(f.Id, formatId, StringComparison.OrdinalIgnoreCase)));
        format.SelectedIndex = selected;
        string[] levels = { "Store", "Fastest", "Normal", "Maximum" };
        var level = new ComboBox { MinWidth = 160, Header = "Compression" };
        foreach (string l in new[] { "Store (no compression)", "Fastest", "Normal", "Maximum (slowest)" })
            level.Items.Add(Services.Loc.T(l));
        level.SelectedIndex = Math.Max(0, Array.IndexOf(levels, compression));
        level.IsEnabled = formats[selected].HasCompressionLevels;
        var move = new CheckBox { Content = "Delete the files after packing (move to archive)", IsChecked = deleteSources };

        // password (ZIP: AES-256 or ZipCrypto): typed twice, shown only when asked for
        var encrypt = new CheckBox { Content = "Encrypt with a password", IsEnabled = formats[selected].SupportsPassword };
        var password = new PasswordBox { PlaceholderText = "Password", MinWidth = 200 };
        var repeat = new PasswordBox { PlaceholderText = "Repeat the password", MinWidth = 200 };
        var passwordError = new TextBlock { Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"], Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        var passwordBoxes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        passwordBoxes.Children.Add(password);
        passwordBoxes.Children.Add(repeat);
        var showPassword = new CheckBox { Content = "Show the password" };
        showPassword.Checked += (_, _) => password.PasswordRevealMode = repeat.PasswordRevealMode = PasswordRevealMode.Visible;
        showPassword.Unchecked += (_, _) => password.PasswordRevealMode = repeat.PasswordRevealMode = PasswordRevealMode.Hidden;
        var passwords = new StackPanel { Spacing = 4, Visibility = Visibility.Collapsed };
        passwords.Children.Add(passwordBoxes);
        passwords.Children.Add(showPassword);

        // Advanced: 7-Zip's "Add to archive" options that fit these formats
        var updateMode = Choices("When the archive exists", UpdateModes, advanced.UpdateMode);
        var pathMode = Choices("Paths in the archive", PathModes, advanced.PathMode);
        var volumes = new ComboBox { Header = "Split into volumes of", IsEditable = true, HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "One file", Text = advanced.Volumes };
        foreach (string preset in VolumePresets)
            volumes.Items.Add(preset);
        var encryption = Choices("Encryption method", EncryptionMethods, advanced.Encryption);
        var selfExtracting = new CheckBox { Content = "Create a self-extracting archive (.exe)", IsChecked = advanced.SelfExtracting };
        var ntfsTimes = new CheckBox { Content = "Store creation and last access times", IsChecked = advanced.NtfsTimes };
        var advancedGrid = new Grid { ColumnSpacing = 12, RowSpacing = 10 };
        advancedGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        advancedGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 4; i++)
            advancedGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void Place(FrameworkElement element, int row, int column, int span = 1)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            Grid.SetColumnSpan(element, span);
            advancedGrid.Children.Add(element);
        }
        Place(updateMode, 0, 0);
        Place(pathMode, 0, 1);
        Place(volumes, 1, 0);
        Place(encryption, 1, 1);
        Place(selfExtracting, 2, 0, 2);
        Place(ntfsTimes, 3, 0, 2);

        // the chosen format's own settings (7z: method, dictionary, ...), built anew when the format changes
        var formatValues = new Dictionary<string, string>(advanced.FormatOptions ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        var formatGrid = new Grid { ColumnSpacing = 12, RowSpacing = 10 };
        formatGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        formatGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        advancedGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Place(formatGrid, 4, 0, 2);
        var passwordOptions = new List<Control>();
        void BuildFormatOptions()
        {
            formatGrid.Children.Clear();
            formatGrid.RowDefinitions.Clear();
            passwordOptions.Clear();
            Plugins.ArchiveFormat f = formats[Math.Max(0, format.SelectedIndex)];
            if (f.Options is not { Count: > 0 } options)
                return;
            int choice = 0, checks = 0;
            int choiceRows = (options.Count(o => o.Kind == Plugins.PluginFieldKind.Choice) + 1) / 2;
            foreach (Plugins.PackOption option in options)
            {
                string key = f.Id + "." + option.Id;
                string value = formatValues.TryGetValue(key, out string? remembered) ? remembered : option.Default;
                Control control;
                int row, column = 0, span = 1;
                if (option.Kind == Plugins.PluginFieldKind.Check)
                {
                    var check = new CheckBox { Content = Services.Loc.T(option.Label), IsChecked = value == "true" };
                    check.Checked += (_, _) => formatValues[key] = "true";
                    check.Unchecked += (_, _) => formatValues[key] = "false";
                    control = check;
                    row = choiceRows + checks++;
                    span = 2;
                }
                else
                {
                    var box = new ComboBox { Header = Services.Loc.T(option.Label), HorizontalAlignment = HorizontalAlignment.Stretch };
                    foreach (string c in option.Choices)
                        box.Items.Add(Services.Loc.T(c));
                    int index = option.Choices.ToList().IndexOf(value);
                    box.SelectedIndex = index >= 0 ? index : Math.Max(0, option.Choices.ToList().IndexOf(option.Default));
                    box.SelectionChanged += (_, _) =>
                    {
                        if (box.SelectedIndex >= 0)
                            formatValues[key] = option.Choices[box.SelectedIndex];
                    };
                    control = box;
                    row = choice / 2;
                    column = choice % 2;
                    choice++;
                }
                while (formatGrid.RowDefinitions.Count <= row)
                    formatGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(control, row);
                Grid.SetColumn(control, column);
                Grid.SetColumnSpan(control, span);
                control.Tag = "NoLoc";
                formatGrid.Children.Add(control);
                if (option.NeedsPassword)
                    passwordOptions.Add(control);
            }
        }
        BuildFormatOptions();
        format.SelectionChanged += (_, _) =>
        {
            BuildFormatOptions();
            UpdateAdvanced();
        };
        var advancedPart = new Expander
        {
            Header = "Advanced",
            Content = advancedGrid,
            IsExpanded = advanced.Open,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        // what applies to the chosen format and the other choices
        void UpdateAdvanced()
        {
            Plugins.ArchiveFormat f = formats[Math.Max(0, format.SelectedIndex)];
            bool encrypted = encrypt.IsChecked == true && f.SupportsPassword;
            encryption.IsEnabled = f.IsZip && encrypted;
            selfExtracting.IsEnabled = f.IsZip && !encrypted;
            ntfsTimes.IsEnabled = f.IsZip;
            volumes.IsEnabled = !(selfExtracting.IsEnabled && selfExtracting.IsChecked == true);
            foreach (Control control in passwordOptions)
                control.IsEnabled = encrypted;
        }
        encrypt.Checked += (_, _) => UpdateAdvanced();
        encrypt.Unchecked += (_, _) => UpdateAdvanced();
        selfExtracting.Checked += (_, _) => UpdateAdvanced();
        selfExtracting.Unchecked += (_, _) => UpdateAdvanced();
        format.SelectionChanged += (_, _) => UpdateAdvanced();
        UpdateAdvanced();
        encrypt.Checked += (_, _) =>
        {
            passwords.Visibility = Visibility.Visible;
            password.Focus(FocusState.Programmatic);
        };
        encrypt.Unchecked += (_, _) =>
        {
            passwords.Visibility = Visibility.Collapsed;
            passwordError.Visibility = Visibility.Collapsed;
        };
        // null = fine; otherwise what is wrong with the password (or the volume size)
        string? PasswordProblem()
        {
            if (volumes.IsEnabled && PackAdvanced.ParseVolumeSize(volumes.Text ?? string.Empty) < 0)
                return "The volume size is a number of bytes, or with K, M or G (like 700M or 4G), and at least 64K.";
            if (encrypt.IsChecked != true || !encrypt.IsEnabled)
                return null;
            if (password.Password.Length == 0)
                return "Type a password, or turn off the encryption.";
            return password.Password == repeat.Password ? null : "The passwords don't match.";
        }

        // the extension follows the format ("backup.zip" → "backup.tar.gz")
        string currentExtension = formats[selected].Extension;
        format.SelectionChanged += (_, _) =>
        {
            if (format.SelectedIndex < 0)
                return;
            Plugins.ArchiveFormat chosen = formats[format.SelectedIndex];
            level.IsEnabled = chosen.HasCompressionLevels;
            encrypt.IsEnabled = chosen.SupportsPassword;
            passwords.Visibility = chosen.SupportsPassword && encrypt.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            string text = box.Text.TrimEnd();
            if (text.EndsWith(currentExtension, StringComparison.OrdinalIgnoreCase))
                box.Text = text[..^currentExtension.Length] + chosen.Extension;
            currentExtension = chosen.Extension;
        };

        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        options.Children.Add(format);
        options.Children.Add(level);
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(Text($"Pack {what} into:"));
        body.Children.Add(row);
        body.Children.Add(options);
        body.Children.Add(move);
        body.Children.Add(encrypt);
        body.Children.Add(passwords);
        body.Children.Add(advancedPart);
        body.Children.Add(passwordError);

        // the Advanced part (7z has many settings) can be taller than the window: the dialog's content scrolls
        var scroller = new ScrollViewer
        {
            Content = body,
            MaxHeight = 560,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 16, 0), // room for the scroll bar next to the fields
        };
        ContentDialog dialog = NewDialog("Pack", scroller);
        dialog.Opened += (_, _) =>
        {
            if (dialog.XamlRoot is { } root)
                scroller.MaxHeight = Math.Max(240, root.Size.Height - 220); // the title and the buttons stay visible
        };
        advancedPart.Expanding += (_, _) => dialog.DispatcherQueue.TryEnqueue(() => scroller.ChangeView(null, scroller.ScrollableHeight, null));
        dialog.PrimaryButtonText = "Pack";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        // a missing or mistyped password keeps the dialog open
        bool ShowPasswordProblem()
        {
            string? problem = PasswordProblem();
            passwordError.Text = problem == null ? string.Empty : Services.Loc.T(problem);
            passwordError.Visibility = problem == null ? Visibility.Collapsed : Visibility.Visible;
            return problem != null;
        }
        dialog.PrimaryButtonClick += (_, e) => e.Cancel = ShowPasswordProblem();
        bool enter = false;
        foreach (Control input in new Control[] { box, password, repeat })
        {
            input.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    e.Handled = true;
                    if (ShowPasswordProblem())
                        return;
                    enter = true;
                    dialog.Hide();
                }
            };
        }
        dialog.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            // select the name without the extension, like a rename
            string text = box.Text;
            int nameStart = text.LastIndexOf('\\') + 1;
            int nameEnd = text.EndsWith(currentExtension, StringComparison.OrdinalIgnoreCase) ? text.Length - currentExtension.Length : text.Length;
            box.Select(nameStart, Math.Max(0, nameEnd - nameStart));
        };
        ContentDialogResult result = await ShowAsync(dialog);
        if ((!enter && result != ContentDialogResult.Primary) || string.IsNullOrWhiteSpace(box.Text) || format.SelectedIndex < 0)
            return null;
        bool encrypted = encrypt.IsChecked == true && formats[format.SelectedIndex].SupportsPassword && password.Password.Length > 0;
        var chosenAdvanced = new PackAdvanced(
            advancedPart.IsExpanded,
            UpdateModes[Math.Max(0, updateMode.SelectedIndex)].Value,
            PathModes[Math.Max(0, pathMode.SelectedIndex)].Value,
            (volumes.Text ?? string.Empty).Trim(),
            EncryptionMethods[Math.Max(0, encryption.SelectedIndex)].Value,
            selfExtracting.IsChecked == true,
            ntfsTimes.IsChecked == true,
            formatValues);
        return new PackAnswer(box.Text.Trim(), formats[format.SelectedIndex].Id, levels[Math.Max(0, level.SelectedIndex)], move.IsChecked == true,
            encrypted ? password.Password : null, chosenAdvanced);
    }

    /// <param name="SubfolderFromArchive">the checkbox was set by the archive's root folder, not by hand: not a setting to keep</param>
    public sealed record UnpackAnswer(string Target, string Mask, bool ToSubfolder, bool SubfolderFromArchive = false);

    /// <summary>Unpack (Alt+F6): where to (prefilled with the other panel), which files, one folder per archive.</summary>
    /// <param name="rootFolder">
    /// for one archive: its single root folder, found while the dialog is open (null: none). Then the archive's own
    /// folder isn't needed: the checkbox is cleared (unless already changed by hand) and the folder named next to it.
    /// </param>
    public async Task<UnpackAnswer?> UnpackAsync(string what, string defaultTarget, string mask, bool toSubfolder, string subfolderExample,
        Task<string?>? rootFolder = null)
    {
        var (row, box) = PathRow(defaultTarget, keepFileName: false);
        var maskBox = new TextBox { Header = "Files to unpack", Text = mask, MinWidth = 200, IsSpellCheckEnabled = false };
        var subfolder = new CheckBox { Content = $"Into a folder named after the archive ({subfolderExample})", IsChecked = toSubfolder };
        // below the checkbox (its text already fills the dialog's width), lined up with the checkbox's text
        var found = new TextBlock { Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Tag = "NoLoc", Margin = new Thickness(28, -4, 0, 0), Visibility = Visibility.Collapsed };
        var subfolderRow = new StackPanel { Spacing = 0 };
        subfolderRow.Children.Add(subfolder);
        subfolderRow.Children.Add(found);
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(Text($"Unpack {what} to:"));
        body.Children.Add(row);
        body.Children.Add(subfolderRow);
        body.Children.Add(maskBox);
        bool changedByHand = false, fromArchive = false;
        subfolder.Click += (_, _) => changedByHand = true;
        if (rootFolder != null)
        {
            _ = Task.Run(async () =>
            {
                string? root = await rootFolder.ConfigureAwait(false);
                if (root == null)
                    return;
                subfolder.DispatcherQueue.TryEnqueue(() =>
                {
                    found.Text = Services.Loc.F("Root folder {0} found", root);
                    found.Visibility = Visibility.Visible;
                    if (!changedByHand && subfolder.IsChecked == true)
                    {
                        subfolder.IsChecked = false;
                        fromArchive = true;
                    }
                });
            });
        }

        ContentDialog dialog = NewDialog("Unpack", body);
        dialog.PrimaryButtonText = "Unpack";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        bool enter = false;
        foreach (TextBox b in new[] { box, maskBox })
        {
            b.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    enter = true;
                    e.Handled = true;
                    dialog.Hide();
                }
            };
        }
        dialog.Opened += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        ContentDialogResult result = await ShowAsync(dialog);
        if ((!enter && result != ContentDialogResult.Primary) || string.IsNullOrWhiteSpace(box.Text))
            return null;
        string m = maskBox.Text.Trim();
        return new UnpackAnswer(box.Text.Trim(), m.Length == 0 ? "*.*" : m, subfolder.IsChecked == true, fromArchive && !changedByHand);
    }

    /// <summary>The password of an encrypted archive; null when cancelled.</summary>
    public async Task<string?> PasswordAsync(string archivePath, bool wrongPassword, string? context = null)
    {
        var password = new PasswordBox { PlaceholderText = "Password", MinWidth = 320 };
        var body = new StackPanel { Spacing = 8 };
        if (!string.IsNullOrEmpty(context))
            body.Children.Add(Text(context, secondary: true));
        body.Children.Add(Text(wrongPassword ? "The password is wrong. Enter it again:" : "This archive is protected by a password:"));
        body.Children.Add(Text(archivePath, bold: true));
        body.Children.Add(password);
        ContentDialog dialog = NewDialog("Password", body);
        dialog.PrimaryButtonText = "OK";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        bool enter = false;
        password.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                enter = true;
                e.Handled = true;
                dialog.Hide();
            }
        };
        dialog.Opened += (_, _) => password.Focus(FocusState.Programmatic);
        ContentDialogResult result = await ShowAsync(dialog);
        if (!enter && result != ContentDialogResult.Primary)
            return null;
        return password.Password;
    }
}
