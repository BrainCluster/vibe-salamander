// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace VibeSalamander.Views;

/// <summary>Splitter between the panels, or (Horizontal) above the transfer panel; shows the resize cursor.</summary>
public sealed partial class PanelSplitter : Grid
{
    private bool _horizontal;

    public PanelSplitter()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }

    /// <summary>True for a splitter that resizes vertically (a horizontal bar).</summary>
    public bool Horizontal
    {
        get => _horizontal;
        set
        {
            _horizontal = value;
            ProtectedCursor = InputSystemCursor.Create(value ? InputSystemCursorShape.SizeNorthSouth : InputSystemCursorShape.SizeWestEast);
        }
    }
}
