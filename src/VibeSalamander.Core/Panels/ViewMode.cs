// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Core.Panels;

/// <summary>Panel views (Salamander: Left/Right → Views, Alt+1…5).</summary>
public enum ViewMode
{
    /// <summary>Names only, in several columns, scrolling sideways.</summary>
    Brief,
    /// <summary>One row per item with Name, Ext, Size, Date, Time, Attr columns.</summary>
    Detailed,
    /// <summary>Large icons with the name below.</summary>
    Icons,
    /// <summary>Picture previews (shell thumbnails) with the name below.</summary>
    Thumbnails,
    /// <summary>Large icon with name, type and size beside it.</summary>
    Tiles,
}

/// <summary>Detailed view columns: widths in DIPs and which ones are shown (Name is always shown).</summary>
public sealed class ColumnSettings
{
    public const double DefaultExt = 56, DefaultSize = 112, DefaultDate = 84, DefaultTime = 70, DefaultAttr = 52;

    public double ExtWidth { get; set; } = DefaultExt;
    public double SizeWidth { get; set; } = DefaultSize;
    public double DateWidth { get; set; } = DefaultDate;
    public double TimeWidth { get; set; } = DefaultTime;
    public double AttrWidth { get; set; } = DefaultAttr;

    public bool ShowExt { get; set; } = true;
    public bool ShowSize { get; set; } = true;
    public bool ShowDate { get; set; } = true;
    public bool ShowTime { get; set; } = true;
    public bool ShowAttr { get; set; } = true;
}
