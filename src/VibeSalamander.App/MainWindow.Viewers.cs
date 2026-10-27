// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Xaml;
using VibeSalamander.Services;
using VibeSalamander.Views.Viewer;

namespace VibeSalamander;

// MainWindow: the built-in viewer (F3) and editor (F4) windows.
public sealed partial class MainWindow
{
    private readonly List<Window> _viewers = new();
    private readonly List<Window> _editors = new();

    public void OpenViewer(string path, IReadOnlyList<string> siblings)
    {
        var viewer = new ViewerWindow(_vm, path, siblings, RootGrid.ActualTheme);
        _viewers.Add(viewer);
        viewer.Closed += (_, _) =>
        {
            _viewers.Remove(viewer);
            MemoryCleanup.Schedule(); // a large file's pictures, pages and index are let go
        };
        viewer.Activate();
    }

    public void OpenEditor(string path, Func<string, Task>? afterSave = null)
    {
        var editor = new EditorWindow(_vm, path, afterSave, RootGrid.ActualTheme);
        _editors.Add(editor);
        editor.Closed += (_, _) =>
        {
            _editors.Remove(editor);
            MemoryCleanup.Schedule();
        };
        editor.Activate();
    }

    /// <summary>At exit: the viewers close with the main window (editors stay: they may hold unsaved work).</summary>
    private void CloseViewers()
    {
        foreach (Window viewer in _viewers.ToList())
            viewer.Close();
    }
}
