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

/// <summary>Phase-2 dialogs built in code (ContentDialog based, Salamander wording).</summary>
public sealed partial class Dialogs
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

    /// <summary>The window the dialogs belong to (for file pickers).</summary>
    public Microsoft.UI.WindowId WindowId => _windowId();

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
            Services.Loc.Translate(dialog); // fixed texts and texts made from them ("Copy {0} to:")
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

    /// <param name="bold">Bold texts are names and paths: data, never translated.</param>
    private static TextBlock Text(string text, bool bold = false, bool secondary = false) => new()
    {
        Tag = bold ? "NoLoc" : null,
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
            Content = move ? "Don't show this dialog again (move straight into the other panel)" : "Don't show this dialog again (copy straight into the other panel)",
        };
        var hint = Text("Configuration → Confirmations → Copy / move target shows it again.", secondary: true);
        hint.FontSize = 12;
        hint.Visibility = Visibility.Collapsed;
        dontAsk.Checked += (_, _) => hint.Visibility = Visibility.Visible;
        dontAsk.Unchecked += (_, _) => hint.Visibility = Visibility.Collapsed;

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(Text(move ? $"Move {what} to:" : $"Copy {what} to:"));
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
}
