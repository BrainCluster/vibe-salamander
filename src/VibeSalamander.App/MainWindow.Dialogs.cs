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
        StackPanel body = AboutContent.Build();
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
