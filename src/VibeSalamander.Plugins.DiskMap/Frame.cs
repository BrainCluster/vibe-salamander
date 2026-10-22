// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;

namespace VibeSalamander.Plugins.DiskMap;

/// <summary>What a view needs to know to draw.</summary>
internal sealed class RenderContext
{
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Pixels per display-independent unit.</summary>
    public double Scale { get; init; } = 1;

    public bool IsDark { get; init; }

    /// <summary>Translation of the plugin's texts.</summary>
    public Func<string, string> T { get; init; } = s => s;

    /// <summary>The page color behind the picture (gaps and edges blend into it).</summary>
    public Rgb Background => IsDark ? new Rgb(0.13, 0.13, 0.14) : new Rgb(0.98, 0.98, 0.985);

    /// <summary>Text on a colored area: dark on light colors, white on dark ones.</summary>
    public static uint TextOn(Rgb color) => color.Luminance > 0.62 ? 0xFF1B1B1Fu : 0xFFFFFFFFu;

    public uint Text => IsDark ? 0xFFF3F3F3u : 0xFF1B1B1Fu;
}

/// <summary>A drawn view: its pixels and labels, and what is where (for the pointer).</summary>
internal sealed class Frame
{
    public Frame(Picture picture) => Picture = picture;

    public Picture Picture { get; }

    public List<PluginLabel> Labels { get; } = new();

    public List<PluginLegendItem> Legend { get; } = new();

    /// <summary>The item at a point (deepest), or null.</summary>
    public Func<PluginPoint, Node?> HitTest { get; set; } = _ => null;

    /// <summary>The outline of an item as drawn, or null when it isn't on the picture.</summary>
    public Func<Node, IReadOnlyList<PluginPoint>?> Outline { get; set; } = _ => null;
}

/// <summary>One way of drawing the tree.</summary>
internal interface IMapView
{
    /// <summary>English (translated by the plugin).</summary>
    string Name { get; }

    /// <summary>Runs on a background thread; throws OperationCanceledException when cancelled.</summary>
    Frame Render(Node root, RenderContext context, CancellationToken cancellationToken);
}

/// <summary>Hit testing and outlines for views made of rectangles (later ones lie on top of earlier ones).</summary>
internal sealed class RectIndex
{
    private readonly List<(Node Node, double X, double Y, double W, double H)> _items = new();
    private readonly Dictionary<Node, int> _byNode = new(ReferenceEqualityComparer.Instance);

    public void Add(Node node, double x, double y, double w, double h)
    {
        _byNode[node] = _items.Count;
        _items.Add((node, x, y, w, h));
    }

    public Node? HitTest(PluginPoint p)
    {
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            var (node, x, y, w, h) = _items[i];
            if (p.X >= x && p.X < x + w && p.Y >= y && p.Y < y + h)
                return node;
        }
        return null;
    }

    public IReadOnlyList<PluginPoint>? Outline(Node node)
    {
        if (!_byNode.TryGetValue(node, out int index))
            return null;
        var (_, x, y, w, h) = _items[index];
        return new[] { new PluginPoint(x, y), new PluginPoint(x + w, y), new PluginPoint(x + w, y + h), new PluginPoint(x, y + h) };
    }

    public void AttachTo(Frame frame)
    {
        frame.HitTest = HitTest;
        frame.Outline = Outline;
    }
}

internal static class Layout
{
    /// <summary>
    /// Squarified treemap (Bruls, Huizing, van Wijk): the items (largest first) fill the rectangle with tiles as
    /// close to squares as possible; <paramref name="place"/> gets each item with its tile.
    /// </summary>
    public static void Squarify(IReadOnlyList<Node> items, double x, double y, double w, double h,
        Action<Node, double, double, double, double> place)
    {
        int count = 0;
        long total = 0;
        foreach (Node item in items)
        {
            if (item.Size <= 0)
                break; // largest first: the rest are empty too
            total += item.Size;
            count++;
        }
        if (count == 0 || w <= 0 || h <= 0)
            return;
        double areaPerByte = w * h / total;
        int start = 0;
        while (start < count && w > 0 && h > 0)
        {
            double side = Math.Min(w, h);
            double rowArea = items[start].Size * areaPerByte;
            double maxArea = rowArea, minArea = rowArea;
            double worst = Worst(rowArea, minArea, maxArea, side);
            int end = start + 1;
            while (end < count)
            {
                double a = items[end].Size * areaPerByte;
                double nextWorst = Worst(rowArea + a, Math.Min(minArea, a), Math.Max(maxArea, a), side);
                if (nextWorst > worst)
                    break;
                rowArea += a;
                minArea = Math.Min(minArea, a);
                maxArea = Math.Max(maxArea, a);
                worst = nextWorst;
                end++;
            }
            // the row's thickness, and its tiles along the shorter side
            double thickness = rowArea / side;
            bool column = w >= h; // lay a column at the left of a wide area, a row at the top of a tall one
            double offset = 0;
            for (int i = start; i < end; i++)
            {
                double length = items[i].Size * areaPerByte / thickness;
                if (i == end - 1)
                    length = side - offset; // no rounding gap at the end
                if (column)
                    place(items[i], x, y + offset, thickness, length);
                else
                    place(items[i], x + offset, y, length, thickness);
                offset += length;
            }
            if (column)
            {
                x += thickness;
                w -= thickness;
            }
            else
            {
                y += thickness;
                h -= thickness;
            }
            start = end;
        }
    }

    private static double Worst(double rowArea, double minArea, double maxArea, double side)
    {
        double s2 = side * side, r2 = rowArea * rowArea;
        return Math.Max(s2 * maxArea / r2, r2 / (s2 * minArea));
    }

    /// <summary>The kind of the largest file in a directory (its color when it is drawn as one tile).</summary>
    public static FileKind DominantKind(Node node)
    {
        Node n = node;
        for (int guard = 0; n.IsDirectory && n.Children.Length > 0 && guard < 64; guard++)
            n = n.Children[0];
        return n.IsDirectory ? FileKind.Other : n.Kind;
    }

    /// <summary>The colors of the top-level directories (Sunburst, Columns): everything inside takes its directory's color.</summary>
    private static readonly uint[] Hues =
    {
        0x4A8FE7, 0xF2A33A, 0x2FB383, 0xE5534B, 0x8E6BE8, 0x22B5C9, 0xD95FA6, 0x93B83C, 0xE07B39, 0x5C6BC0,
    };

    public static Rgb HueOf(int index) => Rgb.From(Hues[index % Hues.Length]);

    /// <summary>The color of an item <paramref name="level"/> levels below the view's root, in its branch's hue.</summary>
    public static Rgb BranchColor(Rgb hue, int level, bool isFile, RenderContext context)
    {
        // deeper levels fade toward the page; files a little more than directories
        double fade = Math.Min(0.55, 0.11 * (level - 1) + (isFile ? 0.08 : 0));
        return hue.Mix(context.Background, fade);
    }

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        string number = unit == 0 ? value.ToString("0", CultureInfo.CurrentCulture)
            : value.ToString(value < 10 ? "0.##" : value < 100 ? "0.#" : "0", CultureInfo.CurrentCulture);
        return $"{number} {units[unit]}";
    }

    public static string Percent(long part, long whole) =>
        whole <= 0 ? "0 %" : (100.0 * part / whole).ToString(part * 1000 < whole ? "0.##" : "0.#", CultureInfo.CurrentCulture) + " %";
}
