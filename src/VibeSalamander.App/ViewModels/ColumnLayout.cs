// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Xaml;
using VibeSalamander.Core;
using VibeSalamander.Core.Panels;

namespace VibeSalamander.ViewModels;

public enum DetailColumn
{
    Ext,
    Size,
    Date,
    Time,
    Attr,
}

/// <summary>
/// Widths and visibility of the Detailed view columns of one panel. Rows and the header line bind to
/// the GridLength properties, so resizing a column in the header resizes every row at once.
/// </summary>
public sealed class ColumnLayout : BindableBase
{
    public const double MinWidth = 24, MaxWidth = 480;

    private readonly ColumnSettings _settings;

    public ColumnLayout(ColumnSettings settings)
    {
        _settings = settings;
    }

    public GridLength ExtColumn => Length(DetailColumn.Ext);
    public GridLength SizeColumn => Length(DetailColumn.Size);
    public GridLength DateColumn => Length(DetailColumn.Date);
    public GridLength TimeColumn => Length(DetailColumn.Time);
    public GridLength AttrColumn => Length(DetailColumn.Attr);

    public Visibility ExtVisibility => Vis(DetailColumn.Ext);
    public Visibility SizeVisibility => Vis(DetailColumn.Size);
    public Visibility DateVisibility => Vis(DetailColumn.Date);
    public Visibility TimeVisibility => Vis(DetailColumn.Time);
    public Visibility AttrVisibility => Vis(DetailColumn.Attr);

    private GridLength Length(DetailColumn c) => new(IsShown(c) ? GetWidth(c) : 0);
    private Visibility Vis(DetailColumn c) => IsShown(c) ? Visibility.Visible : Visibility.Collapsed;

    // ---- narrow panels: the name always has room ------------------------------------------------------

    /// <summary>The Name column never gets narrower than this while other columns can step aside.</summary>
    public const double MinNameWidth = 140;

    /// <summary>The panel's padding and the icon column (the header and the rows have the same).</summary>
    private const double FixedPart = 16 + 22 + 4;

    /// <summary>The columns that step aside first when the panel gets narrow.</summary>
    private static readonly DetailColumn[] StepAsideOrder = { DetailColumn.Attr, DetailColumn.Time, DetailColumn.Date, DetailColumn.Ext, DetailColumn.Size };

    private double _panelWidth = double.PositiveInfinity;
    private HashSet<DetailColumn> _steppedAside = new();

    /// <summary>
    /// The list's width (the panel tells it when it changes): columns that don't fit next to a readable name are left
    /// out until there is room again (the user's choice of columns stays as it is).
    /// </summary>
    public void SetPanelWidth(double width)
    {
        if (width <= 0 || Math.Abs(width - _panelWidth) < 0.5)
            return;
        _panelWidth = width;
        UpdateSteppedAside();
    }

    private void UpdateSteppedAside()
    {
        var aside = new HashSet<DetailColumn>();
        double Others() => FixedPart + Enum.GetValues<DetailColumn>().Where(c => IsVisible(c) && !aside.Contains(c)).Sum(GetWidth);
        foreach (DetailColumn c in StepAsideOrder)
        {
            if (_panelWidth - Others() >= MinNameWidth)
                break;
            if (IsVisible(c))
                aside.Add(c);
        }
        if (aside.SetEquals(_steppedAside))
            return;
        _steppedAside = aside;
        foreach (DetailColumn c in Enum.GetValues<DetailColumn>())
        {
            OnPropertyChanged(c + "Column");
            OnPropertyChanged(c + "Visibility");
        }
    }

    /// <summary>The column is chosen and has room (the rows and the header show it).</summary>
    public bool IsShown(DetailColumn c) => IsVisible(c) && !_steppedAside.Contains(c);

    public double GetWidth(DetailColumn c) => c switch
    {
        DetailColumn.Ext => _settings.ExtWidth,
        DetailColumn.Size => _settings.SizeWidth,
        DetailColumn.Date => _settings.DateWidth,
        DetailColumn.Time => Math.Max(_settings.TimeWidth, MinTimeWidth),
        _ => _settings.AttrWidth,
    };

    /// <summary>
    /// 12-hour times ("9:26:53 PM", English and others) are longer than 24-hour ones: the Time column is never
    /// narrower than they need (it would cut off the hour).
    /// </summary>
    private static double MinTimeWidth =>
        Core.Formatting.Formats.TwelveHours ? Math.Round(90 * UiState.FontSize / 13) : 0;

    public bool IsVisible(DetailColumn c) => c switch
    {
        DetailColumn.Ext => _settings.ShowExt,
        DetailColumn.Size => _settings.ShowSize,
        DetailColumn.Date => _settings.ShowDate,
        DetailColumn.Time => _settings.ShowTime,
        _ => _settings.ShowAttr,
    };

    public void SetWidth(DetailColumn c, double width)
    {
        width = Math.Round(Math.Clamp(width, MinWidth, MaxWidth));
        switch (c)
        {
            case DetailColumn.Ext: _settings.ExtWidth = width; break;
            case DetailColumn.Size: _settings.SizeWidth = width; break;
            case DetailColumn.Date: _settings.DateWidth = width; break;
            case DetailColumn.Time: _settings.TimeWidth = width; break;
            default: _settings.AttrWidth = width; break;
        }
        Changed(c);
    }

    public void SetVisible(DetailColumn c, bool visible)
    {
        switch (c)
        {
            case DetailColumn.Ext: _settings.ShowExt = visible; break;
            case DetailColumn.Size: _settings.ShowSize = visible; break;
            case DetailColumn.Date: _settings.ShowDate = visible; break;
            case DetailColumn.Time: _settings.ShowTime = visible; break;
            default: _settings.ShowAttr = visible; break;
        }
        Changed(c);
    }

    public static double DefaultWidth(DetailColumn c) => c switch
    {
        DetailColumn.Ext => ColumnSettings.DefaultExt,
        DetailColumn.Size => ColumnSettings.DefaultSize,
        DetailColumn.Date => ColumnSettings.DefaultDate,
        DetailColumn.Time => ColumnSettings.DefaultTime,
        _ => ColumnSettings.DefaultAttr,
    };

    /// <summary>All columns visible with their default widths.</summary>
    public void Reset()
    {
        foreach (DetailColumn c in Enum.GetValues<DetailColumn>())
        {
            SetVisible(c, true);
            SetWidth(c, DefaultWidth(c));
        }
    }

    private void Changed(DetailColumn c)
    {
        OnPropertyChanged(c + "Column");
        OnPropertyChanged(c + "Visibility");
        UpdateSteppedAside(); // a wider or newly shown column may not fit any more (or a narrower one fits again)
    }
}
