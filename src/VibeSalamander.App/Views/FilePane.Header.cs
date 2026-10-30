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
        var flyout = new Flyout { Content = DriveInformationView.Build(info), Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        flyout.Closed += (_, _) => FocusList();
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

    /// <summary>The hot paths as a menu (Shift+F9; Ctrl+D in Total Commander's shortcuts).</summary>
    private async void HotPathsButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null || _main == null)
            return;
        _main.Activate(ViewModel);
        _menuAnchor = HotPathsButton;
        await _main.ExecuteAsync(CommandId.HotPathList);
    }

    /// <summary>The recently opened files as a menu (Alt+F11).</summary>
    private async void OpenedFilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null || _main == null)
            return;
        _main.Activate(ViewModel);
        _menuAnchor = OpenedFilesButton;
        await _main.ExecuteAsync(CommandId.OpenedFilesList);
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
        Core.Settings.DriveOptions driveOptions = _main.Settings.Drives;
        var entries = new List<MenuEntry>();
        foreach (DriveButtonViewModel drive in ViewModel.VisibleDrives)
        {
            string free = drive.Drive.HasSize ? $"   {Core.Formatting.Formats.ShortSize(drive.Drive.FreeSpace)} free" : string.Empty;
            string root = drive.Drive.Root;
            entries.Add(new MenuEntry($"{root.TrimEnd('\\')}   {drive.Drive.Caption}{free}", async () => await ViewModel.ChangeDriveAsync(root), "\uEDA2"));
            if (drive.SeparatorAfter)
                entries.Add(MenuEntry.Separator);
        }
        entries.Add(MenuEntry.Separator);
        entries.Add(new MenuEntry(Loc.T("This PC"), async () => await ViewModel.NavigateAsync(ThisPcProvider.RootPath), "\uE7F4"));
        if (driveOptions.MenuShowDocuments)
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            entries.Add(new MenuEntry(Loc.T("Documents"), async () => await ViewModel.NavigateAsync(documents), "\uE8A5"));
        }
        if (driveOptions.MenuShowNetwork)
            entries.Add(new MenuEntry(Loc.T("Network"), () => Core.Interop.ShellOperations.OpenInExplorer("shell:NetworkPlacesFolder"), "\uE968"));
        if (driveOptions.MenuShowOtherPanelPath)
        {
            PaneViewModel other = _main.Left == ViewModel ? _main.Right : _main.Left;
            string otherPath = other.IsThisPc ? ThisPcProvider.RootPath : other.Path;
            entries.Add(new MenuEntry(Loc.T("As Other Panel"), async () => await ViewModel.NavigateAsync(otherPath), "\uE8AB", "Ctrl+."));
        }

        // the hot paths have their own button (next to the working directories) and Shift+F9
        ListMenu.Show(DriveMenuButton, entries, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft,
            ShowMode = Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowMode.Standard,
        }, FocusList);
    }

    private void DirectoryLine_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel == null || _main == null)
            return;
        _main.Activate(ViewModel);
        if (e.OriginalSource is DependencyObject source && (IsInside(source, DriveMenuButton) || IsInside(source, HotPathsButton) || IsInside(source, OpenedFilesButton) || IsInside(source, HistoryButton) || IsInside(source, MaximizeButton)))
            return;
        BeginPathEdit();
        e.Handled = true;
    }

    private static bool IsInside(DependencyObject element, DependencyObject container) => VisualTree.IsInside(element, container);
}
