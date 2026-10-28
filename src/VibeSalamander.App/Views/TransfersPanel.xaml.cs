// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using VibeSalamander.Core.Operations;
using VibeSalamander.Services;
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
        // the mouse over the panel or the keyboard focus in it holds the auto close
        PointerEntered += (_, _) => SetHold(pointer: true, focus: _focusInside);
        PointerExited += (_, _) => SetHold(pointer: false, focus: _focusInside);
        PointerCanceled += (_, _) => SetHold(pointer: false, focus: _focusInside);
        GotFocus += (_, _) => SetHold(_pointerInside, focus: true);
        LostFocus += (_, _) => SetHold(_pointerInside, focus: XamlRoot != null && IsInside(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject));
    }

    private bool _pointerInside;
    private bool _focusInside;

    /// <summary>True while the mouse is over the panel or it has the keyboard focus: it doesn't close by itself then.</summary>
    public bool IsHeld => _pointerInside || _focusInside;

    /// <summary>Raised when <see cref="IsHeld"/> changes.</summary>
    public event EventHandler? HoldChanged;

    private void SetHold(bool pointer, bool focus)
    {
        bool before = IsHeld;
        _pointerInside = pointer;
        _focusInside = focus;
        if (before != IsHeld)
            HoldChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool IsInside(DependencyObject? element)
    {
        for (DependencyObject? node = element; node != null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
            if (node == this)
                return true;
        return false;
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
        SharedLimitText.Text = _vm.SharedLimit > 0 ? Loc.F("Max {0}", SpeedLimits.Describe(_vm.SharedLimit)) : Loc.T("No limit");
        GraphsButton.IsChecked = _vm.ShowGraphs;
        ClearButton.IsEnabled = _vm.Items.Any(i => !i.IsActive);
        ClearAllButton.IsEnabled = _vm.HasItems;
    }

    private static TransferItemViewModel? ItemOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as TransferItemViewModel;

    /// <summary>The limit choices: no limit, the presets, a typed one; the current one checked.</summary>
    private MenuFlyout LimitMenu(long current, Action<long> set, Func<Task<long?>> ask)
    {
        var menu = new MenuFlyout();
        bool preset = false;
        foreach (long limit in SpeedLimits.Presets)
        {
            var item = new RadioMenuFlyoutItem { Text = SpeedLimits.Describe(limit), GroupName = "limit", IsChecked = limit == current };
            preset |= limit == current;
            long chosen = limit;
            item.Click += (_, _) =>
            {
                set(chosen);
                Interacted?.Invoke(this, EventArgs.Empty);
            };
            menu.Items.Add(item);
            if (limit == 0)
                menu.Items.Add(new MenuFlyoutSeparator());
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var other = new RadioMenuFlyoutItem
        {
            Text = preset ? Loc.T("Other...") : Loc.F("Other ({0})...", SpeedLimits.Describe(current)),
            GroupName = "limit",
            IsChecked = !preset,
        };
        other.Click += async (_, _) =>
        {
            if (await ask() is long typed)
                set(typed);
            Interacted?.Invoke(this, EventArgs.Empty);
        };
        menu.Items.Add(other);
        return menu;
    }

    private void Limit_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item || _vm == null || sender is not FrameworkElement button)
            return;
        TransfersViewModel vm = _vm;
        LimitMenu(item.Limit, limit => item.Limit = limit, () => vm.AskLimitAsync(item.Limit)).ShowAt(button);
    }

    private void SharedLimit_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null)
            return;
        TransfersViewModel vm = _vm;
        LimitMenu(vm.SharedLimit, limit =>
        {
            vm.SharedLimit = limit;
            UpdateHeader();
        }, () => vm.AskLimitAsync(vm.SharedLimit)).ShowAt(SharedLimitButton);
    }

    private void Graphs_Click(object sender, RoutedEventArgs e)
    {
        if (_vm != null)
            _vm.ShowGraphs = GraphsButton.IsChecked == true;
        Interacted?.Invoke(this, EventArgs.Empty);
    }

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
