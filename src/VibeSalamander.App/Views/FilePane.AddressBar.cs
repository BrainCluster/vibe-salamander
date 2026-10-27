// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using VibeSalamander.Core.Commands;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Panels;
using VibeSalamander.Services;
using VibeSalamander.ViewModels;

namespace VibeSalamander.Views;

// FilePane: the address bar (breadcrumb, overflow, subfolder menus).
public sealed partial class FilePane
{
    // ---- address bar (breadcrumb) ---------------------------------------------------------

    /// <summary>One location in the address bar: its button and the chevron after it.</summary>
    private sealed record Crumb(string Label, string Path, Button Segment, Button Chevron);

    private readonly List<Crumb> _crumbs = new();
    private Button? _overflowButton;
    private (string Path, string Display, object Drives)? _breadcrumbKey;

    /// <summary>"This PC", the drive ("Local Disk (C:)") or share, then each folder of the path.</summary>
    private List<(string Label, string Path)> BreadcrumbSegments()
    {
        var segments = new List<(string, string)> { (Loc.T("This PC"), ThisPcProvider.RootPath) };
        if (ViewModel == null || ViewModel.IsThisPc)
            return segments;
        if (ViewModel.IsRemote && _main != null)
        {
            // the server, then its directories: walked up through the plugin's parents
            var chain = new List<string>();
            string? step = ViewModel.Path;
            while (step != null && step != ThisPcProvider.RootPath && chain.Count < 64)
            {
                chain.Add(step);
                step = _main.RemoteProvider.GetParent(step)?.Path;
            }
            chain.Reverse();
            for (int i = 0; i < chain.Count; i++)
                segments.Add((i == 0 ? _main.RemoteProvider.GetDisplayPath(chain[i]).TrimEnd('/') : RemoteFileSystemProvider.LastName(chain[i]), chain[i]));
            return segments;
        }
        if (!ViewModel.IsLocalDisk && !ViewModel.IsArchive)
        {
            segments.Add((ViewModel.DisplayPath, ViewModel.Path));
            return segments;
        }

        string path = ViewModel.Path;
        string root = Path.GetPathRoot(path) ?? path;
        string rootLabel;
        if (root.Length >= 2 && root[1] == ':')
        {
            string drive = root[..2].ToUpperInvariant();
            DriveButtonViewModel? info = ViewModel.Drives.FirstOrDefault(d => d.Drive.Root.StartsWith(drive, StringComparison.OrdinalIgnoreCase));
            rootLabel = info?.Drive.Caption ?? drive;
            root = drive + "\\";
        }
        else
        {
            rootLabel = root.TrimEnd('\\'); // \\server\share
        }
        segments.Add((rootLabel, root));

        string current = root;
        foreach (string part in path[Math.Min(path.Length, Path.GetPathRoot(path)?.Length ?? 0)..]
                     .Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            segments.Add((part, current));
        }
        return segments;
    }

    private Button CrumbButton(UIElement content, string? toolTip)
    {
        // like Explorer, the address bar doesn't take the keyboard focus away from the file list
        var button = new Button { Content = content, AllowFocusOnInteraction = false, IsTabStop = false };
        if (Application.Current.Resources.TryGetValue("VibeBreadcrumbButtonStyle", out object? style))
            button.Style = style as Style;
        if (toolTip != null)
            ToolTipService.SetToolTip(button, toolTip);
        return button;
    }

    private void RebuildBreadcrumb()
    {
        if (ViewModel == null)
            return;
        // PropertyChanged comes several times per navigation and on every refresh (DisplayPath); rebuilding
        // only on a real change keeps the buttons (and an open subfolder menu's anchor) alive
        var key = (ViewModel.Path, ViewModel.DisplayPath, (object)ViewModel.Drives);
        if (_breadcrumbKey == key)
            return;
        _breadcrumbKey = key;
        Brush foreground = PathForeground(ViewModel.IsActive);

        // shown when the path is too long: the leading locations move into its menu (like Explorer)
        if (_overflowButton == null)
        {
            _overflowButton = CrumbButton(new FontIcon { Glyph = "\uE712", FontSize = 12, Foreground = foreground }, "More locations");
            _overflowButton.Visibility = Visibility.Collapsed;
            _overflowButton.Click += Overflow_Click;
            Breadcrumb.Children.Insert(0, _overflowButton);
        }

        // going into a subfolder or back up keeps the buttons the paths share; only the rest is rebuilt
        var segments = BreadcrumbSegments();
        int keep = 0;
        while (keep < _crumbs.Count && keep < segments.Count && _crumbs[keep].Label == segments[keep].Label &&
               string.Equals(_crumbs[keep].Path, segments[keep].Path, StringComparison.Ordinal))
            keep++;
        for (int i = _crumbs.Count - 1; i >= keep; i--)
        {
            Breadcrumb.Children.Remove(_crumbs[i].Segment);
            Breadcrumb.Children.Remove(_crumbs[i].Chevron);
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
            segment.Click += async (_, _) => await NavigateFromBreadcrumbAsync(path);

            Button chevron = CrumbButton(new FontIcon { Glyph = "\uE76C", FontSize = 8, Foreground = foreground }, null);
            chevron.Padding = new Thickness(3, 4, 3, 4);
            chevron.Click += async (sender, _) =>
            {
                // the folder after this one in the current path (it is preselected in the menu)
                int index = _crumbs.FindIndex(c => c.Chevron == sender);
                string? next = index >= 0 && index + 1 < _crumbs.Count ? _crumbs[index + 1].Path : null;
                await ShowSubfoldersAsync((Button)sender, path, next);
            };

            Breadcrumb.Children.Add(segment);
            Breadcrumb.Children.Add(chevron);
            _crumbs.Add(new Crumb(label, path, segment, chevron));
        }

        // the current folder in bold
        for (int i = 0; i < _crumbs.Count; i++)
            if (_crumbs[i].Segment.Content is TextBlock label)
                label.FontWeight = i == _crumbs.Count - 1 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        UpdateBreadcrumbOverflow();
    }

    /// <summary>Active/inactive or theme change: only the text color changes.</summary>
    private void UpdateBreadcrumbForeground()
    {
        if (ViewModel == null)
            return;
        Brush foreground = PathForeground(ViewModel.IsActive);
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

    private void BreadcrumbHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateBreadcrumbOverflow();

    /// <summary>Hides leading locations (into the "..." menu) until the rest fits; the current folder always stays.</summary>
    private void UpdateBreadcrumbOverflow()
    {
        if (_overflowButton == null || _crumbs.Count == 0)
            return;
        double available = BreadcrumbHost.ActualWidth;
        if (available <= 0)
            return;
        var infinite = new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
        double Width(UIElement element)
        {
            element.Measure(infinite);
            return element.DesiredSize.Width;
        }

        double total = 0;
        foreach (Crumb crumb in _crumbs)
        {
            crumb.Segment.Visibility = Visibility.Visible;
            crumb.Chevron.Visibility = Visibility.Visible;
            total += Width(crumb.Segment) + Width(crumb.Chevron);
        }
        _overflowButton.Visibility = Visibility.Collapsed;
        if (total <= available)
            return;

        _overflowButton.Visibility = Visibility.Visible;
        total += Width(_overflowButton);
        for (int i = 0; i < _crumbs.Count - 1 && total > available; i++)
        {
            total -= _crumbs[i].Segment.DesiredSize.Width + _crumbs[i].Chevron.DesiredSize.Width;
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
            item.Click += async (_, _) => await NavigateFromBreadcrumbAsync(path);
            menu.Items.Add(item);
        }
        menu.ShowAt((FrameworkElement)sender);
    }

    private async Task NavigateFromBreadcrumbAsync(string path)
    {
        if (ViewModel == null || _main == null)
            return;
        _main.Activate(ViewModel);
        // going up focuses the folder we came from, like Backspace
        string? focus = (ViewModel.IsLocalDisk || ViewModel.IsArchive) && path != ThisPcProvider.RootPath ? ChildOnTheWay(path, ViewModel.Path) : null;
        await ViewModel.NavigateAsync(path, focus);
        FocusList();
    }

    /// <summary>Name of the item inside <paramref name="ancestor"/> that leads to <paramref name="path"/>, if any.</summary>
    private static string? ChildOnTheWay(string ancestor, string path)
    {
        string a = ancestor.TrimEnd('\\') + "\\";
        if (!path.StartsWith(a, StringComparison.OrdinalIgnoreCase) || path.Length <= a.Length)
            return null;
        return path[a.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    /// <summary>The chevron after a location: its subfolders (drives after "This PC"), the next one on the path in bold.</summary>
    private async Task ShowSubfoldersAsync(Button anchor, string path, string? next)
    {
        if (ViewModel == null || _main == null)
            return;
        _main.Activate(ViewModel);
        var menu = new MenuFlyout();
        if (path == ThisPcProvider.RootPath)
        {
            foreach (DriveButtonViewModel drive in ViewModel.Drives)
            {
                string root = drive.Drive.Root;
                var item = new MenuFlyoutItem { Text = drive.Drive.Caption, Icon = new FontIcon { Glyph = "\uEDA2" } };
                if (next != null && root.StartsWith(next.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    item.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                item.Click += async (_, _) => await NavigateFromBreadcrumbAsync(root);
                menu.Items.Add(item);
            }
        }
        else
        {
            bool showHidden = _main.Settings.ShowHidden;
            // on a server: its directories, as the plugin lists them
            var remoteTargets = new Dictionary<string, string>(StringComparer.Ordinal);
            if (_main.RemoteProvider.CanHandle(path))
            {
                DirectoryListing listing = await _main.RemoteProvider.ListAsync(path, new ListingOptions { ShowHidden = showHidden }, CancellationToken.None);
                foreach (FileEntry entry in listing.Entries.Where(e => e.IsDirectory && !e.IsUpDir).OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).Take(500))
                    remoteTargets[entry.Name] = entry.FullPath;
            }
            List<string> folders = remoteTargets.Count > 0 || _main.RemoteProvider.CanHandle(path)
                ? remoteTargets.Keys.ToList()
                : _main.ArchiveProvider.CanHandle(path)
                ? await _main.ArchiveProvider.ListDirectoriesAsync(path) // a directory inside an archive (or the archive itself)
                : await Task.Run(() =>
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
            });
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
                item.Click += async (_, _) => await NavigateFromBreadcrumbAsync(target);
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
}
