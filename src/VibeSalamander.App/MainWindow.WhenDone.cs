// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VibeSalamander.Core.Interop;
using VibeSalamander.Services;

namespace VibeSalamander;

// MainWindow: the transfers' progress on the taskbar button, and what happens when they are all done.
public sealed partial class MainWindow
{
    private TaskbarProgress? _taskbarProgress;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _taskbarTimer;

    /// <summary>While transfers run, the taskbar button shows how far they are (paused: yellow, a question waiting: red).</summary>
    private void UpdateTaskbarProgress()
    {
        _taskbarProgress ??= new TaskbarProgress(WindowHandle);
        if (_taskbarTimer == null)
        {
            _taskbarTimer = DispatcherQueue.CreateTimer();
            _taskbarTimer.Interval = TimeSpan.FromMilliseconds(500);
            _taskbarTimer.Tick += (_, _) => UpdateTaskbarProgress();
        }
        var (state, percent) = _vm.Transfers.Overall;
        _taskbarProgress.Set(state, percent);
        if (state == TaskbarProgress.State.None)
            _taskbarTimer.Stop();
        else if (!_taskbarTimer.IsRunning)
            _taskbarTimer.Start();
    }

    /// <summary>A minute's countdown (Cancel stops it), then the action.</summary>
    private async Task RunWhenDoneAsync(WhenDone action)
    {
        const int Seconds = 60;
        int left = Seconds;
        var text = new TextBlock { Text = Loc.F(PowerActions.Countdown(action), left), TextWrapping = TextWrapping.Wrap };
        var bar = new ProgressBar { Maximum = Seconds, Value = Seconds, Margin = new Thickness(0, 12, 0, 0) };
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = Loc.T("All transfers have finished."), Margin = new Thickness(0, 0, 0, 6) });
        body.Children.Add(text);
        body.Children.Add(bar);
        ContentDialog dialog = Dialogs.Create("When done", body);
        dialog.PrimaryButtonText = Loc.T("Now");
        dialog.CloseButtonText = Loc.T("Cancel");
        dialog.DefaultButton = ContentDialogButton.Close;
        bool timeUp = false;
        Microsoft.UI.Dispatching.DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (_, _) =>
        {
            left--;
            text.Text = Loc.F(PowerActions.Countdown(action), left);
            bar.Value = left;
            if (left <= 0)
            {
                timeUp = true;
                timer.Stop();
                dialog.Hide();
            }
        };
        timer.Start();
        ContentDialogResult answer = await Dialogs.ShowDialogAsync(dialog);
        timer.Stop();
        if (!timeUp && answer != ContentDialogResult.Primary)
            return;
        if (action == WhenDone.CloseApp)
        {
            Exit();
            return;
        }
        _vm.SaveSettings();
        if (!PowerActions.Run(action))
            Notify(Loc.F("Windows didn't do it: {0}", Loc.T(PowerActions.Name(action))));
    }
}
