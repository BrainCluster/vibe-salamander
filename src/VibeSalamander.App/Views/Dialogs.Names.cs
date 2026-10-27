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

// Dialogs: change attributes (Ctrl+F2), change case (Ctrl+F7), select / unselect / filter masks.
public sealed partial class Dialogs
{
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
        body.Children.Add(Text(items.Count == 1 ? items[0].Name : Services.Loc.F("{0} selected items", items.Count), bold: true));
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
        mode.Items.Add(Services.Loc.T("lower case"));
        mode.Items.Add(Services.Loc.T("UPPER CASE"));
        mode.Items.Add(Services.Loc.T("Capitalize first letter"));
        mode.Items.Add(Services.Loc.T("Capitalize Each Word"));
        mode.SelectedIndex = 0;
        var part = new RadioButtons { Header = "Apply to" };
        part.Items.Add(Services.Loc.T("Whole name"));
        part.Items.Add(Services.Loc.T("Name only"));
        part.Items.Add(Services.Loc.T("Extension only"));
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

    /// <summary>One line of text (a new directory's name, ...); null when cancelled or empty.</summary>
    public async Task<string?> InputAsync(string title, string prompt, string initial = "")
    {
        var box = new TextBox { Text = initial, MinWidth = 380, IsSpellCheckEnabled = false };
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(Text(prompt));
        body.Children.Add(box);
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
        if ((!enter && result != ContentDialogResult.Primary) || string.IsNullOrWhiteSpace(box.Text))
            return null;
        return box.Text.Trim();
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
