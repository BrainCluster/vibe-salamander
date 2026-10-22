// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Plugins.DiskMap;

/// <summary>
/// A flat, nested treemap: every directory is a rounded panel with its name and size in a header, its
/// files and directories as tiles inside. Files are colored by what they are.
/// </summary>
internal sealed class BlocksView : IMapView
{
    public string Name => "Blocks";

    private sealed class State
    {
        public required Picture Picture;
        public required RectIndex Index;
        public required RenderContext Context;
        public required CancellationToken Cancellation;
        public int Count;
        public int LabelCount;
        public readonly List<PluginLabel> Labels = new();
    }

    public Frame Render(Node root, RenderContext context, CancellationToken cancellationToken)
    {
        var picture = new Picture(context.Width, context.Height);
        var frame = new Frame(picture);
        var state = new State { Picture = picture, Index = new RectIndex(), Context = context, Cancellation = cancellationToken };
        double margin = 2 * context.Scale;
        Draw(state, root, margin, margin, picture.Width - 2 * margin, picture.Height - 2 * margin, 0);
        state.Index.AttachTo(frame);
        frame.Labels.AddRange(state.Labels);
        CushionView.AddKindLegend(frame, root, context);
        return frame;
    }

    private static Rgb PanelColor(RenderContext c, int level) => c.IsDark
        ? new Rgb(0.19, 0.19, 0.21).Mix(Rgb.White, Math.Min(0.18, 0.035 * level))
        : new Rgb(0.90, 0.905, 0.92).Mix(Rgb.Black, Math.Min(0.22, 0.045 * level));

    private static void Draw(State state, Node node, double x, double y, double w, double h, int level)
    {
        RenderContext c = state.Context;
        double gap = Math.Max(1, c.Scale);
        if (w < 1 || h < 1 || node.Size <= 0 && level > 0)
            return;
        if (++state.Count % 4096 == 0)
            state.Cancellation.ThrowIfCancellationRequested();
        state.Index.Add(node, x, y, w, h);
        // the tile is drawn inset by half a gap on each side: neighbours keep a gap between them
        double tx = x + gap / 2, ty = y + gap / 2, tw = w - gap, th = h - gap;
        if (tw < 1 || th < 1)
        {
            state.Picture.FillRoundedRect(x, y, w, h, 0, TileColor(node, c));
            return;
        }
        double radius = Math.Min(5 * c.Scale, Math.Min(tw, th) / 4);

        if (!node.IsDirectory || node.Children.Length == 0)
        {
            Rgb color = TileColor(node, c);
            state.Picture.FillRoundedRect(tx, ty, tw, th, radius, color, t => 1.10 - 0.16 * t); // a soft light from above
            if (!node.IsDirectory)
                AddTileLabel(state, node, tx, ty, tw, th, color);
            return;
        }

        double header = 20 * c.Scale;
        double pad = 3 * c.Scale;
        bool roomy = tw > 70 * c.Scale && th > header + 24 * c.Scale;
        if (tw < 10 * c.Scale || th < 10 * c.Scale)
        {
            // too small for its content: one tile in its main color
            state.Picture.FillRoundedRect(tx, ty, tw, th, radius, TileColor(node, c));
            return;
        }
        Rgb panel = PanelColor(c, level);
        state.Picture.FillRoundedRect(tx, ty, tw, th, radius, panel);
        double innerTop = ty + (roomy ? header : pad);
        if (roomy && state.LabelCount < 400)
        {
            state.LabelCount++;
            state.Labels.Add(new PluginLabel(tx + 6 * c.Scale, ty + 2 * c.Scale, tw - 12 * c.Scale, header - 2 * c.Scale,
                $"{node.Name}  ·  {Layout.FormatSize(node.Size)}", c.Text, FontSize: 11.5, Bold: level <= 1));
        }
        Layout.Squarify(node.Children, tx + pad, innerTop, tw - 2 * pad, ty + th - pad - innerTop,
            (child, cx, cy, cw, ch) => Draw(state, child, cx, cy, cw, ch, level + 1));
    }

    private static Rgb TileColor(Node node, RenderContext c)
    {
        Rgb color = Rgb.From(FileKinds.Color(node.IsDirectory ? Layout.DominantKind(node) : node.Kind));
        return c.IsDark ? color.Scale(0.92) : color;
    }

    private static void AddTileLabel(State state, Node node, double x, double y, double w, double h, Rgb color)
    {
        RenderContext c = state.Context;
        if (w < 64 * c.Scale || h < 22 * c.Scale || state.LabelCount >= 400)
            return;
        state.LabelCount++;
        bool twoLines = h >= 40 * c.Scale;
        state.Labels.Add(new PluginLabel(x + 4 * c.Scale, y + 3 * c.Scale, w - 8 * c.Scale, Math.Min(h - 6 * c.Scale, 40 * c.Scale),
            twoLines ? $"{node.Name}\n{Layout.FormatSize(node.Size)}" : node.Name, RenderContext.TextOn(color), FontSize: 11));
    }
}
