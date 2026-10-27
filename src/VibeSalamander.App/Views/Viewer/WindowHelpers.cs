// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using VibeSalamander.Core.Settings;

namespace VibeSalamander.Views.Viewer;

/// <summary>Remembering where the viewer and editor windows were.</summary>
internal static class WindowPlacements
{
    /// <summary>Puts the window where it was last time (when that place is still on a screen), else a default size.</summary>
    public static void Restore(Window window, WindowPlacement placement, int defaultWidth, int defaultHeight)
    {
        AppWindow app = window.AppWindow;
        if (placement.Width >= 300 && placement.Height >= 200)
        {
            var rect = new Windows.Graphics.RectInt32(placement.Left, placement.Top, placement.Width, placement.Height);
            DisplayArea? area = DisplayArea.GetFromRect(rect, DisplayAreaFallback.None);
            if (area != null)
            {
                app.MoveAndResize(rect);
                if (placement.Maximized && app.Presenter is OverlappedPresenter maximizable)
                    maximizable.Maximize();
                return;
            }
        }
        app.Resize(new Windows.Graphics.SizeInt32(defaultWidth, defaultHeight));
    }

    public static void Save(Window window, WindowPlacement placement)
    {
        AppWindow app = window.AppWindow;
        bool maximized = app.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        placement.Maximized = maximized;
        if (maximized || app.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
            return; // the normal size stays what it was
        placement.Left = app.Position.X;
        placement.Top = app.Position.Y;
        placement.Width = app.Size.Width;
        placement.Height = app.Size.Height;
    }

    public static void SetIcon(Window window)
    {
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "VibeSalamander.ico");
        if (File.Exists(icon))
            window.AppWindow.SetIcon(icon);
    }
}

/// <summary>The web view (Microsoft Edge WebView2) the previews and the editor use: one environment for the app.</summary>
internal static class WebViews
{
    private static Task<CoreWebView2Environment>? _environment;

    /// <summary>The installed WebView2 runtime's version, or null when it isn't installed.</summary>
    public static string? RuntimeVersion()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static Task<CoreWebView2Environment> EnvironmentAsync() =>
        _environment ??= CoreWebView2Environment.CreateWithOptionsAsync(null, AppPaths.WebView, new CoreWebView2EnvironmentOptions()).AsTask();

    /// <summary>Prepares a web view: the shared environment, no developer tools, no status bar, no browser accelerator keys.</summary>
    public static async Task<CoreWebView2> PrepareAsync(Microsoft.UI.Xaml.Controls.WebView2 view, bool scripts)
    {
        CoreWebView2Environment environment = await EnvironmentAsync();
        await view.EnsureCoreWebView2Async(environment);
        CoreWebView2 core = view.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsZoomControlEnabled = true;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsScriptEnabled = scripts;
        return core;
    }

    /// <summary>The page the Microsoft Store / Microsoft offers the runtime on.</summary>
    public const string RuntimeDownload = "https://developer.microsoft.com/microsoft-edge/webview2/";
}
