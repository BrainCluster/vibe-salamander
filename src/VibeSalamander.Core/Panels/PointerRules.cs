// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Core.Panels;

/// <summary>Selecting with a mouse rectangle in a panel: when it starts, what it marks, how it scrolls.</summary>
public static class MarqueeRules
{
    /// <summary>How far (in pixels, either way) the mouse moves before a press becomes a rectangle.</summary>
    public const double StartDistance = 4;

    /// <summary>How close to the list's edge the mouse starts scrolling it.</summary>
    public const double ScrollEdge = 12;

    /// <summary>The most the list scrolls in one step.</summary>
    public const double MaxScrollStep = 40;

    public static bool HasStarted(double dx, double dy) => Math.Abs(dx) >= StartDistance || Math.Abs(dy) >= StartDistance;

    /// <summary>
    /// A row's mark while the rectangle is drawn: rows inside it are marked (with Ctrl: flipped from what they were
    /// when the rectangle started); rows outside keep what they had then.
    /// </summary>
    public static bool MarkFor(bool inside, bool toggle, bool markedAtStart) =>
        inside ? !toggle || !markedAtStart : markedAtStart;

    /// <summary>True when two rectangles (left, top, right, bottom) overlap; touching edges don't.</summary>
    public static bool Overlaps(double left, double top, double right, double bottom,
                                double otherLeft, double otherTop, double otherRight, double otherBottom) =>
        left < otherRight && right > otherLeft && top < otherBottom && bottom > otherTop;

    /// <summary>
    /// The scroll step along one axis while the mouse is at <paramref name="position"/> in a list
    /// <paramref name="length"/> long: back near the start, forward near the end (faster further out), else 0.
    /// </summary>
    public static double AutoScrollStep(double position, double length)
    {
        if (position < ScrollEdge)
            return -Math.Min(MaxScrollStep, ScrollEdge - position + 4);
        if (position > length - ScrollEdge)
            return Math.Min(MaxScrollStep, position - (length - ScrollEdge) + 4);
        return 0;
    }
}

/// <summary>Drag and drop between panels (and from or to Explorer): what is dragged, where it goes, copy or move.</summary>
public static class DropRules
{
    /// <summary>
    /// What a drag takes: the dragged rows, or every marked row when one of the dragged rows is marked (Salamander).
    /// ".." and drives never go along.
    /// </summary>
    public static List<string> DraggedPaths(IReadOnlyList<IMarkable> dragged, IReadOnlyList<IMarkable> marked)
    {
        IReadOnlyList<IMarkable> items = dragged.Any(i => i.IsMarked) ? marked : dragged;
        return items.Where(Marking.CanMark).Select(i => i.Entry.FullPath).ToList();
    }

    /// <summary>
    /// A drag from a panel released sooner than Configuration → Panels → "Cancel drag-and-drop operations shorter
    /// than" after it started is an accident: nothing is dropped. Drags from other programs always count.
    /// </summary>
    public static bool IsTooShort(int minimumMilliseconds, bool fromAPanel, TimeSpan sinceStart) =>
        minimumMilliseconds > 0 && fromAPanel && sinceStart.TotalMilliseconds < minimumMilliseconds;

    /// <summary>
    /// Explorer's rules: Shift moves, Ctrl copies, otherwise items move within a drive and are copied to another
    /// drive. Into an archive or onto a server they are copied unless Shift is held. Without knowing where the
    /// items come from (a drag from another program), they are copied.
    /// </summary>
    public static bool IsMove(bool shift, bool ctrl, bool intoArchiveOrServer, string? firstSource, string target)
    {
        if (shift)
            return true;
        if (ctrl || intoArchiveOrServer || string.IsNullOrEmpty(firstSource))
            return false;
        return string.Equals(Path.GetPathRoot(firstSource), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the drop would do nothing: no items, or all of them are in the target directory already.</summary>
    public static bool ChangesNothing(IReadOnlyList<string> paths, string target) =>
        paths.Count == 0 || paths.All(p => string.Equals(Path.GetDirectoryName(p), target, StringComparison.OrdinalIgnoreCase));
}
