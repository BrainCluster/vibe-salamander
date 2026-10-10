using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Formatting;
using VibeSalamander.Core.Operations;
using VibeSalamander.Core.Tools;

namespace VibeSalamander.Views;

/// <summary>Phase-2 dialogs built in code (ContentDialog based, Salamander wording).</summary>
public sealed class Dialogs
{
    private readonly Func<XamlRoot> _xamlRoot;
    private readonly Func<ElementTheme> _theme;
    private readonly Func<Microsoft.UI.WindowId> _windowId;

    public Dialogs(Func<XamlRoot> xamlRoot, Func<ElementTheme> theme, Func<Microsoft.UI.WindowId> windowId)
    {
        _xamlRoot = xamlRoot;
        _theme = theme;
        _windowId = windowId;
    }

    /// <summary>Raised while a dialog is open (the main window suspends its key handling).</summary>
    public event EventHandler<bool>? DialogOpenChanged;

    private ContentDialog NewDialog(string title, object content) => new()
    {
        Title = title,
        Content = content,
        XamlRoot = _xamlRoot(),
        RequestedTheme = _theme(),
    };

    /// <summary>
    /// Only one ContentDialog may be open per window. Background transfers ask questions too, so every
    /// dialog in the app waits its turn here instead of failing with "only one dialog can be open".
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Shows any ContentDialog through the shared gate (use this instead of ContentDialog.ShowAsync).</summary>
    public async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        await Gate.WaitAsync();
        DialogOpenChanged?.Invoke(this, true);
        try
        {
            dialog.XamlRoot ??= _xamlRoot();
            return await dialog.ShowAsync();
        }
        finally
        {
            DialogOpenChanged?.Invoke(this, false);
            Gate.Release();
        }
    }

    private Task<ContentDialogResult> ShowAsync(ContentDialog dialog) => ShowDialogAsync(dialog);

    /// <summary>Creates a dialog with the app's theme (for callers that build their own content).</summary>
    public ContentDialog Create(string title, object content) => NewDialog(title, content);

    /// <summary>A simple message with an OK button.</summary>
    public async Task MessageAsync(string title, string message)
    {
        ContentDialog dialog = NewDialog(title, Text(message));
        dialog.CloseButtonText = "OK";
        dialog.DefaultButton = ContentDialogButton.Close;
        await ShowAsync(dialog);
    }

    /// <summary>A dialog whose answers are buttons inside the content (more than ContentDialog's three).</summary>
    private async Task<T> ChooseAsync<T>(string title, UIElement body, (string Label, T Value, bool Accent)[] buttons, T cancelValue)
    {
        T result = cancelValue;
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8, Margin = new Thickness(0, 16, 0, 0) };
        const int columns = 3;
        for (int c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int rows = (buttons.Length + columns - 1) / columns;
        for (int r = 0; r < rows; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var panel = new StackPanel();
        panel.Children.Add(body);
        panel.Children.Add(grid);
        ContentDialog dialog = NewDialog(title, panel);
        dialog.CloseButtonText = "Cancel";

        for (int i = 0; i < buttons.Length; i++)
        {
            var (label, value, accent) = buttons[i];
            var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch };
            if (accent && Application.Current.Resources.TryGetValue("AccentButtonStyle", out object? style))
                button.Style = style as Style;
            button.Click += (_, _) =>
            {
                result = value;
                dialog.Hide();
            };
            Grid.SetColumn(button, i % columns);
            Grid.SetRow(button, i / columns);
            grid.Children.Add(button);
        }
        dialog.Opened += (_, _) => (grid.Children[0] as Control)?.Focus(FocusState.Programmatic);
        await ShowAsync(dialog);
        return result;
    }

    private static TextBlock Text(string text, bool bold = false, bool secondary = false) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Opacity = secondary ? 0.75 : 1,
    };

    // ---- operation prompts -------------------------------------------------------------------

    /// <param name="context">Which operation asks (e.g. "Copy 3 items"); shown above the question
    /// because several transfers may run at once.</param>
    public Task<ConflictChoice> ConflictAsync(ConflictInfo c, string? context = null)
    {
        var body = new StackPanel { Spacing = 6 };
        if (!string.IsNullOrEmpty(context))
            body.Children.Add(Text(context, secondary: true));
        if (c.IsMerge)
        {
            body.Children.Add(Text("The target directory already exists. Merge the copied directory into it?"));
            body.Children.Add(Text(c.TargetPath, bold: true));
            return ChooseAsync("Confirm Directory Merge", body, new[]
            {
                ("Merge", ConflictChoice.Overwrite, true),
                ("Merge All", ConflictChoice.OverwriteAll, false),
                ("Skip", ConflictChoice.Skip, false),
                ("Skip All", ConflictChoice.SkipAll, false),
            }, ConflictChoice.Cancel);
        }
        if ((c.TargetAttributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
            body.Children.Add(Text("The target is a hidden or system file.", secondary: true));
        body.Children.Add(Text(c.IsDirectory ? "A directory with this name already exists:" : "The target file already exists:"));
        body.Children.Add(Text(c.TargetPath, bold: true));
        if (!c.IsDirectory)
        {
            string Compare(DateTime a, DateTime b) => a > b ? " (newer)" : a < b ? " (older)" : string.Empty;
            body.Children.Add(Text($"Existing:  {Formats.ExactSize(c.TargetSize)} bytes, {c.TargetTime:g}{Compare(c.TargetTime, c.SourceTime)}", secondary: true));
            body.Children.Add(Text($"New:         {Formats.ExactSize(c.SourceSize)} bytes, {c.SourceTime:g}{Compare(c.SourceTime, c.TargetTime)}", secondary: true));
        }
        return ChooseAsync("Confirm Overwrite", body, new[]
        {
            ("Overwrite", ConflictChoice.Overwrite, true),
            ("Overwrite All", ConflictChoice.OverwriteAll, false),
            ("Overwrite All Older", ConflictChoice.OverwriteAllOlder, false),
            ("Skip", ConflictChoice.Skip, false),
            ("Skip All", ConflictChoice.SkipAll, false),
            ("Keep Both", ConflictChoice.Rename, false),
        }, ConflictChoice.Cancel);
    }

    public Task<ErrorChoice> ErrorAsync(OperationError e, string? context = null)
    {
        var body = new StackPanel { Spacing = 6 };
        if (!string.IsNullOrEmpty(context))
            body.Children.Add(Text(context, secondary: true));
        body.Children.Add(Text(e.Path, bold: true));
        body.Children.Add(Text(e.Message));
        return ChooseAsync("Error", body, new[]
        {
            ("Retry", ErrorChoice.Retry, true),
            ("Skip", ErrorChoice.Skip, false),
            ("Skip All", ErrorChoice.SkipAll, false),
        }, ErrorChoice.Cancel);
    }

    // ---- copy / move target (F5 / F6) --------------------------------------------------------

    /// <summary>
    /// Asks where to copy/move: a target path (prefilled with the other panel), a Browse button and
    /// "Don't show this dialog again". Returns null when cancelled.
    /// </summary>
    public async Task<(string Target, bool DontAskAgain)?> CopyMoveTargetAsync(bool move, string what, string defaultTarget)
    {
        string verb = move ? "Move" : "Copy";
        var box = new TextBox { Text = defaultTarget, MinWidth = 380, IsSpellCheckEnabled = false };
        var browse = new Button { Content = "Browse...", VerticalAlignment = VerticalAlignment.Stretch };
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(box);
        Grid.SetColumn(browse, 1);
        row.Children.Add(browse);
        var dontAsk = new CheckBox
        {
            Content = $"Don't show this dialog again ({verb.ToLowerInvariant()} straight into the other panel)",
        };
        var hint = Text("Options → Ask for Copy/Move Target shows it again.", secondary: true);
        hint.FontSize = 12;
        hint.Visibility = Visibility.Collapsed;
        dontAsk.Checked += (_, _) => hint.Visibility = Visibility.Visible;
        dontAsk.Unchecked += (_, _) => hint.Visibility = Visibility.Collapsed;

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(Text($"{verb} {what} to:"));
        body.Children.Add(row);
        body.Children.Add(dontAsk);
        body.Children.Add(hint);

        ContentDialog dialog = NewDialog(move ? "Move/Rename" : "Copy", body);
        dialog.PrimaryButtonText = verb;
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;

        browse.Click += async (_, _) =>
        {
            string? folder = await PickFolderAsync();
            if (folder != null)
            {
                box.Text = folder.EndsWith('\\') ? folder : folder + "\\";
                box.Focus(FocusState.Programmatic);
                box.SelectionStart = box.Text.Length;
            }
        };
        bool enter = false;
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                enter = true;
                e.Handled = true;
                dialog.Hide();
            }
        };
        dialog.Opened += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };

        ContentDialogResult result = await ShowAsync(dialog);
        if ((!enter && result != ContentDialogResult.Primary) || string.IsNullOrWhiteSpace(box.Text))
            return null;
        return (box.Text.Trim(), dontAsk.IsChecked == true);
    }

    /// <summary>The Windows folder picker; null when cancelled or unavailable.</summary>
    public async Task<string?> PickFolderAsync()
    {
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(_windowId());
            var picked = await picker.PickSingleFolderAsync();
            return string.IsNullOrEmpty(picked?.Path) ? null : picked.Path;
        }
        catch (Exception)
        {
            return null; // e.g. the picker is not available on this system
        }
    }

    // ---- compare directories (Ctrl+F10) -------------------------------------------------------

    /// <summary>What to compare; returns null when cancelled. Enter starts the comparison.</summary>
    public async Task<CompareOptions?> CompareOptionsAsync(CompareOptions current)
    {
        var byDate = new CheckBox { Content = "Date and time (the newer file is marked)", IsChecked = current.ByDate };
        var bySize = new CheckBox { Content = "Size", IsChecked = current.BySize };
        var byContent = new CheckBox { Content = "Content (reads the files, slower)", IsChecked = current.ByContent };
        var subdirs = new CheckBox { Content = "Compare the content of subdirectories", IsChecked = current.Subdirectories };
        var body = new StackPanel { Spacing = 2, MinWidth = 360 };
        body.Children.Add(Text("Items missing on the other side are always marked. Also compare files by:", secondary: true));
        body.Children.Add(byDate);
        body.Children.Add(bySize);
        body.Children.Add(byContent);
        body.Children.Add(new Border { Height = 8 });
        body.Children.Add(subdirs);

        ContentDialog dialog = NewDialog("Compare Directories", body);
        dialog.PrimaryButtonText = "Compare";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        if (await ShowAsync(dialog) != ContentDialogResult.Primary)
            return null;
        return new CompareOptions
        {
            ByDate = byDate.IsChecked == true,
            BySize = bySize.IsChecked == true,
            ByContent = byContent.IsChecked == true,
            Subdirectories = subdirs.IsChecked == true,
        };
    }

    // ---- user menu editor (Commands → User Menu → Edit) ----------------------------------------

    /// <summary>Edits a copy of the user menu; returns the new list, or null when cancelled.</summary>
    public async Task<List<UserMenuItem>?> EditUserMenuAsync(IReadOnlyList<UserMenuItem> current)
    {
        var items = new System.Collections.ObjectModel.ObservableCollection<UserMenuItem>(current.Select(i => i.Clone()));
        var list = new ListView
        {
            ItemsSource = items,
            Width = 230,
            Height = 300,
            SelectionMode = ListViewSelectionMode.Single,
            BorderThickness = new Thickness(1),
            BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Microsoft.UI.Xaml.Media.Brush,
            CornerRadius = new CornerRadius(4),
        };
        list.DisplayMemberPath = nameof(UserMenuItem.Name);

        var name = new TextBox { Header = "Name (\"-\" for a separator)" };
        var command = new TextBox { Header = "Command", PlaceholderText = "cmd.exe, notepad.exe, C:\\Tools\\tool.exe ..." };
        var browse = new Button { Content = "...", VerticalAlignment = VerticalAlignment.Bottom };
        var arguments = new TextBox { Header = "Arguments", PlaceholderText = "\"$(FullName)\"" };
        var directory = new TextBox { Header = "Initial directory", PlaceholderText = "$(Path)" };
        var help = Text(string.Join("\n", UserMenu.Variables.Select(v => $"{v.Variable}  {v.Meaning}")), secondary: true);
        help.FontSize = 11;

        var commandRow = new Grid { ColumnSpacing = 6 };
        commandRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        commandRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        commandRow.Children.Add(command);
        Grid.SetColumn(browse, 1);
        commandRow.Children.Add(browse);

        var fields = new StackPanel { Spacing = 6, Width = 330 };
        fields.Children.Add(name);
        fields.Children.Add(commandRow);
        fields.Children.Add(arguments);
        fields.Children.Add(directory);
        fields.Children.Add(help);

        Button Tool(string glyph, string tip)
        {
            var b = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 12 }, Padding = new Thickness(8, 5, 8, 5) };
            ToolTipService.SetToolTip(b, tip);
            return b;
        }
        Button add = Tool("\uE710", "Add"), separator = Tool("\uE76F", "Add separator"), remove = Tool("\uE74D", "Remove"),
               up = Tool("\uE70E", "Move up"), down = Tool("\uE70D", "Move down"), reset = Tool("\uE72C", "Restore the default menu");
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (Button b in new[] { add, separator, remove, up, down, reset })
            tools.Children.Add(b);

        var left = new StackPanel { Spacing = 6 };
        left.Children.Add(list);
        left.Children.Add(tools);
        var layout = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        layout.Children.Add(left);
        layout.Children.Add(fields);

        bool loading = false;
        UserMenuItem? Selected() => list.SelectedItem as UserMenuItem;
        void Load()
        {
            loading = true;
            UserMenuItem? item = Selected();
            name.Text = item?.Name ?? string.Empty;
            command.Text = item?.Command ?? string.Empty;
            arguments.Text = item?.Arguments ?? string.Empty;
            directory.Text = item?.InitialDirectory ?? string.Empty;
            fields.Opacity = item == null ? 0.5 : 1;
            fields.IsHitTestVisible = item != null;
            loading = false;
        }
        void Store()
        {
            if (loading || Selected() is not { } item)
                return;
            item.Command = command.Text;
            item.Arguments = arguments.Text;
            item.InitialDirectory = directory.Text;
            if (item.Name != name.Text)
            {
                // the list shows the name: replace the item to refresh its row
                item.Name = name.Text;
                int index = items.IndexOf(item);
                if (index >= 0)
                {
                    loading = true;
                    items[index] = item.Clone();
                    list.SelectedIndex = index;
                    loading = false;
                }
            }
        }
        list.SelectionChanged += (_, _) => { if (!loading) Load(); };
        name.TextChanged += (_, _) => Store();
        command.TextChanged += (_, _) => Store();
        arguments.TextChanged += (_, _) => Store();
        directory.TextChanged += (_, _) => Store();

        void Insert(UserMenuItem item)
        {
            int at = list.SelectedIndex >= 0 ? list.SelectedIndex + 1 : items.Count;
            items.Insert(at, item);
            list.SelectedIndex = at;
        }
        add.Click += (_, _) => { Insert(new UserMenuItem { Name = "New command", InitialDirectory = "$(Path)" }); name.Focus(FocusState.Programmatic); name.SelectAll(); };
        separator.Click += (_, _) => Insert(new UserMenuItem { Name = "-" });
        remove.Click += (_, _) =>
        {
            int i = list.SelectedIndex;
            if (i < 0)
                return;
            items.RemoveAt(i);
            list.SelectedIndex = Math.Min(i, items.Count - 1);
        };
        void Move(int delta)
        {
            int i = list.SelectedIndex, j = i + delta;
            if (i < 0 || j < 0 || j >= items.Count)
                return;
            items.Move(i, j);
            list.SelectedIndex = j;
        }
        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(+1);
        reset.Click += (_, _) =>
        {
            items.Clear();
            foreach (UserMenuItem item in UserMenu.CreateDefaults())
                items.Add(item);
            list.SelectedIndex = items.Count > 0 ? 0 : -1;
        };
        browse.Click += async (_, _) =>
        {
            string? file = await PickFileAsync();
            if (file != null)
                command.Text = file;
        };

        list.SelectedIndex = items.Count > 0 ? 0 : -1;
        Load();

        ContentDialog dialog = NewDialog("User Menu", layout);
        dialog.PrimaryButtonText = "Save";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        dialog.Resources["ContentDialogMaxWidth"] = 760.0;
        if (await ShowAsync(dialog) != ContentDialogResult.Primary)
            return null;
        return items.Where(i => i.IsSeparator || !string.IsNullOrWhiteSpace(i.Name)).ToList();
    }

    /// <summary>The Windows file picker (for a program); null when cancelled or unavailable.</summary>
    public async Task<string?> PickFileAsync()
    {
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(_windowId());
            picker.FileTypeFilter.Add(".exe");
            picker.FileTypeFilter.Add(".cmd");
            picker.FileTypeFilter.Add(".bat");
            picker.FileTypeFilter.Add(".ps1");
            picker.FileTypeFilter.Add("*");
            var picked = await picker.PickSingleFileAsync();
            return string.IsNullOrEmpty(picked?.Path) ? null : picked.Path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- change attributes (Ctrl+F2) -----------------------------------------------------------

    public async Task<AttributeChange?> AttributesAsync(IReadOnlyList<FileEntry> items)
    {
        CheckBox Box(string label, FileAttributes flag)
        {
            int on = items.Count(i => (i.Attributes & flag) != 0);
            bool? state = on == 0 ? false : on == items.Count ? true : null;
            return new CheckBox { Content = label, IsThreeState = state == null, IsChecked = state, Tag = flag };
        }
        var boxes = new[]
        {
            Box("Read only", FileAttributes.ReadOnly),
            Box("Hidden", FileAttributes.Hidden),
            Box("System", FileAttributes.System),
            Box("Archive", FileAttributes.Archive),
        };

        DateTime current = items.Count == 1 ? items[0].LastWrite : DateTime.Now;
        var changeTime = new CheckBox { Content = "Change last write time:" };
        var date = new CalendarDatePicker { Date = new DateTimeOffset(current), IsEnabled = false };
        var time = new TimePicker { Time = current.TimeOfDay, ClockIdentifier = "24HourClock", IsEnabled = false };
        changeTime.Checked += (_, _) => { date.IsEnabled = true; time.IsEnabled = true; };
        changeTime.Unchecked += (_, _) => { date.IsEnabled = false; time.IsEnabled = false; };
        var subdirs = new CheckBox { Content = "Include contents of subdirectories", Visibility = items.Any(i => i.IsDirectory) ? Visibility.Visible : Visibility.Collapsed };

        var body = new StackPanel { Spacing = 4, MinWidth = 380 };
        body.Children.Add(Text(items.Count == 1 ? items[0].Name : $"{items.Count} selected items", bold: true));
        foreach (var b in boxes)
            body.Children.Add(b);
        body.Children.Add(changeTime);
        var when = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(28, 0, 0, 0) };
        when.Children.Add(date);
        when.Children.Add(time);
        body.Children.Add(when);
        body.Children.Add(subdirs);

        ContentDialog dialog = NewDialog("Change Attributes", body);
        dialog.PrimaryButtonText = "OK";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        if (await ShowAsync(dialog) != ContentDialogResult.Primary)
            return null;

        FileAttributes set = 0, clear = 0;
        foreach (var b in boxes)
        {
            var flag = (FileAttributes)b.Tag;
            if (b.IsChecked == true) set |= flag;
            else if (b.IsChecked == false) clear |= flag;
        }
        DateTime? newTime = null;
        if (changeTime.IsChecked == true && date.Date is DateTimeOffset d)
            newTime = d.Date + time.Time;
        return new AttributeChange
        {
            Set = set,
            Clear = clear,
            LastWriteTime = newTime,
            IncludeSubdirectories = subdirs.IsChecked == true,
        };
    }

    // ---- change case (Ctrl+F7) ---------------------------------------------------------------

    public async Task<(CaseMode Mode, CasePart Part, bool Recursive)?> ChangeCaseAsync(bool hasDirectories)
    {
        var mode = new RadioButtons { Header = "Change to" };
        mode.Items.Add("lower case");
        mode.Items.Add("UPPER CASE");
        mode.Items.Add("Capitalize first letter");
        mode.Items.Add("Capitalize Each Word");
        mode.SelectedIndex = 0;
        var part = new RadioButtons { Header = "Apply to" };
        part.Items.Add("Whole name");
        part.Items.Add("Name only");
        part.Items.Add("Extension only");
        part.SelectedIndex = 0;
        var subdirs = new CheckBox { Content = "Include contents of subdirectories", Visibility = hasDirectories ? Visibility.Visible : Visibility.Collapsed };

        var body = new StackPanel { Spacing = 12, MinWidth = 360 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 32 };
        row.Children.Add(mode);
        row.Children.Add(part);
        body.Children.Add(row);
        body.Children.Add(subdirs);

        ContentDialog dialog = NewDialog("Change Case", body);
        dialog.PrimaryButtonText = "OK";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        if (await ShowAsync(dialog) != ContentDialogResult.Primary)
            return null;
        return ((CaseMode)Math.Max(0, mode.SelectedIndex), (CasePart)Math.Max(0, part.SelectedIndex), subdirs.IsChecked == true);
    }

    // ---- masks: select / unselect (Num + / Num -) and filter (Ctrl+F12) ----------------------

    public async Task<(string Mask, bool IncludeDirectories)?> MaskAsync(string title, string initialMask, bool includeDirectories)
    {
        var box = new TextBox { Text = initialMask, MinWidth = 380, PlaceholderText = "*.txt;*.doc|*.bak" };
        var dirs = new CheckBox { Content = "Also directories", IsChecked = includeDirectories };
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(Text("Mask (';' separates masks, '|' starts excluded masks):"));
        body.Children.Add(box);
        body.Children.Add(dirs);

        ContentDialog dialog = NewDialog(title, body);
        dialog.PrimaryButtonText = "OK";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        bool enter = false;
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                enter = true;
                e.Handled = true;
                dialog.Hide();
            }
        };
        dialog.Opened += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        ContentDialogResult result = await ShowAsync(dialog);
        if (!enter && result != ContentDialogResult.Primary)
            return null;
        return (string.IsNullOrWhiteSpace(box.Text) ? "*.*" : box.Text.Trim(), dirs.IsChecked == true);
    }

    /// <summary>Filter for the panel. Returns the new mask, "" to remove the filter, or null when cancelled.</summary>
    public async Task<string?> FilterAsync(string currentMask)
    {
        var box = new TextBox { Text = string.IsNullOrEmpty(currentMask) ? "*.*" : currentMask, MinWidth = 380 };
        var presets = new ComboBox { PlaceholderText = "Common filters", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (string p in new[] { "*.*", "*.txt;*.md;*.log", "*.jpg;*.jpeg;*.png;*.gif;*.webp;*.bmp", "*.mp3;*.flac;*.wav;*.m4a", "*.mp4;*.mkv;*.avi;*.mov",
                                     "*.doc;*.docx;*.xls;*.xlsx;*.ppt;*.pptx;*.pdf", "*.zip;*.7z;*.rar;*.tar;*.gz", "*.exe;*.msi;*.bat;*.cmd;*.ps1" })
            presets.Items.Add(p);
        presets.SelectionChanged += (_, _) => { if (presets.SelectedItem is string s) box.Text = s; };

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(Text("Show only files matching (';' separates masks, '|' starts excluded masks):"));
        body.Children.Add(box);
        body.Children.Add(presets);

        ContentDialog dialog = NewDialog("Filter", body);
        dialog.PrimaryButtonText = "Apply";
        dialog.SecondaryButtonText = "Remove Filter";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        bool enter = false;
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                enter = true;
                e.Handled = true;
                dialog.Hide();
            }
        };
        dialog.Opened += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        ContentDialogResult result = await ShowAsync(dialog);
        if (result == ContentDialogResult.Secondary)
            return string.Empty;
        if (!enter && result != ContentDialogResult.Primary)
            return null;
        string mask = box.Text.Trim();
        return new WildcardMask(mask).MatchesEverything ? string.Empty : mask;
    }
}
