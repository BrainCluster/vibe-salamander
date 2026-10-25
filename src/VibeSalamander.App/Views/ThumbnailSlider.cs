// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using UiState = VibeSalamander.ViewModels.UiState;

namespace VibeSalamander.Views;

/// <summary>
/// The thumbnail size slider (toolbar and Configuration → Appearance): dots every 32 pixels, and the thumb
/// snaps to a dot when it comes close to it.
/// </summary>
public static class ThumbnailSlider
{
    public const int Step = 32;

    /// <summary>How close (pixels of thumbnail size) the thumb has to come to a dot to snap to it.</summary>
    private const int SnapDistance = 8;

    /// <summary>The size for a slider position: the nearest dot when it is close, else in steps of 4.</summary>
    public static int Snap(double value)
    {
        double dot = UiState.MinThumbnailSize + Math.Round((value - UiState.MinThumbnailSize) / Step) * Step;
        int size = Math.Abs(value - dot) <= SnapDistance ? (int)dot : (int)(Math.Round(value / 4) * 4);
        return Math.Clamp(size, UiState.MinThumbnailSize, UiState.MaxThumbnailSize);
    }

    /// <summary>A new slider; <paramref name="changed"/> gets the (snapped) sizes the user chooses.</summary>
    public static Slider Create(double width, Action<int> changed)
    {
        var slider = new Slider
        {
            Minimum = UiState.MinThumbnailSize,
            Maximum = UiState.MaxThumbnailSize,
            Value = UiState.ThumbnailSize,
            StepFrequency = 1,
            SmallChange = 4,
            LargeChange = Step,
            TickFrequency = Step,
            TickPlacement = TickPlacement.Inline,
            Width = width,
            VerticalAlignment = VerticalAlignment.Center,
        };
        bool snapping = false;
        slider.ValueChanged += (_, args) =>
        {
            if (snapping)
                return;
            int size = Snap(args.NewValue);
            if (size != args.NewValue)
            {
                // the thumb jumps to the dot (or the step) while dragging
                snapping = true;
                slider.Value = size;
                snapping = false;
            }
            changed(size);
        };
        return slider;
    }
}
