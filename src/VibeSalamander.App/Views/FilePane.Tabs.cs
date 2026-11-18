// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using VibeSalamander.Core.Commands;
using VibeSalamander.Services;
using VibeSalamander.ViewModels;

namespace VibeSalamander.Views;

// FilePane: the row of tabs above the path line. A click shows a tab, a middle click closes it, a right click has
// the tab's menu; tabs are dragged along the row to reorder them, onto the other panel's row to move them there
// (copy with Ctrl); a folder dropped on the row opens in a new tab; a double click on the empty row makes a new tab.
public sealed partial class FilePane
{
    private const string TabFormat = "VibeSalamander.Tab";
    private const string TabSideKey = "VibeSalamander.TabSide";

    /// <summary>A tab dragged from the other panel onto this one's files (its row may be hidden): it moves here.</summary>
    private bool AcceptsTabFromOtherSide(DragEventArgs e, out bool copy)
    {
        copy = (e.Modifiers & Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Control) != 0;
        return e.DataView.Contains(TabFormat) && e.DataView.Properties.TryGetValue(TabSideKey, out object? side) &&
               side is string name && name != SideName;
    }

    private async Task DropTabFromOtherSideAsync(DragEventArgs e, bool copy)
    {
        if (_tabs == null || _main == null || ViewModel == null)
            return;
        if (await e.DataView.GetDataAsync(TabFormat) is string what && what.Split(':') is [string side, string number] &&
            int.TryParse(number, out int from))
        {
            PaneViewModel source = side == "Left" ? _main.Left : _main.Right;
            await _main.MoveTabToOtherSideAsync(source, from, null, copy);
        }
        _main.Activate(ViewModel);
        FocusList();
    }
    private TabsViewModel? _tabs;

    private void InitializeTabs(TabsViewModel tabs)
    {
        _tabs = tabs;
        tabs.Items.CollectionChanged += (_, _) => RebuildTabs();
        tabs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TabsViewModel.RowVisibility) or nameof(TabsViewModel.ArrowVisibility))
            {
                TabRow.Visibility = tabs.RowVisibility;
                UpdateTabRowButton();
            }
        };
        if (ViewModel != null)
            ViewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PaneViewModel.IsActive))
                    RestyleTabs();
            };
        TabRow.Visibility = tabs.RowVisibility;
        UpdateTabRowButton();
        AutomationProperties.SetName(TabsLeftButton, Loc.T("Earlier tabs"));
        AutomationProperties.SetName(TabsRightButton, Loc.T("Later tabs"));
        ToolTipService.SetToolTip(TabsLeftButton, Loc.T("Earlier tabs"));
        ToolTipService.SetToolTip(TabsRightButton, Loc.T("Later tabs"));
        RebuildTabs();
    }

    /// <summary>The arrow at the end of the drive bar: down opens the row of tabs, up closes it.</summary>
    private void UpdateTabRowButton()
    {
        if (_tabs == null)
            return;
        TabRowButton.Visibility = _tabs.ArrowVisibility;
        bool open = _tabs.RowVisibility == Visibility.Visible;
        TabRowGlyph.Glyph = open ? "\uE70E" : "\uE70D";
        string tip = Loc.T(open ? "Hide tabs" : "Show tabs");
        // closed with several tabs: a badge with their number, so hidden tabs aren't forgotten
        bool badge = !open && _tabs.Count > 1;
        TabCountText.Text = _tabs.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        TabCountText.Visibility = badge ? Visibility.Visible : Visibility.Collapsed;
        if (badge)
        {
            // the arrow becomes a badge: accent pill, the number before the arrow
            TabRowPill.Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            TabRowPill.Padding = new Thickness(6, 1, 5, 1);
            var onAccent = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"];
            TabCountText.Foreground = onAccent;
            TabRowGlyph.Foreground = onAccent;
            tip = Loc.F("Show tabs ({0} open)", _tabs.Count);
        }
        else
        {
            TabRowPill.ClearValue(Border.BackgroundProperty);
            TabRowPill.Padding = new Thickness(0);
            TabCountText.ClearValue(TextBlock.ForegroundProperty);
            TabRowGlyph.ClearValue(IconElement.ForegroundProperty);
        }
        string? keys = _main?.KeyMap.GetShortcutText(CommandId.ToggleTabRow);
        ToolTipService.SetToolTip(TabRowButton, string.IsNullOrEmpty(keys) ? tip : $"{tip} ({keys})");
        AutomationProperties.SetName(TabRowButton, tip);
    }

    // ---- scrolling a full row -----------------------------------------------------------------------------

    /// <summary>The arrows at both ends of the row: there only when the tabs don't fit; greyed at either end.</summary>
    private void UpdateTabScrollButtons()
    {
        bool overflow = TabScroller.ScrollableWidth > 0.5;
        TabsLeftButton.Visibility = TabsRightButton.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        TabsLeftButton.IsEnabled = TabScroller.HorizontalOffset > 0.5;
        TabsRightButton.IsEnabled = TabScroller.HorizontalOffset < TabScroller.ScrollableWidth - 0.5;
    }

    private void TabScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => UpdateTabScrollButtons();

    private void TabScroller_SizeChanged(object sender, SizeChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(UpdateTabScrollButtons); // after the scroller has measured the new size

    private void TabsLeftButton_Click(object sender, RoutedEventArgs e) => ScrollTabs(-1);

    private void TabsRightButton_Click(object sender, RoutedEventArgs e) => ScrollTabs(+1);

    /// <summary>
    /// Scrolls to the next tab that is cut off on that side (so every click shows one more tab whole); holding the
    /// arrow repeats.
    /// </summary>
    private void ScrollTabs(int direction)
    {
        double offset = TabScroller.HorizontalOffset;
        double width = TabScroller.ViewportWidth;
        double target = direction > 0 ? Math.Min(offset + 120, TabScroller.ScrollableWidth) : Math.Max(offset - 120, 0);
        foreach (UIElement child in TabStrip.Children)
        {
            if (child is not FrameworkElement tab)
                continue;
            double left = tab.TransformToVisual(TabStrip).TransformPoint(default).X;
            double right = left + tab.ActualWidth;
            if (direction > 0 && right > offset + width + 0.5)
            {
                target = Math.Min(right - width, TabScroller.ScrollableWidth); // the first tab cut off on the right, whole
                break;
            }
            if (direction < 0 && left < offset - 0.5)
                target = left; // the last tab cut off on the left, whole
        }
        TabScroller.ChangeView(target, null, null, disableAnimation: true);
    }

    private void TabRowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_main == null || ViewModel == null)
            return;
        _main.Activate(ViewModel);
        _tabs?.ToggleRow();
        FocusList();
    }

    /// <summary>The tab buttons, one per tab, in the list's order.</summary>
    private void RebuildTabs()
    {
        if (_tabs == null)
            return;
        TabStrip.Children.Clear();
        for (int i = 0; i < _tabs.Items.Count; i++)
            TabStrip.Children.Add(MakeTab(_tabs.Items[i]));
        TabRow.Visibility = _tabs.RowVisibility;
        UpdateTabRowButton();
        BringShownTabIntoView();
    }

    /// <summary>
    /// A tab: its button, and over its upper right corner a close button while the pointer is on the shown tab.
    /// The host (a Grid, Tag = the tab) is what the row holds.
    /// </summary>
    private Grid MakeTab(TabItemViewModel item)
    {
        var title = new TextBlock
        {
            Text = item.Title,
            MaxWidth = 180,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var lockIcon = new FontIcon
        {
            Glyph = "",
            FontFamily = (FontFamily)Application.Current.Resources["VibeIconFont"],
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = item.LockVisibility,
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        content.Children.Add(lockIcon);
        content.Children.Add(title);
        var button = new Button
        {
            Content = content,
            Style = (Style)Application.Current.Resources["VibeToolButtonStyle"],
            Padding = new Thickness(10, 3, 10, 3),
            MinHeight = 24,
            CornerRadius = new CornerRadius(4),
            Tag = item,
        };
        var close = new Button
        {
            Content = new FontIcon
            {
                Glyph = "\uE711",
                FontFamily = (FontFamily)Application.Current.Resources["VibeIconFont"],
                FontSize = 8,
            },
            Style = (Style)Application.Current.Resources["VibeToolButtonStyle"],
            Width = 16,
            Height = 16,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 1, 0),
            Visibility = Visibility.Collapsed,
            IsTabStop = false,
        };
        AutomationProperties.SetAutomationId(close, "PaneTabClose");
        AutomationProperties.SetName(close, Loc.T("Close tab"));
        ToolTipService.SetToolTip(close, Loc.T("Close tab"));
        close.Click += async (_, _) =>
        {
            if (_tabs == null || _main == null)
                return;
            _main.Activate(ViewModel!);
            await _tabs.CloseAsync(IndexOfTab(item));
            FocusList();
        };
        var host = new Grid { Tag = item };
        host.Children.Add(button);
        host.Children.Add(close);
        bool hovered = false;
        void ShowClose() => close.Visibility = hovered && item.IsShown && (_tabs?.Items.Count ?? 0) > 1 ? Visibility.Visible : Visibility.Collapsed;
        host.AddHandler(PointerEnteredEvent, new PointerEventHandler((_, _) => { hovered = true; ShowClose(); }), handledEventsToo: true);
        host.AddHandler(PointerExitedEvent, new PointerEventHandler((_, _) => { hovered = false; ShowClose(); }), handledEventsToo: true);
        // dragging: started by hand once the pointer moved a little (the button keeps the press to itself)
        Windows.Foundation.Point? pressedAt = null;
        button.AddHandler(PointerPressedEvent, new PointerEventHandler((s, e) =>
        {
            Microsoft.UI.Input.PointerPoint point = e.GetCurrentPoint(host);
            pressedAt = point.Properties.IsLeftButtonPressed ? point.Position : null;
        }), handledEventsToo: true);
        button.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => pressedAt = null), handledEventsToo: true);
        button.AddHandler(PointerMovedEvent, new PointerEventHandler(async (s, e) =>
        {
            Microsoft.UI.Input.PointerPoint point = e.GetCurrentPoint(host);
            if (pressedAt is not { } start || !point.Properties.IsLeftButtonPressed)
                return;
            if (Math.Abs(point.Position.X - start.X) < 6 && Math.Abs(point.Position.Y - start.Y) < 6)
                return;
            pressedAt = null;
            try
            {
                await button.StartDragAsync(e.GetCurrentPoint(button));
            }
            catch (Exception)
            {
                // another drag is running
            }
        }), handledEventsToo: true);
        AutomationProperties.SetAutomationId(button, "PaneTab");
        AutomationProperties.SetName(button, item.Title);
        ToolTipService.SetToolTip(button, item.ToolTip);
        button.Click += Tab_Click;
        button.AddHandler(PointerPressedEvent, new PointerEventHandler(Tab_PointerPressed), handledEventsToo: true);
        button.ContextFlyout = new MenuFlyout();
        ((MenuFlyout)button.ContextFlyout).Opening += (s, _) => FillTabMenu((MenuFlyout)s!, item);
        button.DragStarting += (_, e) =>
        {
            e.Data.SetData(TabFormat, $"{SideName}:{IndexOfTab(item)}");
            e.Data.Properties[TabSideKey] = SideName; // read while dragging (DragOver can't wait for the data)
            e.Data.RequestedOperation = DataPackageOperation.Move | DataPackageOperation.Copy;
        };
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TabItemViewModel.Title))
            {
                title.Text = item.Title;
                AutomationProperties.SetName(button, item.Title);
            }
            else if (e.PropertyName == nameof(TabItemViewModel.ToolTip))
                ToolTipService.SetToolTip(button, item.ToolTip);
            else if (e.PropertyName == nameof(TabItemViewModel.IsLocked))
                lockIcon.Visibility = item.LockVisibility;
            else if (e.PropertyName == nameof(TabItemViewModel.IsShown))
            {
                Restyle(button, item);
                ShowClose();
                BringShownTabIntoView();
            }
        };
        Restyle(button, item);
        return host;
    }

    /// <summary>The shown tab looks like the path line under it: accent-filled in the active panel.</summary>
    private void Restyle(Button button, TabItemViewModel item)
    {
        bool active = ViewModel?.IsActive == true;
        if (item.IsShown)
        {
            button.Background = (Brush)Application.Current.Resources[active ? "AccentFillColorDefaultBrush" : "VibeInactivePathBrush"];
            button.Foreground = PathForeground(active);
            button.Padding = new Thickness(10, 3, 20, 3); // room for the close button
            AutomationProperties.SetItemStatus(button, "shown");
        }
        else
        {
            button.ClearValue(Control.BackgroundProperty);
            button.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            button.Padding = new Thickness(10, 3, 10, 3);
            AutomationProperties.SetItemStatus(button, string.Empty);
        }
    }

    private void RestyleTabs()
    {
        foreach (UIElement child in TabStrip.Children)
            if (child is Grid { Tag: TabItemViewModel item } host && host.Children[0] is Button button)
                Restyle(button, item);
    }

    /// <summary>The shown tab whole in view (after the row has laid out the tabs it just got).</summary>
    private void BringShownTabIntoView()
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_tabs == null)
                return;
            int shown = _tabs.List.ActiveIndex;
            if (shown < 0 || shown >= TabStrip.Children.Count || TabStrip.Children[shown] is not FrameworkElement tab)
                return;
            TabScroller.UpdateLayout();
            double left = tab.TransformToVisual(TabStrip).TransformPoint(default).X;
            double right = left + tab.ActualWidth;
            double offset = TabScroller.HorizontalOffset;
            double width = TabScroller.ViewportWidth;
            if (left < offset)
                TabScroller.ChangeView(left, null, null, disableAnimation: true);
            else if (right > offset + width)
                TabScroller.ChangeView(Math.Min(right - width, TabScroller.ScrollableWidth), null, null, disableAnimation: true);
            UpdateTabScrollButtons();
        });
    }

    private string SideName => _main != null && ViewModel == _main.Left ? "Left" : "Right";

    private int IndexOfTab(TabItemViewModel item) => _tabs?.Items.IndexOf(item) ?? -1;

    // ---- clicks -----------------------------------------------------------------------------------------

    private async void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (_tabs == null || _main == null || sender is not Button { Tag: TabItemViewModel item })
            return;
        _main.Activate(ViewModel!);
        await _tabs.ShowAsync(IndexOfTab(item));
        FocusList();
    }

    private async void Tab_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_tabs == null || sender is not Button { Tag: TabItemViewModel item } ||
            !e.GetCurrentPoint((UIElement)sender).Properties.IsMiddleButtonPressed)
            return;
        e.Handled = true;
        await _tabs.CloseAsync(IndexOfTab(item)); // a middle click closes a tab, as in browsers
    }

    private async void NewTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (_main == null || ViewModel == null)
            return;
        _main.Activate(ViewModel);
        await _main.ExecuteAsync(CommandId.NewTab);
    }

    private async void TabRow_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // on the empty part of the row only (a double click on a tab is two clicks on it)
        if (e.OriginalSource is DependencyObject source && VisualTree.FindAncestor<Button>(source) != null)
            return;
        e.Handled = true;
        NewTabButton_Click(sender, e);
        await Task.CompletedTask;
    }

    /// <summary>A tab's right-click menu.</summary>
    private void FillTabMenu(MenuFlyout menu, TabItemViewModel item)
    {
        menu.Items.Clear();
        if (_tabs == null || _main == null || ViewModel == null)
            return;
        TabsViewModel tabs = _tabs;
        PaneViewModel pane = ViewModel;
        MainViewModel main = _main;
        MenuFlyoutItem Item(string text, string glyph, CommandId shortcutOf, Func<Task> run, bool enabled = true)
        {
            var entry = new MenuFlyoutItem
            {
                Text = Loc.T(text),
                Icon = new FontIcon { Glyph = glyph },
                IsEnabled = enabled,
                KeyboardAcceleratorTextOverride = main.KeyMap.GetShortcutText(shortcutOf) ?? string.Empty,
            };
            entry.Click += async (_, _) =>
            {
                main.Activate(pane);
                await run();
                FocusList();
            };
            return entry;
        }
        menu.Items.Add(Item("New Tab", "", CommandId.NewTab, () => tabs.NewAsync()));
        menu.Items.Add(Item("Copy to Other Panel", "", CommandId.CopyTabToOtherPanel,
            () => main.CopyTabToOtherPanelAsync(pane, IndexOfTab(item))));
        menu.Items.Add(Item(item.IsLocked ? "Unlock" : "Lock", item.IsLocked ? "" : "", CommandId.LockTab, () =>
        {
            tabs.ToggleLock(IndexOfTab(item));
            return Task.CompletedTask;
        }));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Close", "", CommandId.CloseTab, () => tabs.CloseAsync(IndexOfTab(item)), tabs.Count > 1));
        menu.Items.Add(Item("Close Other Tabs", "", CommandId.CloseOtherTabs, () => tabs.CloseOthersAsync(IndexOfTab(item)), tabs.Count > 1));
        menu.Items.Add(Item("Reopen Closed Tab", "", CommandId.ReopenClosedTab, () => tabs.ReopenAsync(), tabs.List.CanReopen));
    }

    // ---- dragging tabs, and folders onto the row ---------------------------------------------------------

    private void TabRow_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(TabFormat))
        {
            bool copy = (e.Modifiers & Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Control) != 0;
            e.AcceptedOperation = copy ? DataPackageOperation.Copy : DataPackageOperation.Move;
            e.DragUIOverride.Caption = Loc.T(copy ? "Copy tab here" : "Move tab here");
            e.Handled = true;
            ShowDropMarker(DropIndex(e));
        }
        else if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Link;
            e.DragUIOverride.Caption = Loc.T("Open in new tab");
            e.Handled = true;
        }
    }

    private void TabRow_DragLeave(object sender, DragEventArgs e) => TabDropMarker.Visibility = Visibility.Collapsed;

    /// <summary>The marker between the tabs where a dragged one lands.</summary>
    private void ShowDropMarker(int index)
    {
        double x = 0;
        if (TabStrip.Children.Count > 0)
        {
            bool atEnd = index >= TabStrip.Children.Count;
            var tab = (FrameworkElement)TabStrip.Children[atEnd ? TabStrip.Children.Count - 1 : index];
            double left = tab.TransformToVisual(TabStrip).TransformPoint(default).X;
            x = atEnd ? left + tab.ActualWidth : left - 2;
        }
        Canvas.SetLeft(TabDropMarker, Math.Max(0, x));
        TabDropMarker.Height = Math.Max(16, TabStrip.ActualHeight - 4);
        TabDropMarker.Visibility = Visibility.Visible;
    }

    private async void TabRow_Drop(object sender, DragEventArgs e)
    {
        TabDropMarker.Visibility = Visibility.Collapsed;
        if (_tabs == null || _main == null || ViewModel == null)
            return;
        e.Handled = true;
        DragOperationDeferral deferral = e.GetDeferral();
        try
        {
            int at = DropIndex(e);
            if (e.DataView.Contains(TabFormat) && await e.DataView.GetDataAsync(TabFormat) is string what &&
                what.Split(':') is [string side, string number] && int.TryParse(number, out int from))
            {
                bool copy = (e.Modifiers & Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Control) != 0;
                if (side == SideName)
                {
                    if (copy)
                        await _tabs.AddAsync(_tabs.Snapshot(from));
                    else
                        _tabs.Move(from, at > from ? at - 1 : at);
                }
                else
                {
                    PaneViewModel source = side == "Left" ? _main.Left : _main.Right;
                    await _main.MoveTabToOtherSideAsync(source, from, at, copy);
                }
            }
            else if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                foreach (IStorageItem dropped in await e.DataView.GetStorageItemsAsync())
                    if (dropped is StorageFolder folder)
                        await _tabs.NewAsync(folder.Path);
            }
            _main.Activate(ViewModel);
            FocusList();
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>Where on the row a drop lands: before the tab whose middle is right of the pointer.</summary>
    private int DropIndex(DragEventArgs e)
    {
        double x = e.GetPosition(TabStrip).X;
        for (int i = 0; i < TabStrip.Children.Count; i++)
        {
            if (TabStrip.Children[i] is not FrameworkElement tab)
                continue;
            double left = tab.TransformToVisual(TabStrip).TransformPoint(default).X;
            if (x < left + tab.ActualWidth / 2)
                return i;
        }
        return TabStrip.Children.Count;
    }
}
