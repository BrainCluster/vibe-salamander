// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Plugins;

namespace VibeSalamander.ViewModels;

// PaneViewModel: a plugin's decoration of the folder shown (IPluginPanelDecorator: Git).
public sealed partial class PaneViewModel
{
    private string? _decorationStatus;
    private string? _decorationToolTip;

    /// <summary>On the path line: what a plugin says about this folder (Git: "main ↑2 ↓1").</summary>
    public string? DecorationStatus
    {
        get => _decorationStatus;
        private set => SetProperty(ref _decorationStatus, value);
    }

    public string? DecorationToolTip
    {
        get => _decorationToolTip;
        private set => SetProperty(ref _decorationToolTip, value);
    }

    /// <summary>The last decoration and the folder it was for: a refresh of that folder shows it at once, until the new one arrives.</summary>
    internal (string Path, PanelDecoration Decoration, string Header)? LastDecoration { get; private set; }

    /// <summary>Applies a decoration to the items listed now (null: none here - the column goes away).</summary>
    internal void ApplyDecoration(PanelDecoration? decoration, string? header)
    {
        LastDecoration = decoration != null && header != null ? (Path, decoration, header) : null;
        DecorationStatus = decoration?.Status;
        DecorationToolTip = decoration?.StatusToolTip;
        Columns.ShowPluginColumn(decoration != null ? header : null);
        foreach (FileItemViewModel item in Items)
        {
            if (decoration == null || item.Entry.IsUpDir)
            {
                item.Decorate(null);
                continue;
            }
            item.Decorate(decoration.Items.TryGetValue(item.Entry.Name, out ItemDecoration? own) ? own : decoration.Others);
        }
    }
}
