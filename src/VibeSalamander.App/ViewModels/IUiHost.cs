// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using VibeSalamander.Core;
using VibeSalamander.Core.Commands;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Formatting;
using VibeSalamander.Core.Interop;
using VibeSalamander.Core.Operations;
using VibeSalamander.Core.Panels;
using VibeSalamander.Core.Settings;
using VibeSalamander.Core.Tools;
using VibeSalamander.Plugins;
using VibeSalamander.Services;

namespace VibeSalamander.ViewModels;

/// <summary>UI services the main view model needs from the window.</summary>
public interface IUiHost
{
    /// <summary>The user interface lent to plugins (windows, dialogs, clipboard).</summary>
    IPluginUi PluginUi { get; }

    IntPtr WindowHandle { get; }
    Task<string?> PromptAsync(string title, string label, string initialText, string primaryButton, bool selectNameWithoutExtension = false);
    Task<bool> ConfirmAsync(string title, string message, string primaryButton);
    Task ShowMessageAsync(string title, string message);
    void ShowDriveMenu(PaneViewModel pane);
    void FocusPane(PaneViewModel pane);
    void SetPanelMaximized(PaneViewModel? pane);
    void ApplyDensity();
    void ApplyAlwaysOnTop(bool onTop);
    void ShowAbout();
    void Exit();

    // phase 2
    Views.Dialogs Dialogs { get; }
    /// <summary>A short non-modal message (errors, hints) that disappears by itself, instead of a dialog.</summary>
    void Notify(string message, bool isError = true);
    /// <summary>Shift+F7: puts the pane's address bar into edit mode (no dialog).</summary>
    void EditPath(PaneViewModel pane);
    /// <summary>Ctrl+F1: volume details in a flyout at the pane's address bar.</summary>
    void ShowDriveInformation(PaneViewModel pane);
    void ShowFindWindow(string directory);

    /// <summary>The list of differences of Compare Directories, with Synchronize.</summary>
    void ShowCompareWindow(string left, string right, Core.Tools.CompareOptions options);

    /// <summary>Find Duplicates (Alt+Shift+F7), looking in <paramref name="directory"/>.</summary>
    void ShowDuplicatesWindow(string directory);

    /// <summary>The built-in viewer (F3) on a file; <paramref name="siblings"/> are the files the viewer can go through.</summary>
    void OpenViewer(string path, IReadOnlyList<string> siblings);

    /// <summary>The built-in editor (F4) on a file; <paramref name="afterSave"/> runs after each save (to send a copy back).</summary>
    void OpenEditor(string path, Func<string, Task>? afterSave = null);
    Task<string?> EditNameInPlaceAsync(PaneViewModel pane, FileItemViewModel item, bool selectWholeName = false);
    void ShowContextMenu(PaneViewModel pane);

    // phase 3C
    /// <summary>Options → Configuration: the settings window.</summary>
    void ShowConfiguration();

    // phase 4
    /// <summary>The configuration window at a page ("Plugins").</summary>
    void ShowConfigurationPage(string page);

    /// <summary>Brings the main window back in front (e.g. after Explorer opened a window).</summary>
    void BringToFront();

    // phase 3B
    /// <summary>Shows a command menu at the pane's caret item (atCaret) or at its address bar.</summary>
    void ShowMenu(PaneViewModel pane, IReadOnlyList<MenuEntry> entries, bool atCaret);
    /// <summary>The Explorer "New" menu for the pane's directory; true when an item was chosen.</summary>
    bool ShowNewMenu(PaneViewModel pane);
}
