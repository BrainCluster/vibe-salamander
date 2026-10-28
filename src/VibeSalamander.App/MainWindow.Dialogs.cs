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

// MainWindow: dialogs and notifications (the host services of the view model).
public sealed partial class MainWindow
{
    // ---- dialogs -----------------------------------------------------------------------------

    public async Task<string?> PromptAsync(string title, string label, string initialText, string primaryButton, bool selectNameWithoutExtension = false)
    {
        var box = new TextBox { Text = initialText, MinWidth = 420 };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(box);

        ContentDialog dialog = Dialogs.Create(title, content);
        dialog.PrimaryButtonText = primaryButton;
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        bool accepted = false;
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                accepted = true;
                e.Handled = true;
                dialog.Hide();
            }
        };
        dialog.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            int dot = box.Text.LastIndexOf('.');
            if (selectNameWithoutExtension && dot > 0)
                box.Select(0, dot);
            else
                box.SelectAll();
        };

        try
        {
            ContentDialogResult result = await Dialogs.ShowDialogAsync(dialog);
            accepted |= result == ContentDialogResult.Primary;
        }
        finally
        {
            PaneView(_vm.Active).FocusList();
        }
        return accepted ? box.Text : null;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string primaryButton)
    {
        ContentDialog dialog = Dialogs.Create(title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        dialog.PrimaryButtonText = primaryButton;
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        try
        {
            return await Dialogs.ShowDialogAsync(dialog) == ContentDialogResult.Primary;
        }
        finally
        {
            PaneView(_vm.Active).FocusList();
        }
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        ContentDialog dialog = Dialogs.Create(title, new ScrollViewer
        {
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontFamily = new FontFamily("Cascadia Mono,Consolas") },
            MaxHeight = 480,
        });
        dialog.CloseButtonText = "OK";
        dialog.DefaultButton = ContentDialogButton.Close;
        await Dialogs.ShowDialogAsync(dialog);
    }

    public async void ShowAbout()
    {
        Version? version = typeof(MainWindow).Assembly.GetName().Version;
        var body = new StackPanel { Spacing = 10, MaxWidth = 460 };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        header.Children.Add(new Image
        {
            Width = 56,
            Height = 56,
            Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "VibeSalamander.png"))),
        });
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = "Vibe Salamander", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Tag = "NoLoc" });
        name.Children.Add(new TextBlock { Text = Loc.F("Version {0}", version == null ? "0.1" : $"{version.Major}.{version.Minor}.{version.Build}"), Opacity = 0.7, Tag = "NoLoc" });
        header.Children.Add(name);
        body.Children.Add(header);

        TextBlock Para(string text, double opacity = 1) =>
            new() { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Opacity = opacity, Tag = "NoLoc" };
        HyperlinkButton Link(string text, string url) =>
            new() { Content = text, NavigateUri = new Uri(url), Padding = new Thickness(0), Tag = "NoLoc" };

        body.Children.Add(Para(Loc.T("A modern spiritual successor to Altap / Open Salamander: dual-pane, keyboard-first, fast and precise, built with C# and WinUI 3.")));
        body.Children.Add(Para(Loc.T("Written by Claude (Anthropic's AI model), with BrainCluster as its guide.")));
        body.Children.Add(Para("Copyright © 2026 Vibe Salamander Authors", 0.8));
        body.Children.Add(Para(Loc.T("This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version. It comes with ABSOLUTELY NO WARRANTY."), 0.8));
        body.Children.Add(Link(Loc.T("GNU General Public License v3"), "https://www.gnu.org/licenses/gpl-3.0.html"));

        // Open Salamander: the program this one follows, and the source of the icon
        body.Children.Add(new TextBlock { Text = Loc.T("Credits"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0), Tag = "NoLoc" });
        body.Children.Add(Para(Loc.T("Vibe Salamander is inspired by Servant / Altap / Open Salamander, created by Petr Šolín and developed by Altap, Fine and the Open Salamander authors. The application icon is based on the Open Salamander icon (© 2023 Open Salamander Authors, GPL-2.0-or-later), recolored.")));
        body.Children.Add(Para("Open Salamander: David Andrš, Lukáš Cerman, Jakub Červený, Tomáš Jelínek, Milan Kaše, Tomáš Kopal, " +
                               "Jan Patera, Martin Přikryl, Juraj Rojko, Jan Ryšavý, Petr Šolín", 0.8));
        body.Children.Add(Link("Open Salamander", "https://github.com/OpenSalamander/salamander"));
        body.Children.Add(Para(Loc.T("Built with .NET, the Windows App SDK (WinUI 3) and SharpCompress (MIT licenses). See LICENSE.txt and CREDITS.md next to the program."), 0.8));
        body.Children.Add(Para(Loc.T("Settings:") + " " + SettingsStore.FilePath, 0.6));
        body.Children.Add(Link("Vibe Salamander", "https://github.com/BrainCluster/vibe-salamander"));

        ContentDialog dialog = Dialogs.Create("About Vibe Salamander", new ScrollViewer { Content = body, MaxHeight = 520 });
        dialog.CloseButtonText = "OK";
        dialog.DefaultButton = ContentDialogButton.Close;
        await Dialogs.ShowDialogAsync(dialog);
    }

    /// <summary>Another start of Vibe Salamander (single instance): show this window.</summary>
    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
        SetForegroundWindow(WindowHandle);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    public async void Exit() => await CloseAfterConfirmAsync();

    // ---- phase 2 host services -----------------------------------------------------------------

    public Dialogs Dialogs { get; }

    // ---- notifications (instead of message dialogs) -------------------------------------------

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _notificationTimer;

    public void Notify(string message, bool isError = true) => Notify(message, isError, log: true);

    /// <param name="log">Errors go to the error log too (Help → Error Log); false when it is already logged.</param>
    public void Notify(string message, bool isError, bool log)
    {
        if (isError && log)
            Core.Diagnostics.ErrorLog.Error("Shown", message);
        Notification.Severity = isError ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
        Notification.Title = isError ? Loc.T("Couldn't do that") : string.Empty;
        Notification.Message = Loc.T(message);
        Notification.ActionButton = null;
        Notification.IsOpen = true;
        NotificationHost.Visibility = Visibility.Visible;
        if (_notificationTimer == null)
        {
            _notificationTimer = DispatcherQueue.CreateTimer();
            _notificationTimer.IsRepeating = false;
            _notificationTimer.Tick += (_, _) => HideNotification();
        }
        // long enough to read; longer messages stay a bit longer
        _notificationTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(4 + message.Length / 40.0, 5, 12));
        _notificationTimer.Stop();
        _notificationTimer.Start();
    }

    /// <summary>A message with a button (an update is available: "Update..."); it stays a little longer.</summary>
    public void Notify(string message, string actionText, Action action)
    {
        Notify(message, isError: false, log: false);
        var button = new Button { Content = Loc.T(actionText) };
        button.Click += (_, _) =>
        {
            HideNotification();
            action();
        };
        Notification.ActionButton = button;
        _notificationTimer!.Interval = TimeSpan.FromSeconds(20);
        _notificationTimer.Stop();
        _notificationTimer.Start();
    }

    private void HideNotification()
    {
        _notificationTimer?.Stop();
        NotificationHost.Visibility = Visibility.Collapsed;
    }

    private void Notification_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        HideNotification(); // Notify opens it again for the next message
        PaneView(_vm.Active).FocusList();
    }
}
