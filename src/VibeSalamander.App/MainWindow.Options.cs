// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using VibeSalamander.Core.Commands;
using VibeSalamander.Core.Panels;
using VibeSalamander.Core.Settings;
using VibeSalamander.Services;
using VibeSalamander.ViewModels;
using VibeSalamander.Views;

namespace VibeSalamander;

// MainWindow: theme, density, always on top, the bars.
public sealed partial class MainWindow
{
    // ---- options -----------------------------------------------------------------------------

    private static FontIcon CheckIcon(bool on) => new() { Glyph = on ? "\uE73A" : "\uE739" };

    private void UpdateMenuChecks()
    {
        // check boxes as icons: a check mark column would push every icon of the Options menu to the right
        HiddenFilesMenuItem.Icon = CheckIcon(_vm.Settings.ShowHidden);
        AlwaysOnTopMenuItem.Icon = CheckIcon(_vm.Settings.AlwaysOnTop);
        ThemeSystemItem.IsChecked = _vm.Settings.Theme == AppTheme.System;
        ThemeLightItem.IsChecked = _vm.Settings.Theme == AppTheme.Light;
        ThemeDarkItem.IsChecked = _vm.Settings.Theme == AppTheme.Dark;
        ShowToolbarItem.IsChecked = _vm.Settings.ShowTopToolbar;
        ShowHotPathBarItem.IsChecked = _vm.Settings.ShowHotPathBar;
        ShowUserMenuBarItem.IsChecked = _vm.Settings.ShowUserMenuBar;
        ShowDriveBarsItem.IsChecked = _vm.Settings.ShowDriveBars;
        ShowInfoLineItem.IsChecked = _vm.Settings.ShowInformationLine;
        ShowCommandLineItem.IsChecked = _vm.Settings.ShowCommandLine;
        ShowFKeyBarItem.IsChecked = _vm.Settings.ShowFunctionKeyBar;
        ShowTransfersItem.IsChecked = _vm.Settings.ShowTransfers;
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out AppTheme theme))
        {
            _vm.Settings.Theme = theme;
            ApplyThemeSetting();
        }
    }

    private void ApplyThemeSetting()
    {
        RootGrid.RequestedTheme = ThemeFor(_vm.Settings.Theme);
        OnThemeChanged();
    }

    private static ElementTheme ThemeFor(AppTheme theme) => theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    private void OnThemeChanged()
    {
        UiState.ApplyTheme(RootGrid.ActualTheme == ElementTheme.Dark);
        UpdateTitleBarButtons();
        _settingsWindow?.SetTheme(RootGrid.ActualTheme);
        _vm.Left.Rebuild();
        _vm.Right.Rebuild();
        LeftPane.ApplyOptions();
        RightPane.ApplyOptions();
        LeftPane.RefreshBindings();
        RightPane.RefreshBindings();
        UpdateMenuChecks();
    }

    private void UpdateTitleBarButtons()
    {
        var titleBar = AppWindow.TitleBar;
        bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        titleBar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x18, 0, 0, 0);
        titleBar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
    }

    public void ApplyDensity()
    {
        UiState.ApplyDensity(_vm.Settings.Density);
        // the font size follows the density: it is set on the rows' style (ApplyOptions)
        LeftPane.ApplyOptions();
        RightPane.ApplyOptions();
        _vm.Left.Rebuild();
        _vm.Right.Rebuild();
        UpdateMenuChecks();
    }

    public void ApplyAlwaysOnTop(bool onTop)
    {
        // setting the flag (even to its current value) moves the window to the top of the z-order,
        // which would put it over the configuration window on every settings change
        if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.IsAlwaysOnTop != onTop)
            presenter.IsAlwaysOnTop = onTop;
    }

    private void ShowBars_Click(object sender, RoutedEventArgs e)
    {
        AppSettings s = _vm.Settings;
        s.ShowTopToolbar = ShowToolbarItem.IsChecked;
        s.ShowDriveBars = ShowDriveBarsItem.IsChecked;
        s.ShowInformationLine = ShowInfoLineItem.IsChecked;
        s.ShowCommandLine = ShowCommandLineItem.IsChecked;
        s.ShowFunctionKeyBar = ShowFKeyBarItem.IsChecked;
        s.ShowTransfers = ShowTransfersItem.IsChecked;
        s.ShowHotPathBar = ShowHotPathBarItem.IsChecked;
        s.ShowUserMenuBar = ShowUserMenuBarItem.IsChecked;
        ApplyBars();
    }

    private void ApplyBars()
    {
        AppSettings s = _vm.Settings;
        ToolbarPanel.Visibility = s.ShowTopToolbar ? Visibility.Visible : Visibility.Collapsed;
        HotPathBar.Visibility = s.ShowHotPathBar && HotPathBar.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UserMenuBar.Visibility = s.ShowUserMenuBar && UserMenuBar.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TopToolbar.Visibility = ToolbarPanel.Visibility == Visibility.Visible || HotPathBar.Visibility == Visibility.Visible ||
                                UserMenuBar.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        CommandLineBar.Visibility = s.ShowCommandLine ? Visibility.Visible : Visibility.Collapsed;
        FKeyBar.Visibility = s.ShowFunctionKeyBar ? Visibility.Visible : Visibility.Collapsed;
        LeftPane.SetBarsVisibility(s.ShowDriveBars, s.ShowInformationLine);
        RightPane.SetBarsVisibility(s.ShowDriveBars, s.ShowInformationLine);
        UpdateTransfersLayout();
        UpdateMenuChecks();
    }
}
