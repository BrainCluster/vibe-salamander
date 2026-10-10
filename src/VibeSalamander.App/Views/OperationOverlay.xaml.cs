using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VibeSalamander.Core.Formatting;
using VibeSalamander.Core.Operations;

namespace VibeSalamander.Views;

/// <summary>Modal progress card shown over the window while a file operation runs (Esc or Cancel stops it).</summary>
public sealed partial class OperationOverlay : UserControl
{
    private CancellationTokenSource? _cts;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _showTimer;

    public OperationOverlay()
    {
        InitializeComponent();
        Visibility = Visibility.Collapsed;
    }

    public bool IsActive => _cts != null;

    /// <summary>Shows the overlay (fades in after a short delay so quick operations don't flash) and returns the cancellation token.</summary>
    public CancellationToken Begin(string title)
    {
        _cts = new CancellationTokenSource();
        TitleText.Text = title;
        SourceText.Text = "Preparing...";
        TargetText.Text = string.Empty;
        DetailsText.Text = string.Empty;
        SpeedText.Text = string.Empty;
        FileProgress.Value = 0;
        TotalProgress.Value = 0;
        TotalProgress.IsIndeterminate = true;
        CancelButton.IsEnabled = true;
        Opacity = 0;
        Visibility = Visibility.Visible; // blocks input immediately

        _showTimer ??= DispatcherQueue.CreateTimer();
        _showTimer.Interval = TimeSpan.FromMilliseconds(250);
        _showTimer.IsRepeating = false;
        _showTimer.Tick += ShowTimer_Tick;
        _showTimer.Start();
        CancelButton.Focus(FocusState.Programmatic);
        return _cts.Token;
    }

    private void ShowTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Tick -= ShowTimer_Tick;
        if (IsActive)
            Opacity = 1;
    }

    public void Update(OperationProgress p)
    {
        if (!IsActive)
            return;
        if (p.Analyzing)
        {
            SourceText.Text = "Reading directory tree: " + p.CurrentSource;
            TotalProgress.IsIndeterminate = true;
            return;
        }
        string verb = p.Kind switch { OperationKind.Move => "Moving", OperationKind.Delete => "Deleting", _ => "Copying" };
        SourceText.Text = string.IsNullOrEmpty(p.CurrentSource) ? verb + "..." : $"{verb}: {p.CurrentSource}";
        TargetText.Text = string.IsNullOrEmpty(p.CurrentTarget) ? string.Empty : "To: " + p.CurrentTarget;
        FileProgress.Visibility = p.Kind == OperationKind.Delete ? Visibility.Collapsed : Visibility.Visible;
        FileProgress.Value = p.CurrentFraction;
        TotalProgress.IsIndeterminate = false;
        TotalProgress.Value = p.TotalFraction;

        DetailsText.Text = p.Kind == OperationKind.Delete || p.TotalBytes == 0
            ? $"{p.ItemsDone:N0} of {p.ItemsTotal:N0} items"
            : $"{Formats.ShortSize(p.TotalBytesDone)} of {Formats.ShortSize(p.TotalBytes)}  ·  {p.ItemsDone:N0} of {p.ItemsTotal:N0} items";
        SpeedText.Text = p.BytesPerSecond > 0
            ? $"{Formats.ShortSize((long)p.BytesPerSecond)}/s" + (p.Remaining is TimeSpan r ? $"  ·  {FormatRemaining(r)} left" : string.Empty)
            : string.Empty;
    }

    private static string FormatRemaining(TimeSpan r) =>
        r.TotalHours >= 1 ? $"{(int)r.TotalHours}:{r.Minutes:00}:{r.Seconds:00}" : $"{r.Minutes}:{r.Seconds:00}";

    public void End()
    {
        _cts?.Dispose();
        _cts = null;
        if (_showTimer != null)
        {
            _showTimer.Stop();
            _showTimer.Tick -= ShowTimer_Tick;
        }
        Visibility = Visibility.Collapsed;
    }

    public void Cancel()
    {
        if (_cts == null)
            return;
        _cts.Cancel();
        CancelButton.IsEnabled = false;
        SourceText.Text = "Cancelling...";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancel();
}
