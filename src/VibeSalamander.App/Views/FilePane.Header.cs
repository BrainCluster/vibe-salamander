// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using VibeSalamander.Core.Commands;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Panels;
using VibeSalamander.Services;
using VibeSalamander.ViewModels;

namespace VibeSalamander.Views;

// FilePane: drive bar, directory line, header, panel options and drive information (Ctrl+F1).
public sealed partial class FilePane
{
    // ---- drive information (Ctrl+F1) ---------------------------------------------------------

    private static string DriveTypeName(DriveType type) => type switch
    {
        DriveType.Fixed => "Local disk",
        DriveType.Removable => "Removable disk",
        DriveType.CDRom => "CD/DVD drive",
        DriveType.Network => "Network drive",
        DriveType.Ram => "RAM disk",
        _ => "Drive",
    };

    /// <summary>Volume details of the panel's drive (or the focused drive in This PC) in a flyout at the address bar.</summary>
    public async void ShowDriveInformation()
    {
        if (ViewModel == null || _main == null)
            return;
        string? path = ViewModel.IsThisPc ? ViewModel.FocusedItem?.Drive?.Root : ViewModel.IsLocalDisk ? ViewModel.Path : null;
        if (path == null)
        {
            await _main.ShowErrorAsync("Focus a drive (or open a folder on it) to see its information.");
            return;
        }
        DriveInformation? info = await DriveInformation.QueryAsync(path);
        if (info == null)
        {
            await _main.ShowErrorAsync($"No information is available for {DriveInformation.RootOf(path) ?? path} (is the drive ready?).");
            return;
        }

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

        var flyout = new Flyout { Content = panel, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        flyout.Closed += (_, _) => FocusList();
        Loc.Translate(panel);
        flyout.ShowAt(DriveMenuButton);
    }

    // ---- header, drive bar, directory line --------------------------------------------------

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null || _main == null || sender is not FrameworkElement { Tag: string tag })
            return;
        _main.Activate(ViewModel);
        if (Enum.TryParse(tag, out SortMode mode))
            ViewModel.SetSort(mode);
        FocusList();
    }

    private async void ThisPcButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null || _main == null)
            return;
        _main.Activate(ViewModel);
        await ViewModel.NavigateAsync(ThisPcProvider.RootPath);
    }

    private async void DriveButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null || _main == null || (sender as FrameworkElement)?.DataContext is not DriveButtonViewModel drive)
            return;
        _main.Activate(ViewModel);
        await ViewModel.ChangeDriveAsync(drive.Drive.Root);
    }

    private void DriveMenuButton_Click(object sender, RoutedEventArgs e) => ShowDriveMenu();

    private async void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null || _main == null)
            return;
        _main.Activate(ViewModel);
        await _main.ExecuteAsync(CommandId.MaximizePanel);
        FocusList();
    }

    /// <summary>Applies Appearance / Colors / Drives options: font, colors, address bar buttons, drive bar.</summary>
    private string? _appliedFontKey;

    public void ApplyOptions()
    {
        if (ViewModel == null || _main == null)
            return;
        // the default ListViewItem style sets its own font, so the list's font isn't inherited by the rows:
        // the panel font goes into the item container style (a new style re-creates the rows)
        FileList.FontSize = UiState.FontSize;
        if (UiState.PanelFont is { } font)
            FileList.FontFamily = font;
        else
            FileList.ClearValue(Control.FontFamilyProperty);
        string fontKey = $"{UiState.PanelFont?.Source}|{UiState.FontSize}";
        if (fontKey != _appliedFontKey)
        {
            _appliedFontKey = fontKey;
            var rowStyle = new Style(typeof(ListViewItem))
            {
                BasedOn = (Style)Application.Current.Resources["VibeListItemStyle"],
            };
            rowStyle.Setters.Add(new Setter(Control.FontSizeProperty, UiState.FontSize));
            // room on the left for the selection indicator (the accent bar of the focused row) before the icon
            rowStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 0, 4, 0)));
            if (UiState.PanelFont is { } rowFont)
                rowStyle.Setters.Add(new Setter(Control.FontFamilyProperty, rowFont));
            FileList.ItemContainerStyle = rowStyle;
        }
        MaximizeButton.Visibility = _main.Settings.Appearance.MaximizeButtonInAddressBar ? Visibility.Visible : Visibility.Collapsed;

        // dark schemes (terminal styles) make the whole panel dark: address bar, header, drive bar
        RequestedTheme = UiState.PanelTheme; // Default = the window's theme
        // color scheme: panel background and the focused row (lightweight styling of the list items)
        if (UiState.PanelBackground is { } background)
            FileList.Background = background;
        else
            FileList.ClearValue(Control.BackgroundProperty);
        foreach (string key in new[] { "ListViewItemBackgroundSelected", "ListViewItemBackgroundSelectedPointerOver", "ListViewItemBackgroundSelectedPressed" })
        {
            if (UiState.FocusBackground is { } focus)
                FileList.Resources[key] = focus;
            else if (FileList.Resources.ContainsKey(key))
                FileList.Resources.Remove(key);
        }
        // re-template the rows so the new resources and the thumbnail size are used
        _appliedView = (ViewMode)(-1);
        ApplyViewMode();
        ViewModel.RefreshVisibleDrives();
        ViewModel.ApplyDriveOptions();
    }

    private async void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null || _main == null)
            return;
        _main.Activate(ViewModel);
        await _main.ExecuteAsync(CommandId.WorkingDirectoriesList);
    }

    /// <summary>Alt+F1 / Alt+F2 drive menu.</summary>
    public void ShowDriveMenu()
    {
        if (ViewModel == null || _main == null)
            return;
        var menu = new MenuFlyout();
        Core.Settings.DriveOptions driveOptions = _main.Settings.Drives;
        foreach (DriveButtonViewModel drive in ViewModel.VisibleDrives)
        {
            string free = drive.Drive.HasSize ? $"   {Core.Formatting.Formats.ShortSize(drive.Drive.FreeSpace)} free" : string.Empty;
            var item = new MenuFlyoutItem
            {
                Text = $"{drive.Drive.Root.TrimEnd('\\')}   {drive.Drive.Caption}{free}",
                Icon = new FontIcon { Glyph = "\uEDA2" },
            };
            string root = drive.Drive.Root;
            item.Click += async (_, _) => await ViewModel.ChangeDriveAsync(root);
            menu.Items.Add(item);
            if (drive.SeparatorAfter)
                menu.Items.Add(new MenuFlyoutSeparator());
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var thisPc = new MenuFlyoutItem { Text = Loc.T("This PC"), Icon = new FontIcon { Glyph = "\uE7F4" } };
        thisPc.Click += async (_, _) => await ViewModel.NavigateAsync(ThisPcProvider.RootPath);
        menu.Items.Add(thisPc);
        if (driveOptions.MenuShowDocuments)
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var docs = new MenuFlyoutItem { Text = Loc.T("Documents"), Icon = new FontIcon { Glyph = "\uE8A5" } };
            docs.Click += async (_, _) => await ViewModel.NavigateAsync(documents);
            menu.Items.Add(docs);
        }
        if (driveOptions.MenuShowNetwork)
        {
            var network = new MenuFlyoutItem { Text = Loc.T("Network"), Icon = new FontIcon { Glyph = "\uE968" } };
            network.Click += (_, _) => Core.Interop.ShellOperations.OpenInExplorer("shell:NetworkPlacesFolder");
            menu.Items.Add(network);
        }
        if (driveOptions.MenuShowOtherPanelPath)
        {
            PaneViewModel other = _main.Left == ViewModel ? _main.Right : _main.Left;
            string otherPath = other.IsThisPc ? ThisPcProvider.RootPath : other.Path;
            var asOther = new MenuFlyoutItem { Text = Loc.T("As Other Panel"), Icon = new FontIcon { Glyph = "\uE8AB" }, KeyboardAcceleratorTextOverride = "Ctrl+." };
            asOther.Click += async (_, _) => await ViewModel.NavigateAsync(otherPath);
            menu.Items.Add(asOther);
        }

        // hot paths (Ctrl+Shift+digit sets one, Ctrl+digit goes there)
        IList<string> hotPaths = _main.Settings.HotPaths;
        bool header = false;
        for (int i = 1; i < hotPaths.Count && i < 10 && driveOptions.MenuShowHotPaths; i++)
        {
            string hot = hotPaths[i];
            if (string.IsNullOrEmpty(hot))
                continue;
            if (!header)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                header = true;
            }
            string hotName = i < _main.Settings.HotPathNames.Count ? _main.Settings.HotPathNames[i] : string.Empty;
            var item = new MenuFlyoutItem
            {
                Text = hotName.Length > 0 ? hotName : hot == ThisPcProvider.RootPath ? Loc.T("This PC") : hot,
                Icon = new FontIcon { Glyph = "\uE734" },
                KeyboardAcceleratorTextOverride = "Ctrl+" + i,
            };
            item.Click += async (_, _) => await ViewModel.NavigateAsync(hot);
            menu.Items.Add(item);
        }
        menu.ShowAt(DriveMenuButton);
    }

    private void DirectoryLine_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel == null || _main == null)
            return;
        _main.Activate(ViewModel);
        if (e.OriginalSource is DependencyObject source && (IsInside(source, DriveMenuButton) || IsInside(source, HistoryButton) || IsInside(source, MaximizeButton)))
            return;
        BeginPathEdit();
        e.Handled = true;
    }

    /// <summary>Address bar in edit mode with the full location selected (empty-area click, Shift+F7).</summary>
    public void BeginPathEdit()
    {
        if (ViewModel == null)
            return;
        // the full location, ready to edit (Windows 11 Explorer: click the empty part of the address bar)
        PathEditor.Text = ViewModel.IsLocalDisk ? ViewModel.Path : ViewModel.IsThisPc ? Loc.T("This PC") : ViewModel.DisplayPath;
        BreadcrumbHost.Visibility = Visibility.Collapsed;
        PathEditor.Visibility = Visibility.Visible;
        // focus after menus/flyouts have closed and returned their focus (Shift+F7 from the menu), else the
        // editor loses the focus at once and closes
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (PathEditor.Visibility != Visibility.Visible)
                return;
            PathEditor.Focus(FocusState.Programmatic);
            PathEditor.SelectAll();
        });
    }

    private static bool IsInside(DependencyObject element, DependencyObject container)
    {
        for (DependencyObject? d = element; d != null; d = VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, container))
                return true;
        return false;
    }

    private async void PathEditor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel == null)
            return;
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            string text = PathEditor.Text;
            EndPathEdit();
            if (text.Trim().Equals("this pc", StringComparison.OrdinalIgnoreCase) || text.Trim().Equals(Loc.T("This PC"), StringComparison.CurrentCultureIgnoreCase))
                await ViewModel.NavigateAsync(ThisPcProvider.RootPath);
            else if (!string.IsNullOrWhiteSpace(text))
            {
                string target = PathUtil.Resolve(ViewModel.IsLocalDisk || ViewModel.IsArchive ? ViewModel.Path : @"C:\", text);
                // Confirmations → "The directory doesn't exist. Do you want to create it?" (not for a directory in an archive)
                if (_main != null && !Directory.Exists(target) && !File.Exists(target) && !_main.ArchiveProvider.CanHandle(target) &&
                    !await _main.CreateMissingDirectoryAsync(target))
                    return;
                await ViewModel.NavigateAsync(target);
            }
            FocusList();
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            EndPathEdit();
            FocusList();
        }
    }

    private void PathEditor_LostFocus(object sender, RoutedEventArgs e) => EndPathEdit();

    private void EndPathEdit()
    {
        PathEditor.Visibility = Visibility.Collapsed;
        BreadcrumbHost.Visibility = Visibility.Visible;
    }
}
