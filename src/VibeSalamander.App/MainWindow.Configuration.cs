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

// MainWindow: Configuration: the window, applying, exporting, importing and resetting the settings, restarting.
public sealed partial class MainWindow
{
    // ---- configuration (phase 3C) --------------------------------------------------------------

    private SettingsWindow? _settingsWindow;

    public MainViewModel ViewModel => _vm;

    public void ShowConfiguration() => ShowConfiguration(null);

    public void ShowConfigurationPage(string page) => ShowConfiguration(page);

    private void ShowConfiguration(string? page)
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(this, RootGrid.ActualTheme, page);
            _settingsWindow.SetOwner(WindowHandle); // stays in front of the main window while open
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                _vm.SaveSettings();
                MemoryCleanup.Schedule(); // the closed window's controls and pages are let go
            };
        }
        else if (page != null)
        {
            _settingsWindow.ShowPage(page);
        }
        _settingsWindow.Activate();
    }

    private void ConfigurationPage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string page })
            ShowConfiguration(page);
    }

    /// <summary>Everything the settings window can change, applied to the running window.</summary>
    public void ApplySettings() => ApplySettings(displayChanged: false);

    /// <param name="displayChanged">Options that change how items look (names, sizes, colors, sorting, font) changed: rebuild the lists.</param>
    public void ApplySettings(bool displayChanged)
    {
        // theme and density rebuild both item lists: only when they changed (this runs on every settings change)
        bool densityChanged = UiState.Density != _vm.Settings.Density;
        if (displayChanged)
        {
            UiState.ApplyOptions(_vm.Settings);
            UiState.ApplyTheme(RootGrid.ActualTheme == ElementTheme.Dark);
        }
        if (densityChanged || displayChanged)
            UiState.ApplyDensity(_vm.Settings.Density);
        if (RootGrid.RequestedTheme != ThemeFor(_vm.Settings.Theme))
        {
            ApplyThemeSetting(); // rebuilds the lists
        }
        else if (densityChanged || displayChanged)
        {
            _vm.Left.Rebuild();
            _vm.Right.Rebuild();
        }
        if (displayChanged || densityChanged)
        {
            // the density changes the font size too, and the font is set on the rows' style
            LeftPane.ApplyOptions();
            RightPane.ApplyOptions();
            LeftPane.RefreshBindings();
            RightPane.RefreshBindings();
        }
        _vm.Transfers.StartWaiting(); // the number of parallel transfers may have changed
        UpdateTitle();
        ApplyAlwaysOnTop(_vm.Settings.AlwaysOnTop);
        BuildToolbar();
        BuildQuickBars();
        UpdateMenuShortcuts();
        _fkeyModifiers = (KeyModifiers)(-1);
        UpdateFunctionKeyBar(KeyModifiers.None);
        ApplyBars();
    }

    private void SaveConfiguration_Click(object sender, RoutedEventArgs e)
    {
        _vm.SaveSettings();
        Notify($"Configuration saved to {SettingsStore.FilePath}", isError: false);
    }

    private async void ExportConfiguration_Click(object sender, RoutedEventArgs e) => await ExportConfigurationAsync(AppWindow.Id);

    private async void ImportConfiguration_Click(object sender, RoutedEventArgs e) => await ImportConfigurationAsync(AppWindow.Id, Dialogs);

    public async Task ExportConfigurationAsync(Microsoft.UI.WindowId owner)
    {
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(owner)
            {
                SuggestedFileName = "VibeSalamander-settings",
            };
            picker.FileTypeChoices.Add("Vibe Salamander configuration", new List<string> { ".json" });
            var picked = await picker.PickSaveFileAsync();
            if (string.IsNullOrEmpty(picked?.Path))
                return;
            bool suppressed = _vm.SuppressSettingsSave;
            _vm.SuppressSettingsSave = false;
            _vm.SaveSettings(); // panels' current state into the settings
            _vm.SuppressSettingsSave = suppressed;
            SettingsStore.Export(_vm.Settings, picked.Path);
            Notify($"Configuration exported to {picked.Path}", isError: false);
        }
        catch (Exception ex)
        {
            Notify($"Export failed: {ex.Message}");
        }
    }

    public async Task<bool> ImportConfigurationAsync(Microsoft.UI.WindowId owner, Dialogs dialogs)
    {
        string? path;
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(owner);
            picker.FileTypeFilter.Add(".json");
            var picked = await picker.PickSingleFileAsync();
            path = picked?.Path;
        }
        catch (Exception ex)
        {
            Notify($"Import failed: {ex.Message}");
            return false;
        }
        if (string.IsNullOrEmpty(path))
            return false;

        AppSettings imported;
        try
        {
            imported = SettingsStore.ReadFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await dialogs.MessageAsync("Import Configuration", ex.Message);
            return false;
        }
        ContentDialog confirm = dialogs.Create("Import Configuration",
            new TextBlock { Text = "All current settings are replaced and Vibe Salamander restarts. Running transfers are cancelled.", TextWrapping = TextWrapping.Wrap });
        confirm.PrimaryButtonText = "Import and Restart";
        confirm.CloseButtonText = "Cancel";
        confirm.DefaultButton = ContentDialogButton.Primary;
        if (await dialogs.ShowDialogAsync(confirm) != ContentDialogResult.Primary)
            return false;
        SettingsStore.Save(imported);
        Restart();
        return true;
    }

    public async Task ResetConfigurationAsync(Dialogs dialogs)
    {
        ContentDialog confirm = dialogs.Create("Reset All Settings",
            new TextBlock { Text = "Hot paths, user menu, shortcuts, toolbar and all other settings go back to the defaults, and Vibe Salamander restarts.", TextWrapping = TextWrapping.Wrap });
        confirm.PrimaryButtonText = "Reset and Restart";
        confirm.CloseButtonText = "Cancel";
        confirm.DefaultButton = ContentDialogButton.Close;
        if (await dialogs.ShowDialogAsync(confirm) != ContentDialogResult.Primary)
            return;
        try
        {
            File.Delete(SettingsStore.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify($"The settings file could not be deleted: {ex.Message}");
            return;
        }
        Restart();
    }

    /// <summary>Saves the settings and restarts (Configuration → General → Language).</summary>
    public void RestartApp()
    {
        _vm.SaveSettings();
        Restart();
    }

    /// <summary>Starts a new instance and closes this one without saving its settings over the new ones.</summary>
    private void Restart()
    {
        _vm.SuppressSettingsSave = true;
        string? exe = Environment.ProcessPath;
        if (exe != null)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--restart") { UseShellExecute = true });
        _vm.Transfers.CancelAll();
        _closeConfirmed = true;
        _settingsWindow?.Close();
        Close();
    }
}
