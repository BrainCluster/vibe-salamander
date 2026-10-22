// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;

namespace VibeSalamander.Plugins.DiskMap;

/// <summary>
/// One Disk Map window: reads the directory tree in the background, then draws the chosen view of the
/// directory shown (double-click zooms in, Backspace goes up) and follows the pointer.
/// </summary>
internal sealed class MapSession
{
    private readonly IPluginHost _host;
    private readonly DiskMapPlugin _plugin;
    private readonly string _path;
    private readonly IPluginCanvasWindow _window;
    private readonly IMapView[] _views = { new BlocksView(), new SunburstView(), new ColumnsView(), new CushionView() };

    private CancellationTokenSource? _scanCancel;
    private CancellationTokenSource? _renderCancel;
    private Node? _tree;          // everything read
    private Node? _shown;         // the directory the picture shows (zoomed in)
    private volatile Frame? _frame;
    private Node? _hover;
    private Node? _selected;
    private int _viewIndex;
    private string? _message;    // shown instead of a picture (while reading)
    private long _zoomedOutAt;   // a click in the middle went up: the double-click that follows doesn't go back in

    public MapSession(IPluginHost host, DiskMapPlugin plugin, string path)
    {
        _host = host;
        _plugin = plugin;
        _path = path;
        _viewIndex = Math.Max(0, Array.FindIndex(_views, v => v.Name == plugin.LastView)); // Blocks unless another was chosen
        _window = host.Ui.OpenCanvasWindow(T("Disk Map") + " – " + path);
        _window.SetHeading(path);
        _window.SetViews(_views.Select(v => T(v.Name)).ToList(), _viewIndex);
        _window.SetButtons(new[]
        {
            new PluginButton("up", T("Up"), ZoomOut),
            new PluginButton("rescan", T("Read again"), Rescan),
        });
        _window.UpdateButton("up", enabled: false);
        _window.ViewChanged += (_, index) =>
        {
            _viewIndex = Math.Clamp(index, 0, _views.Length - 1);
            _plugin.LastView = _views[_viewIndex].Name;
            Render();
        };
        _window.Resized += (_, _) =>
        {
            if (_message != null)
                ShowMessage(_message);
            else
                Render();
        };
        _window.ToolTip = TipAt;
        _window.PointerMoved = PointerMoved;
        _window.PointerExited = () => SetHover(null);
        _window.Clicked = Clicked;
        _window.DoubleClicked = DoubleClicked;
        _window.ContextMenu = MenuAt;
        _window.KeyPressed = KeyPressed;
        _window.Closed += (_, _) =>
        {
            _scanCancel?.Cancel();
            _renderCancel?.Cancel();
            _tree = _shown = _hover = _selected = null;
            _frame = null;
        };
        _ = ScanAsync(null);
    }

    private string T(string text) => _host.Ui.T(text);

    private static string Count(long n) => n.ToString("N0", CultureInfo.CurrentCulture);

    // ---- reading the tree ------------------------------------------------------------------------

    private async Task ScanAsync(string? keepShown)
    {
        _scanCancel?.Cancel();
        var cancel = new CancellationTokenSource();
        _scanCancel = cancel;
        _renderCancel?.Cancel();
        _window.UpdateButton("rescan", enabled: false);
        _window.UpdateButton("up", enabled: false);
        _window.SetProgress(-1);
        ShowMessage(T("Reading the directory tree..."));

        var scanner = new Scanner();
        Task<Node> scan = Task.Run(() => scanner.Scan(_path, cancel.Token), cancel.Token);
        while (!scan.IsCompleted)
        {
            _window.SetStatus(string.Format(T("Reading... {0} files, {1} directories, {2}"),
                Count(scanner.FilesFound), Count(scanner.DirectoriesFound), Layout.FormatSize(scanner.BytesFound)));
            await Task.WhenAny(scan, Task.Delay(200));
        }
        if (cancel.IsCancellationRequested || _window.IsClosed)
            return;
        _window.SetProgress(null);
        _window.UpdateButton("rescan", enabled: true);
        try
        {
            _tree = await scan;
        }
        catch (Exception ex) when (ex is OperationCanceledException or AggregateException { InnerException: OperationCanceledException })
        {
            return;
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message);
            _window.SetStatus(ex.Message);
            return;
        }
        _message = null;
        _shown = Find(_tree, keepShown) ?? _tree;
        _hover = _selected = null;
        Render();
    }

    private void Rescan() => _ = ScanAsync(_shown?.FullPath);

    /// <summary>The node of a path in the new tree (to stay in the directory shown after reading again).</summary>
    private static Node? Find(Node tree, string? path)
    {
        if (path == null || !path.StartsWith(tree.Name, StringComparison.OrdinalIgnoreCase))
            return null;
        Node node = tree;
        foreach (string part in path[tree.Name.Length..].Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            Node? next = node.Children.FirstOrDefault(c => c.IsDirectory && string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase));
            if (next == null)
                return node;
            node = next;
        }
        return node;
    }

    /// <summary>An empty picture with a text in the middle (while reading, or when something went wrong).</summary>
    private void ShowMessage(string text)
    {
        _message = text;
        int w = Math.Max(1, _window.PixelWidth), h = Math.Max(1, _window.PixelHeight);
        double s = _window.Scale;
        _frame = null;
        _window.SetHighlight(null, null);
        _window.SetLegend(Array.Empty<PluginLegendItem>());
        _window.SetPicture(1, 1, new byte[4], new[]
        {
            new PluginLabel(0, h / 2.0 - 20 * s, w, 40 * s, text, _window.IsDark ? 0xFFDDDDDDu : 0xFF444444u, FontSize: 14, Centered: true),
        });
    }

    // ---- drawing ---------------------------------------------------------------------------------

    private void Render()
    {
        if (_shown is not { } shown || _window.IsClosed)
            return;
        _renderCancel?.Cancel();
        var cancel = new CancellationTokenSource();
        _renderCancel = cancel;
        IMapView view = _views[_viewIndex];
        var context = new RenderContext
        {
            Width = Math.Max(1, _window.PixelWidth),
            Height = Math.Max(1, _window.PixelHeight),
            Scale = _window.Scale,
            IsDark = _window.IsDark,
            T = T,
        };
        _window.SetHeading(shown.FullPath);
        _window.UpdateButton("up", enabled: shown.Parent != null);
        ShowSummary();
        _ = Task.Run(() =>
        {
            try
            {
                Frame frame = view.Render(shown, context, cancel.Token);
                if (cancel.IsCancellationRequested || _window.IsClosed)
                    return;
                _frame = frame;
                _window.SetPicture(frame.Picture.Width, frame.Picture.Height, frame.Picture.Pixels, frame.Labels);
                _window.SetLegend(frame.Legend);
                UpdateHighlight();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _window.SetStatus(ex.Message);
            }
        });
    }

    private void ShowSummary()
    {
        if (_shown is not { } shown)
            return;
        string text = string.Format(T("{0} in {1} files and {2} directories"), Layout.FormatSize(shown.Size), Count(shown.Files), Count(shown.Directories));
        if (shown.Incomplete)
            text += "  ·  " + T("some directories couldn't be read");
        _window.SetStatus(text);
    }

    private void UpdateHighlight()
    {
        Frame? frame = _frame;
        if (frame == null)
            return;
        _window.SetHighlight(_hover != null ? frame.Outline(_hover) : null, _selected != null ? frame.Outline(_selected) : null);
    }

    // ---- pointer and keys --------------------------------------------------------------------------

    private Node? At(PluginPoint p) => _frame?.HitTest(p);

    private void SetHover(Node? node)
    {
        if (node == _hover)
            return;
        _hover = node;
        UpdateHighlight();
        if (node == null)
            ShowSummary();
        else
            _window.SetStatus(Describe(node));
    }

    private void PointerMoved(PluginPoint p) => SetHover(At(p));

    private string Describe(Node node)
    {
        long whole = _shown?.Size ?? node.Size;
        string text = $"{node.FullPath}  ·  {Layout.FormatSize(node.Size)}  ·  {Layout.Percent(node.Size, whole)}";
        if (node.IsDirectory)
            text += "  ·  " + string.Format(T("{0} files"), Count(node.Files));
        return text;
    }

    private string? TipAt(PluginPoint p)
    {
        if (At(p) is not { } node)
            return null;
        string text = $"{node.Name}\n{Layout.FormatSize(node.Size)}  ·  {Layout.Percent(node.Size, _shown?.Size ?? node.Size)}";
        if (node.IsDirectory)
            text += "\n" + string.Format(T("{0} files, {1} directories"), Count(node.Files), Count(node.Directories));
        else if (node.Kind != FileKind.Other)
            text += "\n" + T(FileKinds.Name(node.Kind));
        return text;
    }

    private void Clicked(PluginPoint p)
    {
        Node? node = At(p);
        // the middle of the Sunburst (or the left column): the directory shown; a click there goes up
        if (node != null && node == _shown && _views[_viewIndex] is SunburstView or ColumnsView)
        {
            _zoomedOutAt = Environment.TickCount64;
            ZoomOut();
            return;
        }
        _selected = node;
        UpdateHighlight();
    }

    private void DoubleClicked(PluginPoint p)
    {
        if (Environment.TickCount64 - _zoomedOutAt < 800)
            return;
        if (At(p) is { } node)
            ZoomTo(node);
    }

    /// <summary>Shows a directory (a file: its directory) as the whole picture.</summary>
    private void ZoomTo(Node node)
    {
        Node target = node.IsDirectory ? node : node.Parent ?? node;
        if (target == _shown || _shown == null || !target.IsWithin(_tree!))
            return;
        _shown = target;
        _hover = null;
        _selected = node.IsDirectory ? null : node;
        Render();
    }

    private void ZoomOut()
    {
        if (_shown?.Parent is not { } parent)
            return;
        Node previous = _shown;
        _shown = parent;
        _hover = null;
        _selected = previous; // where we came from stays marked
        Render();
    }

    private bool KeyPressed(string key)
    {
        switch (key)
        {
            case "Backspace":
                ZoomOut();
                return true;
            case "Enter":
                if (_selected != null)
                    ZoomTo(_selected);
                return true;
            case "Ctrl+C":
                if ((_selected ?? _hover) is { } node)
                    _host.Ui.CopyToClipboard(node.FullPath);
                return true;
            case "F5" or "Ctrl+R":
                Rescan();
                return true;
            case "1" or "2" or "3" or "4":
                _viewIndex = key[0] - '1';
                _plugin.LastView = _views[_viewIndex].Name;
                _window.SetViews(_views.Select(v => T(v.Name)).ToList(), _viewIndex);
                Render();
                return true;
        }
        return false;
    }

    private IReadOnlyList<PluginMenuItem> MenuAt(PluginPoint p)
    {
        Node? node = At(p);
        if (node != null)
        {
            _selected = node;
            UpdateHighlight();
        }
        var items = new List<PluginMenuItem>();
        if (node != null)
        {
            Node target = node.IsDirectory ? node : node.Parent ?? node;
            items.Add(new PluginMenuItem(T("Zoom in"), "Enter", () => ZoomTo(node), target != _shown));
        }
        items.Add(new PluginMenuItem(T("Up"), "Backspace", ZoomOut, _shown?.Parent != null));
        if (node != null)
        {
            items.Add(new PluginMenuItem(T("Show in panel"), null, () => _ = ShowInPanelAsync(node)));
            items.Add(new PluginMenuItem(T("Copy path"), "Ctrl+C", () => _host.Ui.CopyToClipboard(node.FullPath)));
        }
        items.Add(new PluginMenuItem(T("Read again"), "F5", Rescan));
        return items;
    }

    /// <summary>The active panel goes to the item's directory with the item focused (a directory: into it).</summary>
    private async Task ShowInPanelAsync(Node node)
    {
        try
        {
            if (node.IsDirectory)
                await _host.ActivePanel.NavigateAsync(node.FullPath);
            else if (node.Parent != null)
                await _host.ActivePanel.NavigateAsync(node.Parent.FullPath, node.Name);
        }
        catch (Exception ex)
        {
            _host.Notify(ex.Message, isError: true);
        }
    }
}
