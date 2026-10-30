// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.System;
using VibeSalamander.ViewModels;

namespace VibeSalamander.Views;

/// <summary>
/// The menus of lists (drives, opened files, working directories, hot paths, histories, the user menu): always 50
/// characters wide and at most 10 rows high, scrolling beyond. A label too long for the width scrolls sideways while
/// its row is under the mouse or the keyboard, and its tooltip shows all of it. Up / Down, Enter, Escape, and a
/// letter jumps to the next row starting with it.
/// </summary>
public static class ListMenu
{
    public const int VisibleCharacters = 50;
    public const int VisibleRows = 10;
    private const double RowHeight = 32;
    private const double IconWidth = 30;
    private const double ScrollSpeed = 70; // pixels a second
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(0.9);

    // a mix of letters, capitals, digits and path characters: the width of an ordinary 50 characters
    private const string Sample = "C:\\Users\\Name\\Documents\\Projects 2026\\report_v12.md";

    /// <summary>
    /// The menu under the anchor. Its title is <paramref name="title"/>, or the header the entries start with
    /// (<see cref="MenuEntry.Header"/>). Every list menu has the same size: the title, then 10 rows of an icon,
    /// 50 characters and a shortcut.
    /// </summary>
    public static void Show(FrameworkElement anchor, string? title, IReadOnlyList<MenuEntry> entries, FlyoutShowOptions options, Action? closed = null,
        int characters = VisibleCharacters, string? widestDetail = null)
    {
        if (entries.Count > 0 && entries[0] is { IsSeparator: false, IsEnabled: false, Invoke: null } header)
        {
            title ??= header.Text;
            entries = entries.Skip(1).ToList();
        }
        double labelWidth = Measure(Sample[..Math.Min(characters, Sample.Length)], 14).Width;
        // room for the longest shortcut there is, so every menu has the same width; or for the details (the drive menu)
        double shortcutWidth = widestDetail != null
            ? entries.Where(e => !string.IsNullOrEmpty(e.Detail)).Select(e => Measure(e.Detail!, 11).Width)
                .Append(Measure(widestDetail, 11).Width).Max() + 16
            : entries.Where(e => !string.IsNullOrEmpty(e.Shortcut)).Select(e => Measure(e.Shortcut!, 12).Width)
                .Append(Measure("Ctrl+Shift+F12", 12).Width).Max() + 20;
        double width = IconWidth + labelWidth + shortcutWidth + 44;

        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            MaxHeight = VisibleRows * RowHeight + 8, // fewer rows: shorter; never taller
            Padding = new Thickness(0, 4, 0, 4),
        };
        ScrollViewer.SetHorizontalScrollMode(list, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);

        var actions = new Dictionary<object, Action>();
        var rows = new List<(ListViewItem Item, string Text)>();
        foreach (MenuEntry entry in entries)
        {
            ListViewItem item = Row(entry, labelWidth, shortcutWidth);
            list.Items.Add(item);
            if (entry.IsSeparator)
                continue;
            rows.Add((item, entry.Text));
            if (entry.IsEnabled && entry.Invoke is { } invoke)
            {
                actions[item] = invoke;
                if (item.Content != null)
                    actions[item.Content] = invoke;
            }
        }

        var presenter = new Style(typeof(FlyoutPresenter));
        presenter.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 4000.0));
        presenter.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, 4000.0));
        presenter.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        presenter.Setters.Add(new Setter(ScrollViewer.VerticalScrollModeProperty, ScrollMode.Disabled));
        presenter.Setters.Add(new Setter(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled));
        var content = new Grid { Width = width };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(new TextBlock
        {
            Text = title ?? string.Empty,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 14,
            Height = RowHeight,
            Padding = new Thickness(16, 7, 16, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var divider = new Border { Height = 1, Margin = new Thickness(8, 0, 8, 0), Background = new SolidColorBrush(ColorHelper.FromArgb(0x50, 0x80, 0x80, 0x80)) };
        Grid.SetRow(divider, 1);
        content.Children.Add(divider);
        Grid.SetRow(list, 2);
        content.Children.Add(list);
        var flyout = new Flyout { Content = content, FlyoutPresenterStyle = presenter };

        Action? chosen = null;
        void Choose(object? clicked)
        {
            if (chosen != null || clicked == null || !actions.TryGetValue(clicked, out Action? action))
                return;
            chosen = action;
            flyout.Hide();
        }
        list.ItemClick += (_, e) => Choose(e.ClickedItem);
        list.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                Choose(list.SelectedItem);
                e.Handled = true;
            }
            else if (rows.Count > 0 && e.Key is >= VirtualKey.A and <= VirtualKey.Z or >= VirtualKey.Number0 and <= VirtualKey.Number9)
            {
                // the next row starting with the letter (C for the C: drive, a folder by its first letter)
                char letter = (char)e.Key;
                int start = rows.FindIndex(r => r.Item == list.SelectedItem);
                for (int step = 1; step <= rows.Count; step++)
                {
                    var (item, text) = rows[(Math.Max(start, -1) + step + rows.Count) % rows.Count];
                    if (item.IsEnabled && text.Length > 0 && char.ToUpperInvariant(text[0]) == letter)
                    {
                        list.SelectedItem = item;
                        list.ScrollIntoView(item);
                        item.Focus(FocusState.Keyboard);
                        break;
                    }
                }
                e.Handled = true;
            }
        };
        flyout.Opened += (_, _) =>
        {
            ListViewItem? first = rows.Select(r => r.Item).FirstOrDefault(i => i.IsEnabled);
            if (first == null)
            {
                list.Focus(FocusState.Programmatic);
                return;
            }
            list.SelectedItem = first;
            first.Focus(FocusState.Keyboard);
        };
        flyout.Closed += (_, _) =>
        {
            closed?.Invoke();
            chosen?.Invoke(); // after the menu has given the focus back (the action may open a dialog)
        };
        flyout.ShowAt(anchor, options);
    }

    private static Size Measure(string text, double fontSize)
    {
        var block = new TextBlock { Text = text, FontSize = fontSize };
        block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return block.DesiredSize;
    }

    private static ListViewItem Row(MenuEntry entry, double labelWidth, double shortcutWidth)
    {
        if (entry.IsSeparator)
        {
            return new ListViewItem
            {
                IsEnabled = false,
                IsTabStop = false,
                MinHeight = 9,
                Height = 9,
                Padding = new Thickness(12, 0, 12, 0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new Border
                {
                    Height = 1,
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(ColorHelper.FromArgb(0x50, 0x80, 0x80, 0x80)),
                },
            };
        }

        var grid = new Grid { ColumnSpacing = 0 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(IconWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(shortcutWidth) });
        if (entry.Glyph is { Length: > 0 } glyph)
            grid.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center });

        // the label lives on a clipped canvas, so it can be wider than its column and slide through it
        var label = new TextBlock { Text = entry.Text, TextWrapping = TextWrapping.NoWrap, FontSize = 14 };
        Size natural = Measure(entry.Text, 14);
        double height = Math.Max(20, Math.Ceiling(natural.Height));
        var canvas = new Canvas
        {
            Width = labelWidth,
            Height = height,
            VerticalAlignment = VerticalAlignment.Center,
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, labelWidth, height) },
        };
        canvas.Children.Add(label);
        Grid.SetColumn(canvas, 1);
        grid.Children.Add(canvas);

        if (!string.IsNullOrEmpty(entry.Shortcut))
        {
            var shortcut = new TextBlock
            {
                Text = entry.Shortcut,
                FontSize = 12,
                Opacity = 0.7,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(shortcut, 2);
            grid.Children.Add(shortcut);
        }
        else if (!string.IsNullOrEmpty(entry.Detail))
        {
            // a drive: "120 GB free of 476 GB" at the right
            var detail = new TextBlock
            {
                Text = entry.Detail,
                FontSize = 11,
                Opacity = 0.75,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(detail, 2);
            grid.Children.Add(detail);
        }

        var item = new ListViewItem
        {
            Content = grid,
            IsEnabled = entry.IsEnabled,
            MinHeight = RowHeight,
            Height = RowHeight,
            Padding = new Thickness(12, 0, 12, 0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(item, entry.Text);

        double overflow = natural.Width - labelWidth;
        if (overflow > 1)
        {
            label.Width = labelWidth;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTipService.SetToolTip(item, new ToolTip { Content = new TextBlock { Text = entry.Text, TextWrapping = TextWrapping.Wrap } });
            Slide(item, label, labelWidth, overflow);
        }
        return item;
    }

    /// <summary>Under the mouse or the keyboard, a long label slides to its end and back to the start, over and over.</summary>
    private static void Slide(ListViewItem item, TextBlock label, double labelWidth, double overflow)
    {
        var shift = new TranslateTransform();
        label.RenderTransform = shift;
        Storyboard? story = null;
        bool pointer = false, focus = false;

        void Start()
        {
            if (story != null)
                return;
            label.Width = double.NaN;
            label.TextTrimming = TextTrimming.None;
            double distance = overflow + 12;
            TimeSpan move = TimeSpan.FromSeconds(distance / ScrollSpeed);
            var frames = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            frames.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 0 });
            frames.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = Pause, Value = 0 });
            frames.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = Pause + move, Value = -distance });
            frames.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = Pause + move + Pause, Value = -distance });
            frames.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = Pause + move + Pause + TimeSpan.FromMilliseconds(1), Value = 0 });
            Storyboard.SetTarget(frames, shift);
            Storyboard.SetTargetProperty(frames, "X");
            story = new Storyboard();
            story.Children.Add(frames);
            story.Begin();
        }

        void Stop()
        {
            if (pointer || focus || story == null)
                return;
            story.Stop();
            story = null;
            shift.X = 0;
            label.Width = labelWidth;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
        }

        item.PointerEntered += (_, _) => { pointer = true; Start(); };
        item.PointerExited += (_, _) => { pointer = false; Stop(); };
        item.PointerCanceled += (_, _) => { pointer = false; Stop(); };
        item.GotFocus += (_, _) => { focus = true; Start(); };
        item.LostFocus += (_, _) => { focus = false; Stop(); };
    }
}
