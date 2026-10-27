// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Panels;
using VibeSalamander.Services;
using VibeSalamander.ViewModels;
using VirtualKey = Windows.System.VirtualKey;

namespace VibeSalamander.Views;

/// <summary>
/// A panel's address bar, like Windows 11 Explorer's: "This PC", the drive and each folder as buttons, a chevron after
/// each with its subfolders, leading locations moved into a "..." menu when the path is too long, and an edit box for
/// typing a location (a click on the empty part, Shift+F7). Core's BreadcrumbRules has the decisions.
/// </summary>
internal sealed class AddressBar
{
    /// <summary>One location: its button and the chevron after it.</summary>
    private sealed record Crumb(string Label, string Path, Button Segment, Button Chevron);

    private readonly Panel _crumbsPanel;
    private readonly FrameworkElement _host;
    private readonly TextBox _editor;
    private readonly Func<PaneViewModel?> _pane;
    private readonly Func<MainViewModel?> _main;
    private readonly Func<bool, Brush> _foreground;
    private readonly Action _focusList;
    private readonly List<Crumb> _crumbs = new();
    private Button? _overflowButton;
    private (string Path, string Display, object Drives)? _key;

    /// <param name="crumbsPanel">The horizontal panel the location buttons go into.</param>
    /// <param name="host">What holds it (its width is what there is room for).</param>
    /// <param name="editor">The text box for typing a location.</param>
    /// <param name="foreground">The text color of the active (true) or inactive panel's address bar.</param>
    /// <param name="focusList">Gives the file list the keyboard back.</param>
    public AddressBar(Panel crumbsPanel, FrameworkElement host, TextBox editor, Func<PaneViewModel?> pane, Func<MainViewModel?> main,
        Func<bool, Brush> foreground, Action focusList)
    {
        _crumbsPanel = crumbsPanel;
        _host = host;
        _editor = editor;
        _pane = pane;
        _main = main;
        _foreground = foreground;
        _focusList = focusList;
    }

    // ---- the locations ----------------------------------------------------------------------------------------

    /// <summary>"This PC", the drive ("Local Disk (C:)") or share, then each folder of the path.</summary>
    private List<(string Label, string Path)> Segments(PaneViewModel pane, MainViewModel? main)
    {
        var segments = new List<(string, string)> { (Loc.T("This PC"), ThisPcProvider.RootPath) };
        if (pane.IsThisPc)
            return segments;
        if (pane.IsRemote && main != null)
        {
            // the server, then its directories: walked up through the plugin's parents
            var chain = new List<string>();
            string? step = pane.Path;
            while (step != null && step != ThisPcProvider.RootPath && chain.Count < 64)
            {
                chain.Add(step);
                step = main.RemoteProvider.GetParent(step)?.Path;
            }
            chain.Reverse();
            for (int i = 0; i < chain.Count; i++)
                segments.Add((i == 0 ? main.RemoteProvider.GetDisplayPath(chain[i]).TrimEnd('/') : RemoteFileSystemProvider.LastName(chain[i]), chain[i]));
            return segments;
        }
        if (!pane.IsLocalDisk && !pane.IsArchive)
        {
            segments.Add((pane.DisplayPath, pane.Path));
            return segments;
        }
        segments.AddRange(BreadcrumbRules.DiskSegments(pane.Path, drive =>
            pane.Drives.FirstOrDefault(d => d.Drive.Root.StartsWith(drive, StringComparison.OrdinalIgnoreCase))?.Drive.Caption));
        return segments;
    }

    private static Button CrumbButton(UIElement content, string? toolTip)
    {
        // like Explorer, the address bar doesn't take the keyboard focus away from the file list
        var button = new Button { Content = content, AllowFocusOnInteraction = false, IsTabStop = false };
        if (Application.Current.Resources.TryGetValue("VibeBreadcrumbButtonStyle", out object? style))
            button.Style = style as Style;
        if (toolTip != null)
            ToolTipService.SetToolTip(button, toolTip);
        return button;
    }

    /// <summary>Shows the panel's location (only rebuilds what changed).</summary>
    public void Rebuild()
    {
        if (_pane() is not { } pane)
            return;
        // PropertyChanged comes several times per navigation and on every refresh (DisplayPath); rebuilding
        // only on a real change keeps the buttons (and an open subfolder menu's anchor) alive
        var key = (pane.Path, pane.DisplayPath, (object)pane.Drives);
        if (_key == key)
            return;
        _key = key;
        Brush foreground = _foreground(pane.IsActive);

        // shown when the path is too long: the leading locations move into its menu (like Explorer)
        if (_overflowButton == null)
        {
            _overflowButton = CrumbButton(new FontIcon { Glyph = "\uE712", FontSize = 12, Foreground = foreground }, "More locations");
            _overflowButton.Visibility = Visibility.Collapsed;
            _overflowButton.Click += Overflow_Click;
            _crumbsPanel.Children.Insert(0, _overflowButton);
        }

        // going into a subfolder or back up keeps the buttons the paths share; only the rest is rebuilt
        var segments = Segments(pane, _main());
        int keep = 0;
        while (keep < _crumbs.Count && keep < segments.Count && _crumbs[keep].Label == segments[keep].Label &&
               string.Equals(_crumbs[keep].Path, segments[keep].Path, StringComparison.Ordinal))
            keep++;
        for (int i = _crumbs.Count - 1; i >= keep; i--)
        {
            _crumbsPanel.Children.Remove(_crumbs[i].Segment);
            _crumbsPanel.Children.Remove(_crumbs[i].Chevron);
            _crumbs.RemoveAt(i);
        }

        for (int i = keep; i < segments.Count; i++)
        {
            var (label, path) = segments[i];
            var text = new TextBlock
            {
                Text = label,
                Foreground = foreground,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 360, // a very long folder name is trimmed instead of pushing everything out
            };
            Button segment = CrumbButton(text, path == ThisPcProvider.RootPath ? Loc.T("This PC") : path);
            segment.Click += async (_, _) => await NavigateAsync(path);

            Button chevron = CrumbButton(new FontIcon { Glyph = "\uE76C", FontSize = 8, Foreground = foreground }, null);
            chevron.Padding = new Thickness(3, 4, 3, 4);
            chevron.Click += async (sender, _) =>
            {
                // the folder after this one in the current path (it is preselected in the menu)
                int index = _crumbs.FindIndex(c => c.Chevron == sender);
                string? next = index >= 0 && index + 1 < _crumbs.Count ? _crumbs[index + 1].Path : null;
                await ShowSubfoldersAsync((Button)sender, path, next);
            };

            _crumbsPanel.Children.Add(segment);
            _crumbsPanel.Children.Add(chevron);
            _crumbs.Add(new Crumb(label, path, segment, chevron));
        }

        // the current folder in bold
        for (int i = 0; i < _crumbs.Count; i++)
            if (_crumbs[i].Segment.Content is TextBlock labelText)
                labelText.FontWeight = i == _crumbs.Count - 1 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        UpdateOverflow();
    }

    /// <summary>Active/inactive or theme change: only the text color changes.</summary>
    public void UpdateForeground()
    {
        if (_pane() is not { } pane)
            return;
        Brush foreground = _foreground(pane.IsActive);
        if (_overflowButton?.Content is FontIcon more)
            more.Foreground = foreground;
        foreach (Crumb crumb in _crumbs)
        {
            if (crumb.Segment.Content is TextBlock text)
                text.Foreground = foreground;
            if (crumb.Chevron.Content is FontIcon chevron)
                chevron.Foreground = foreground;
        }
    }

    /// <summary>Hides leading locations (into the "..." menu) until the rest fits; the current folder always stays.</summary>
    public void UpdateOverflow()
    {
        if (_overflowButton == null || _crumbs.Count == 0)
            return;
        double available = _host.ActualWidth;
        if (available <= 0)
            return;
        var infinite = new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
        double Width(UIElement element)
        {
            element.Measure(infinite);
            return element.DesiredSize.Width;
        }

        var widths = new List<double>();
        foreach (Crumb crumb in _crumbs)
        {
            crumb.Segment.Visibility = Visibility.Visible;
            crumb.Chevron.Visibility = Visibility.Visible;
            widths.Add(Width(crumb.Segment) + Width(crumb.Chevron));
        }
        _overflowButton.Visibility = Visibility.Visible;
        int hidden = BreadcrumbRules.HiddenCount(widths, available, Width(_overflowButton));
        _overflowButton.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < hidden; i++)
        {
            _crumbs[i].Segment.Visibility = Visibility.Collapsed;
            _crumbs[i].Chevron.Visibility = Visibility.Collapsed;
        }
    }

    private void Overflow_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        foreach (Crumb crumb in _crumbs.Where(c => c.Segment.Visibility == Visibility.Collapsed).Reverse())
        {
            var item = new MenuFlyoutItem
            {
                Text = crumb.Label,
                Icon = new FontIcon { Glyph = crumb.Path == ThisPcProvider.RootPath ? "\uE7F4" : crumb.Path.Length <= 3 ? "\uEDA2" : "\uE8B7" },
            };
            string path = crumb.Path;
            item.Click += async (_, _) => await NavigateAsync(path);
            menu.Items.Add(item);
        }
        menu.ShowAt((FrameworkElement)sender);
    }

    private async Task NavigateAsync(string path)
    {
        if (_pane() is not { } pane || _main() is not { } main)
            return;
        main.Activate(pane);
        // going up focuses the folder we came from, like Backspace
        string? focus = (pane.IsLocalDisk || pane.IsArchive) && path != ThisPcProvider.RootPath ? BreadcrumbRules.ChildOnTheWay(path, pane.Path) : null;
        await pane.NavigateAsync(path, focus);
        _focusList();
    }

    /// <summary>The chevron after a location: its subfolders (drives after "This PC"), the next one on the path in bold.</summary>
    private async Task ShowSubfoldersAsync(Button anchor, string path, string? next)
    {
        if (_pane() is not { } pane || _main() is not { } main)
            return;
        main.Activate(pane);
        var menu = new MenuFlyout();
        if (path == ThisPcProvider.RootPath)
        {
            foreach (DriveButtonViewModel drive in pane.Drives)
            {
                string root = drive.Drive.Root;
                var item = new MenuFlyoutItem { Text = drive.Drive.Caption, Icon = new FontIcon { Glyph = "\uEDA2" } };
                if (next != null && root.StartsWith(next.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    item.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                item.Click += async (_, _) => await NavigateAsync(root);
                menu.Items.Add(item);
            }
        }
        else
        {
            bool showHidden = main.Settings.ShowHidden;
            // on a server: its directories, as the plugin lists them
            var remoteTargets = new Dictionary<string, string>(StringComparer.Ordinal);
            if (main.RemoteProvider.CanHandle(path))
            {
                DirectoryListing listing = await main.RemoteProvider.ListAsync(path, new ListingOptions { ShowHidden = showHidden }, CancellationToken.None);
                foreach (FileEntry entry in listing.Entries.Where(e => e.IsDirectory && !e.IsUpDir).OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).Take(500))
                    remoteTargets[entry.Name] = entry.FullPath;
            }
            List<string> folders = remoteTargets.Count > 0 || main.RemoteProvider.CanHandle(path)
                ? remoteTargets.Keys.ToList()
                : main.ArchiveProvider.CanHandle(path)
                ? await main.ArchiveProvider.ListDirectoriesAsync(path) // a directory inside an archive (or the archive itself)
                : await Task.Run(() => DiskSubfolders(path, showHidden));
            if (folders.Count == 0)
                menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("(no subfolders)"), IsEnabled = false });
            string? nextName = next == null ? null
                : remoteTargets.Count > 0 ? RemoteFileSystemProvider.LastName(next) : Path.GetFileName(next.TrimEnd('\\'));
            foreach (string name in folders)
            {
                string target = remoteTargets.TryGetValue(name, out string? remotePath) ? remotePath : Path.Combine(path, name);
                var item = new MenuFlyoutItem { Text = name, Icon = new FontIcon { Glyph = "\uE8B7" } };
                if (nextName != null && string.Equals(name, nextName, StringComparison.OrdinalIgnoreCase))
                    item.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                item.Click += async (_, _) => await NavigateAsync(target);
                menu.Items.Add(item);
            }
        }
        if (anchor.XamlRoot == null)
            return; // the address bar was rebuilt while the folders were read
        menu.ShowAt(anchor, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft,
        });
    }

    private static List<string> DiskSubfolders(string path, bool showHidden)
    {
        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = showHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
            };
            return Directory.EnumerateDirectories(path, "*", options)
                .Select(d => Path.GetFileName(d))
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                .Take(500)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            return new List<string>();
        }
    }

    // ---- typing a location ----------------------------------------------------------------------------------------

    /// <summary>The edit box with the full location selected (empty-area click, Shift+F7).</summary>
    public void BeginEdit()
    {
        if (_pane() is not { } pane)
            return;
        // the full location, ready to edit (Windows 11 Explorer: click the empty part of the address bar)
        _editor.Text = pane.IsLocalDisk ? pane.Path : pane.IsThisPc ? Loc.T("This PC") : pane.DisplayPath;
        _host.Visibility = Visibility.Collapsed;
        _editor.Visibility = Visibility.Visible;
        // focus after menus/flyouts have closed and returned their focus (Shift+F7 from the menu), else the
        // editor loses the focus at once and closes
        _editor.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_editor.Visibility != Visibility.Visible)
                return;
            _editor.Focus(FocusState.Programmatic);
            _editor.SelectAll();
        });
    }

    public async void EditorKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_pane() is not { } pane)
            return;
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            string text = _editor.Text;
            EndEdit();
            if (BreadcrumbRules.MeansThisPc(text, Loc.T("This PC")))
                await pane.NavigateAsync(ThisPcProvider.RootPath);
            else if (!string.IsNullOrWhiteSpace(text))
            {
                string target = PathUtil.Resolve(pane.IsLocalDisk || pane.IsArchive ? pane.Path : @"C:\", text);
                MainViewModel? main = _main();
                // Confirmations → "The directory doesn't exist. Do you want to create it?" (not for a directory in an archive)
                if (main != null && !Directory.Exists(target) && !File.Exists(target) && !main.ArchiveProvider.CanHandle(target) &&
                    !await main.CreateMissingDirectoryAsync(target))
                    return;
                await pane.NavigateAsync(target);
            }
            _focusList();
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            EndEdit();
            _focusList();
        }
    }

    public void EndEdit()
    {
        _editor.Visibility = Visibility.Collapsed;
        _host.Visibility = Visibility.Visible;
    }
}
