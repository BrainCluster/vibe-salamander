// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;
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
                UpdateTabJoin();
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
        UpdateTabJoin();
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

    private void TabScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        UpdateTabScrollButtons();
        UpdateTabJoin();
    }

    /// <summary>
    /// The shown tab and the directory line under it are one shape, as in File Explorer: the tab's bottom corners
    /// are square and it sits on the line; the line's top left corner is square while the first tab is shown there.
    /// Without the directory line (Options → Show) the tabs are plain rounded buttons.
    /// </summary>
    internal void UpdateTabJoin()
    {
        bool joined = TabRow.Visibility == Visibility.Visible && DirectoryLine.Visibility == Visibility.Visible;
        DirectoryLine.Margin = new Thickness(4, joined ? 0 : 2, 4, 2);
        CornerRadius tabCorners = joined ? new CornerRadius(6, 6, 0, 0) : new CornerRadius(6);
        foreach (UIElement child in TabStrip.Children)
            if (child is Grid { Children.Count: > 0 } host && host.Children[0] is Button button)
                button.CornerRadius = tabCorners;
        bool firstShownAtStart = joined && _tabs?.List.ActiveIndex == 0 && TabScroller.HorizontalOffset < 1 &&
                                 TabsLeftButton.Visibility != Visibility.Visible;
        var lineCorners = new CornerRadius(firstShownAtStart ? 0 : 6, 6, 6, 6);
        DirectoryLine.CornerRadius = lineCorners;
        foreach (UIElement child in DirectoryLine.Children)
            if (child is Border border)
                border.CornerRadius = lineCorners;
    }

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
        UpdateTabJoin();
        BringShownTabIntoView();
    }

    /// <summary>
    /// A tab: its button, and over its upper right corner a close button while the pointer is on the shown tab.
    /// The host (a Grid, Tag = the tab) is what the row holds.
    /// </summary>
    private Grid MakeTab(TabItemViewModel item)
    {
        // the caption: at least ten characters wide, at most TabTitleMaxWidth; a longer one scrolls while the
        // pointer is on the tab (in a clipping canvas)
        var title = new TextBlock
        {
            Text = item.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
        var shift = new TranslateTransform();
        title.RenderTransform = shift;
        var titleBox = new Canvas { VerticalAlignment = VerticalAlignment.Center };
        titleBox.Children.Add(title);
        double fullWidth = 0;
        Storyboard? scrolling = null;
        void FitTitle()
        {
            title.Width = double.NaN;
            title.TextTrimming = TextTrimming.None;
            title.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            fullWidth = Math.Ceiling(title.DesiredSize.Width);
            double width = Math.Clamp(fullWidth, TenCharacters(title), TabTitleMaxWidth);
            double height = Math.Ceiling(title.DesiredSize.Height);
            titleBox.Width = width;
            titleBox.Height = height;
            titleBox.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, width, height) };
            title.Width = width;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        void Scroll(bool on)
        {
            scrolling?.Stop();
            scrolling = null;
            shift.X = 0;
            double distance = fullWidth - titleBox.Width;
            if (!on || distance < 1 || double.IsNaN(titleBox.Width))
            {
                title.Width = titleBox.Width;
                title.TextTrimming = TextTrimming.CharacterEllipsis;
                return;
            }
            title.TextTrimming = TextTrimming.None;
            title.Width = fullWidth;
            var move = new DoubleAnimation
            {
                From = 0,
                To = -distance,
                BeginTime = TimeSpan.FromMilliseconds(500),
                Duration = TimeSpan.FromSeconds(Math.Max(0.8, distance / 40.0)), // 40 pixels a second
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(move, shift);
            Storyboard.SetTargetProperty(move, "X");
            scrolling = new Storyboard { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            scrolling.Children.Add(move);
            scrolling.Begin();
        }
        title.Loaded += (_, _) => FitTitle();
        var lockIcon = new FontIcon
        {
            Glyph = "",
            FontFamily = (FontFamily)Application.Current.Resources["VibeIconFont"],
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = item.LockVisibility,
        };
        // the folder's icon, as the panels show it (Configuration → Appearance → Icons)
        var iconHolder = new Grid { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        SetTabIcon(iconHolder, item);
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(iconHolder);
        content.Children.Add(lockIcon);
        content.Children.Add(titleBox);
        var button = new Button
        {
            Content = content,
            Style = (Style)Application.Current.Resources["VibeToolButtonStyle"],
            Padding = new Thickness(12, 5, 12, 5),
            MinHeight = 30,
            BorderThickness = new Thickness(0), // the fill reaches the edges: no seam with the directory line
            CornerRadius = new CornerRadius(6, 6, 0, 0),
            VerticalAlignment = VerticalAlignment.Stretch,
            Tag = item,
        };
        var look = new TabLook();
        var closeFace = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(4),
            Child = new FontIcon
            {
                Glyph = "\uE711",
                FontFamily = (FontFamily)Application.Current.Resources["VibeIconFont"],
                FontSize = 11,
                Foreground = look.CloseGlyph,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var close = new Button
        {
            Content = closeFace,
            Style = (Style)Application.Current.Resources["VibeToolButtonStyle"],
            Width = 22,
            Height = 22,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0),
            Visibility = Visibility.Collapsed,
            IsTabStop = false,
        };
        // the tab's colors come from TabLook (Restyle sets them): the button's own hover colors are its brushes
        button.Foreground = look.Fore;
        button.Resources["ButtonBackgroundPointerOver"] = look.Hover;
        button.Resources["ButtonBackgroundPressed"] = look.Pressed;
        button.Resources["ButtonForegroundPointerOver"] = look.Fore;
        button.Resources["ButtonForegroundPressed"] = look.Fore;
        button.Resources["ButtonBorderBrushPointerOver"] = look.Outline;
        button.Resources["ButtonBorderBrushPressed"] = look.Outline;
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        close.Resources["ButtonBackgroundPointerOver"] = clear;
        close.Resources["ButtonBackgroundPressed"] = clear;
        close.Resources["ButtonBorderBrushPointerOver"] = clear;
        close.Resources["ButtonBorderBrushPressed"] = clear;
        close.AddHandler(PointerEnteredEvent, new PointerEventHandler((_, _) => closeFace.Background = look.CloseHover), handledEventsToo: true);
        close.AddHandler(PointerExitedEvent, new PointerEventHandler((_, _) => closeFace.Background = null), handledEventsToo: true);
        close.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => closeFace.Background = look.ClosePressed), handledEventsToo: true);
        close.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => closeFace.Background = look.CloseHover), handledEventsToo: true);
        _looks.AddOrUpdate(button, look);
        // Button fades its color (about 80 ms); the address bar under the tab changes at once: the tab must too,
        // or the two drift apart when the pointer passes quickly
        button.Loaded += (_, _) => NoColorFade(button);
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
        // every tab under the pointer has one (not the last tab: a panel keeps one)
        void ShowClose() => close.Visibility = hovered && (_tabs?.Items.Count ?? 0) > 1 ? Visibility.Visible : Visibility.Collapsed;
        // the shown tab under the pointer: the address bar under it takes the same lighter or dimmer color
        look.BarHovered = () => _barHovered;
        look.UpdateBar = () =>
        {
            if (item.IsShown)
                DirectoryLineHover.Background = hovered || _barHovered ? new SolidColorBrush(look.Overlay) : null;
        };
        void Hover(bool on)
        {
            if (hovered == on)
                return;
            hovered = on;
            look.Hovered = on;
            look.Apply(button); // the whole tab, the close button on it too: tinted while the pointer is anywhere on it
            ShowClose();
            look.UpdateBar();
            Scroll(on);
        }
        host.AddHandler(PointerEnteredEvent, new PointerEventHandler((_, _) => Hover(true)), handledEventsToo: true);
        // entered without an Entered (the pointer was there when the tab appeared)
        host.AddHandler(PointerMovedEvent, new PointerEventHandler((_, _) => Hover(true)), handledEventsToo: true);
        host.AddHandler(PointerExitedEvent, new PointerEventHandler((_, _) => Hover(false)), handledEventsToo: true);
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
                if (title.IsLoaded)
                {
                    FitTitle();
                    Scroll(hovered);
                }
                SetTabIcon(iconHolder, item); // another folder
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
                look.UpdateBar();
                UpdateTabJoin();
                BringShownTabIntoView();
            }
        };
        Restyle(button, item);
        return host;
    }

    /// <summary>
    /// The tab's folder icon: with Windows' icons the shell's (a drive's own icon for a drive), otherwise the
    /// Vibe Salamander glyph, in color or in the tab's text color, like the panels. This PC and servers get glyphs.
    /// </summary>
    private void SetTabIcon(Grid holder, TabItemViewModel item)
    {
        string path = item.Tab.Path;
        bool thisPc = path.Length == 0 || path == Core.FileSystem.ThisPcProvider.RootPath;
        bool remote = path.Contains("://", StringComparison.Ordinal);
        bool driveRoot = !thisPc && !remote && path.Length is 2 or 3 && path[1] == ':';
        var glyph = new FontIcon
        {
            Glyph = thisPc ? "\uE7F4" : remote ? "\uE774" : driveRoot ? "\uEDA2" : "\uE8B7",
            FontFamily = (FontFamily)Application.Current.Resources["VibeIconFont"],
            FontSize = 14,
        };
        Core.Settings.IconPack pack = UiState.IconPack;
        if (pack != Core.Settings.IconPack.FluentMonochrome && !thisPc && !remote && !driveRoot)
            glyph.Foreground = UiState.FolderBrush; // monochrome: the tab's own text color
        holder.Children.Clear();
        holder.Children.Add(glyph);
        if (pack != Core.Settings.IconPack.Windows || thisPc || remote || _main == null)
            return;
        string root = driveRoot ? path[..2] + "\\" : path;
        string name = Path.GetFileName(root.TrimEnd('\\'));
        var entry = new Core.FileSystem.FileEntry(name.Length > 0 ? name : root, root, true, 0, DateTime.Now, System.IO.FileAttributes.Directory,
            tag: driveRoot ? new Core.FileSystem.DriveEntryInfo { Root = root, Type = DriveType.Unknown } : null);
        _main.Icons.Request(entry, generic: root.StartsWith(@"\\", StringComparison.Ordinal), icon =>
        {
            if (icon == null || item.Tab.Path != path)
                return; // none, or the tab went elsewhere meanwhile
            holder.Children.Clear();
            holder.Children.Add(new Image { Source = icon, Width = 16, Height = 16 });
        });
    }

    /// <summary>Takes the fade out of a button's template (its presenter's BackgroundTransition): colors change at once.</summary>
    private static void NoColorFade(DependencyObject element)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(element, i);
            switch (child)
            {
                case ContentPresenter presenter:
                    presenter.BackgroundTransition = null;
                    break;
                case Border border:
                    border.BackgroundTransition = null;
                    break;
                case Grid grid:
                    grid.BackgroundTransition = null;
                    break;
            }
            if (child is not ContentPresenter)
                NoColorFade(child);
        }
    }

    /// <summary>The pointer is on the address bar: it and the shown tab take the hover tint together.</summary>
    private bool _barHovered;

    private void DirectoryLine_PointerEntered(object sender, PointerRoutedEventArgs e) => BarHover(true);

    private void DirectoryLine_PointerExited(object sender, PointerRoutedEventArgs e) => BarHover(false);

    private void BarHover(bool on)
    {
        if (_barHovered == on)
            return;
        _barHovered = on;
        foreach (UIElement child in TabStrip.Children)
            if (child is Grid { Tag: TabItemViewModel { IsShown: true } } host && host.Children[0] is Button button &&
                _looks.TryGetValue(button, out TabLook? look))
            {
                look.Apply(button);
                look.UpdateBar();
                return;
            }
        // no tabs built (yet): the bar alone, with the tint its color calls for
        var alone = new TabLook();
        alone.Paint(this, (Brush)Application.Current.Resources[ViewModel?.IsActive == true ? "AccentFillColorDefaultBrush" : "VibeInactivePathBrush"],
            PathForeground(ViewModel?.IsActive == true), null);
        DirectoryLineHover.Background = on ? new SolidColorBrush(alone.Overlay) : null;
    }

    /// <summary>The widest a tab's caption gets before it is cut short (and scrolls under the pointer).</summary>
    private const double TabTitleMaxWidth = 180;

    private double _tenCharacters;
    private double _tenCharactersSize;

    /// <summary>The narrowest a caption is: ten characters of its font.</summary>
    private double TenCharacters(TextBlock like)
    {
        if (_tenCharactersSize != like.FontSize)
        {
            var probe = new TextBlock { Text = "0123456789", FontSize = like.FontSize, FontFamily = like.FontFamily };
            probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            _tenCharacters = Math.Ceiling(probe.DesiredSize.Width);
            _tenCharactersSize = like.FontSize;
        }
        return _tenCharacters;
    }

    private readonly ConditionalWeakTable<Button, TabLook> _looks = new();

    /// <summary>
    /// A tab's colors. Hovering lays <see cref="Overlay"/> over the tab's fill (and the address bar under the
    /// shown tab): black for a bright fill, white for a dark one, so the accent turns a little dimmer or lighter.
    /// The close button's × and its hover face follow the same brightness.
    /// </summary>
    private sealed class TabLook
    {
        public SolidColorBrush Fore { get; } = new();
        public SolidColorBrush Hover { get; } = new();
        public SolidColorBrush Pressed { get; } = new();
        public SolidColorBrush Outline { get; } = new(Microsoft.UI.Colors.Transparent);
        public SolidColorBrush CloseGlyph { get; } = new();
        public SolidColorBrush CloseHover { get; } = new();
        public SolidColorBrush ClosePressed { get; } = new();
        public Color Overlay { get; private set; }
        public Action UpdateBar { get; set; } = () => { };

        /// <summary>The pointer is on the tab (its button or the close button on it).</summary>
        public bool Hovered { get; set; }

        /// <summary>The shown tab: one shape with the address bar, so the pointer on the bar tints it too.</summary>
        public bool Shown { get; set; }

        public Func<bool> BarHovered { get; set; } = () => false;

        /// <summary>The tab's own fill, not hovered.</summary>
        public Brush? Fill { get; private set; }

        /// <summary>The fill for now: tinted while hovered (set on the button itself, so the close button doesn't take it away).</summary>
        public void Apply(Button button)
        {
            if (Fill != null)
                button.Background = Hovered || (Shown && BarHovered()) ? Hover : Fill;
        }

        public void Paint(FilePane pane, Brush fill, Brush text, Brush? outline)
        {
            Fill = fill;
            Color baseColor = fill is SolidColorBrush solid ? solid.Color : Microsoft.UI.Colors.Transparent;
            Color page = pane.ActualTheme == ElementTheme.Dark ? Color.FromArgb(255, 32, 32, 32) : Microsoft.UI.Colors.White;
            bool bright = TabColors.IsBright(TabColors.Over(page, baseColor));
            Overlay = bright ? Color.FromArgb(0x16, 0, 0, 0) : Color.FromArgb(0x24, 255, 255, 255);
            Hover.Color = TabColors.Over(baseColor, Overlay);
            Pressed.Color = TabColors.Over(baseColor, TabColors.Stronger(Overlay, 1.7));
            Fore.Color = text is SolidColorBrush t ? t.Color : bright ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;
            Outline.Color = outline is SolidColorBrush o ? o.Color : Microsoft.UI.Colors.Transparent;
            CloseGlyph.Color = bright ? Color.FromArgb(0xE4, 0, 0, 0) : Microsoft.UI.Colors.White;
            // the close button under the pointer: the tab's own, untinted color on the tinted tab; pressed, a step further
            Color plain = TabColors.Over(page, baseColor);
            CloseHover.Color = plain;
            ClosePressed.Color = TabColors.Over(plain, TabColors.Stronger(Overlay, 2.4));
            UpdateBar();
        }
    }

    /// <summary>The shown tab looks like the path line under it: accent-filled in the active panel.</summary>
    private void Restyle(Button button, TabItemViewModel item)
    {
        bool active = ViewModel?.IsActive == true;
        TabLook? look = _looks.TryGetValue(button, out TabLook? found) ? found : null;
        if (item.IsShown)
        {
            button.Background = (Brush)Application.Current.Resources[active ? "AccentFillColorDefaultBrush" : "VibeInactivePathBrush"];
            if (look != null)
                look.Shown = true;
            look?.Paint(this, button.Background, PathForeground(active), null);
            look?.Apply(button);
            button.Padding = new Thickness(12, 5, 32, 5); // room for the close button
            if (button.Parent is Grid { Children.Count: > 1 } shownHost && shownHost.Children[1] is Button shownClose)
                shownClose.Margin = new Thickness(0, 0, 5, 0);
            button.Margin = new Thickness(0);
            button.MinHeight = 30;
            button.BorderThickness = new Thickness(0);
            AutomationProperties.SetItemStatus(button, "shown");
        }
        else
        {
            // behind the shown tab, as in File Explorer: a lower, duller shape with a fine outline
            button.Background = (Brush)Application.Current.Resources["VibeBackTabBrush"];
            button.BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
            if (look != null)
                look.Shown = false;
            look?.Paint(this, button.Background, (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], button.BorderBrush);
            look?.Apply(button);
            button.BorderThickness = new Thickness(1, 1, 1, 0);
            button.Margin = new Thickness(0, 3, 0, 0);
            button.MinHeight = 27;
            button.Padding = new Thickness(11, 3, 30, 4); // room for the close button
            if (button.Parent is Grid { Children.Count: > 1 } backHost && backHost.Children[1] is Button backClose)
                backClose.Margin = new Thickness(0, 3, 5, 0); // centered on the lower tab
            AutomationProperties.SetItemStatus(button, string.Empty);
        }
    }

    private void RestyleTabs()
    {
        UpdateTabJoin();
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
            if (shown == TabStrip.Children.Count - 1)
                TabScroller.ChangeView(TabScroller.ScrollableWidth, null, null, disableAnimation: true); // the last: with the + after it
            else if (left < offset)
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
