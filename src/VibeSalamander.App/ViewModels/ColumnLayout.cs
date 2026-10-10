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

    private GridLength Length(DetailColumn c) => new(IsVisible(c) ? GetWidth(c) : 0);
    private Visibility Vis(DetailColumn c) => IsVisible(c) ? Visibility.Visible : Visibility.Collapsed;

    public double GetWidth(DetailColumn c) => c switch
    {
        DetailColumn.Ext => _settings.ExtWidth,
        DetailColumn.Size => _settings.SizeWidth,
        DetailColumn.Date => _settings.DateWidth,
        DetailColumn.Time => _settings.TimeWidth,
        _ => _settings.AttrWidth,
    };

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
    }
}
