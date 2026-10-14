// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using VibeSalamander.ViewModels;

namespace VibeSalamander.Views;

/// <summary>
/// The transfer queue below the file panels (WinSCP-style): every copy, move and permanent delete
/// runs here in the background while the panels stay usable.
/// </summary>
public sealed partial class TransfersPanel : UserControl
{
    private TransfersViewModel? _vm;
    private bool _collapsed;

    public TransfersPanel()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the list is collapsed to its header or expanded again.</summary>
    public event EventHandler? CollapsedChanged;

    /// <summary>Raised after a button was clicked, so the window can give the focus back to the panels.</summary>
    public event EventHandler? Interacted;

    public bool IsCollapsed
    {
        get => _collapsed;
        set
        {
            if (_collapsed == value)
                return;
            _collapsed = value;
            List.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            CollapseGlyph.Glyph = value ? "" : ""; // chevron up / down
            ToolTipService.SetToolTip(CollapseButton, value ? "Expand" : "Collapse");
            CollapsedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private Storyboard? _countdown;

    /// <summary>Shows the thin accent bar on top that runs out while the panel waits to close by itself.</summary>
    public void StartCloseCountdown(TimeSpan duration)
    {
        _countdown?.Stop();
        var shrink = new DoubleAnimation { From = 1, To = 0, Duration = new Duration(duration) };
        Storyboard.SetTarget(shrink, CountdownScale);
        Storyboard.SetTargetProperty(shrink, "ScaleX");
        _countdown = new Storyboard();
        _countdown.Children.Add(shrink);
        CloseCountdown.Visibility = Visibility.Visible;
        _countdown.Begin();
    }

    /// <summary>Hides the countdown bar (a new transfer started, or the panel closed).</summary>
    public void StopCloseCountdown()
    {
        _countdown?.Stop();
        _countdown = null;
        CloseCountdown.Visibility = Visibility.Collapsed;
        CountdownScale.ScaleX = 1;
    }

    /// <summary>Height of the header alone (the collapsed panel).</summary>
    public const double HeaderHeight = 32;

    public void Initialize(TransfersViewModel vm)
    {
        _vm = vm;
        List.ItemsSource = vm.Items;
        vm.StateChanged += (_, _) => UpdateHeader();
        UpdateHeader();
    }

    private void UpdateHeader()
    {
        if (_vm == null)
            return;
        SummaryText.Text = _vm.SummaryText;
        ClearButton.IsEnabled = _vm.Items.Any(i => !i.IsActive);
        ClearAllButton.IsEnabled = _vm.HasItems;
    }

    private static TransferItemViewModel? ItemOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as TransferItemViewModel;

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        ItemOf(sender)?.Pause();
        Interacted?.Invoke(this, EventArgs.Empty);
    }

    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        ItemOf(sender)?.Resume();
        Interacted?.Invoke(this, EventArgs.Empty);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        ItemOf(sender)?.Cancel();
        Interacted?.Invoke(this, EventArgs.Empty);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
            _vm?.Remove(item);
        Interacted?.Invoke(this, EventArgs.Empty);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _vm?.ClearFinished();
        Interacted?.Invoke(this, EventArgs.Empty);
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        _vm?.ClearAll();
        Interacted?.Invoke(this, EventArgs.Empty);
    }

    private void Collapse_Click(object sender, RoutedEventArgs e)
    {
        IsCollapsed = !IsCollapsed;
        Interacted?.Invoke(this, EventArgs.Empty);
    }
}
