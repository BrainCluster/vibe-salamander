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

// Dialogs: the user menu editor (Commands → User Menu → Edit).
public sealed partial class Dialogs
{
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
}
