// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Services;

namespace VibeSalamander.Views;

/// <summary>The contents of the drive information flyout (Ctrl+F1): name, fullness bar, file system, sizes, features.</summary>
internal static class DriveInformationView
{
    private static string DriveTypeName(DriveType type) => type switch
    {
        DriveType.Fixed => "Local disk",
        DriveType.Removable => "Removable disk",
        DriveType.CDRom => "CD/DVD drive",
        DriveType.Network => "Network drive",
        DriveType.Ram => "RAM disk",
        _ => "Drive",
    };

    public static StackPanel Build(DriveInformation info)
    {
        string title = info.Root.Length <= 3
            ? $"{(info.Label.Length > 0 ? info.Label : DriveTypeName(info.Type))} ({info.Root.TrimEnd('\\')})"
            : info.Root.TrimEnd('\\');
        var panel = new StackPanel { Spacing = 10, MinWidth = 320 };
        panel.Children.Add(new TextBlock { Text = title, Style = Application.Current.Resources["SubtitleTextBlockStyle"] as Style });
        if (info.TotalBytes > 0)
        {
            panel.Children.Add(new ProgressBar { Maximum = 100, Value = info.UsedPercent, Height = 6, MinHeight = 6, CornerRadius = new CornerRadius(3) });
        }
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 3 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Row(string label, string value)
        {
            int r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, Opacity = 0.7 };
            var v = new TextBlock { Text = value, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(l, r);
            Grid.SetRow(v, r);
            Grid.SetColumn(v, 1);
            grid.Children.Add(l);
            grid.Children.Add(v);
        }
        string Size(long bytes) => $"{Core.Formatting.Formats.ShortSize(bytes)}  ({Core.Formatting.Formats.ExactSize(bytes)} bytes)";
        Row("Type", DriveTypeName(info.Type));
        Row("File system", info.FileSystem.Length > 0 ? info.FileSystem : "-");
        if (info.SerialNumber != 0)
            Row("Serial number", info.SerialText);
        if (info.TotalBytes > 0)
        {
            Row("Capacity", Size(info.TotalBytes));
            Row("Used", $"{Size(info.TotalBytes - info.FreeBytes)},  {info.UsedPercent} %");
            Row("Free", Size(info.FreeBytes));
            if (info.AvailableBytes != info.FreeBytes)
                Row("Free for you", Size(info.AvailableBytes));
        }
        if (info.BytesPerCluster > 0)
            Row("Cluster size", Core.Formatting.Formats.ExactSize(info.BytesPerCluster) + " bytes");
        if (info.MaxComponentLength > 0)
            Row("Max name length", info.MaxComponentLength + " characters");
        Row("Supports", info.Features);
        panel.Children.Add(grid);

        Loc.Translate(panel);
        return panel;
    }
}
