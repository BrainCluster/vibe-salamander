// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Plugins.DiskMap;

/// <summary>
/// Columns from left to right, one per level (an icicle chart): the directory on the left, its items in the
/// next column with heights by size, their items in the next, and so on. Colors follow the top-level
/// directories like in the Sunburst view.
/// </summary>
internal sealed class ColumnsView : IMapView
{
    public string Name => "Columns";

    private sealed class State
    {
        public required Picture Picture;
        public required RectIndex Index;
        public required RenderContext Context;
        public required CancellationToken Cancellation;
        public required double ColumnWidth;
        public required int Columns;
        public int Count;
        public readonly List<PluginLabel> Labels = new();
    }

    public Frame Render(Node root, RenderContext context, CancellationToken cancellationToken)
    {
        var picture = new Picture(context.Width, context.Height);
        var frame = new Frame(picture);
        double s = context.Scale;
        int fit = Math.Max(2, (int)(picture.Width / (170 * s)));
        int columns = Math.Clamp(Depth(root, fit - 1) + 1, 2, fit);
        var state = new State
        {
            Picture = picture,
            Index = new RectIndex(),
            Context = context,
            Cancellation = cancellationToken,
            ColumnWidth = picture.Width / (double)columns,
            Columns = columns,
        };

        // the directory itself on the left
        Rgb neutral = context.IsDark ? new Rgb(0.24, 0.24, 0.27) : new Rgb(0.86, 0.87, 0.89);
        Tile(state, root, 0, 0, picture.Height, neutral, root.Name);

        double y = 0;
        int hue = 0;
        foreach (Node child in root.Children)
        {
            if (child.Size <= 0)
                break;
            double h = picture.Height * (double)child.Size / Math.Max(1, root.Size);
            Draw(state, child, 1, y, h, Layout.HueOf(hue++));
            y += h;
        }
        state.Index.AttachTo(frame);
        frame.Labels.AddRange(state.Labels);

        int legend = 0;
        foreach (Node child in root.Children)
        {
            if (child.Size <= 0 || legend >= 10)
                break;
            frame.Legend.Add(new PluginLegendItem(Layout.HueOf(legend++).ToArgb(), $"{child.Name}  {Layout.FormatSize(child.Size)}"));
        }
        return frame;
    }

    private static int Depth(Node node, int limit)
    {
        if (limit == 0 || !node.IsDirectory || node.Children.Length == 0)
            return 0;
        int deepest = 0;
        foreach (Node child in node.Children)
            if (child.IsDirectory)
                deepest = Math.Max(deepest, Depth(child, limit - 1));
        return deepest + 1;
    }

    private static void Draw(State state, Node node, int column, double y, double h, Rgb hue)
    {
        if (h < 1 || column >= state.Columns)
            return;
        if (++state.Count % 4096 == 0)
            state.Cancellation.ThrowIfCancellationRequested();
        Rgb color = Layout.BranchColor(hue, column, !node.IsDirectory, state.Context);
        Tile(state, node, column, y, h, color, node.Name);
        if (!node.IsDirectory || node.Size <= 0)
            return;
        double childY = y;
        foreach (Node child in node.Children)
        {
            if (child.Size <= 0)
                break;
            double ch = h * child.Size / node.Size;
            if (ch < 1)
                break; // largest first: the rest are smaller still
            Draw(state, child, column + 1, childY, ch, hue);
            childY += ch;
        }
    }

    private static void Tile(State state, Node node, int column, double y, double h, Rgb color, string name)
    {
        RenderContext c = state.Context;
        double gap = Math.Max(1, c.Scale);
        double x = column * state.ColumnWidth;
        state.Index.Add(node, x, y, state.ColumnWidth, h);
        double radius = Math.Min(4 * c.Scale, (h - gap) / 3);
        state.Picture.FillRoundedRect(x + gap / 2, y + gap / 2, state.ColumnWidth - gap, h - gap, radius, color,
            t => 1.06 - 0.08 * t);
        if (h >= 20 * c.Scale && state.Labels.Count < 600)
        {
            bool twoLines = h >= 38 * c.Scale;
            string text = twoLines ? $"{name}\n{Layout.FormatSize(node.Size)}" : $"{name}  {Layout.FormatSize(node.Size)}";
            state.Labels.Add(new PluginLabel(x + 8 * c.Scale, y + Math.Min(6 * c.Scale, h / 2 - 8 * c.Scale), state.ColumnWidth - 16 * c.Scale,
                Math.Min(h - 4 * c.Scale, 36 * c.Scale), text, RenderContext.TextOn(color), FontSize: 11.5, Bold: column <= 1));
        }
    }
}
