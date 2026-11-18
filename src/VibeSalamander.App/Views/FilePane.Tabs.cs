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
    private TabsViewModel? _tabs;

    private void InitializeTabs(TabsViewModel tabs)
    {
        _tabs = tabs;
        tabs.Items.CollectionChanged += (_, _) => RebuildTabs();
        tabs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TabsViewModel.RowVisibility))
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
        RebuildTabs();
    }

    /// <summary>The arrow at the end of the drive bar: down opens the row of tabs, up closes it.</summary>
    private void UpdateTabRowButton()
    {
        if (_tabs == null)
            return;
        bool open = _tabs.RowVisibility == Visibility.Visible;
        TabRowGlyph.Glyph = open ? "\uE70E" : "\uE70D";
        string tip = Loc.T(open ? "Hide tabs" : "Show tabs");
        ToolTipService.SetToolTip(TabRowButton, tip);
        AutomationProperties.SetName(TabRowButton, tip);
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

    private Button MakeTab(TabItemViewModel item)
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
            CanDrag = true,
            Tag = item,
        };
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
                BringShownTabIntoView();
            }
        };
        Restyle(button, item);
        return button;
    }

    /// <summary>The shown tab looks like the path line under it: accent-filled in the active panel.</summary>
    private void Restyle(Button button, TabItemViewModel item)
    {
        bool active = ViewModel?.IsActive == true;
        if (item.IsShown)
        {
            button.Background = (Brush)Application.Current.Resources[active ? "AccentFillColorDefaultBrush" : "VibeInactivePathBrush"];
            button.Foreground = PathForeground(active);
            AutomationProperties.SetItemStatus(button, "shown");
        }
        else
        {
            button.ClearValue(Control.BackgroundProperty);
            button.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            AutomationProperties.SetItemStatus(button, string.Empty);
        }
    }

    private void RestyleTabs()
    {
        foreach (UIElement child in TabStrip.Children)
            if (child is Button { Tag: TabItemViewModel item } button)
                Restyle(button, item);
    }

    private void BringShownTabIntoView()
    {
        if (_tabs == null)
            return;
        int shown = _tabs.List.ActiveIndex;
        if (shown >= 0 && shown < TabStrip.Children.Count && TabStrip.Children[shown] is FrameworkElement tab)
            DispatcherQueue.TryEnqueue(() => tab.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false }));
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
        }
        else if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Link;
            e.DragUIOverride.Caption = Loc.T("Open in new tab");
            e.Handled = true;
        }
    }

    private async void TabRow_Drop(object sender, DragEventArgs e)
    {
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
