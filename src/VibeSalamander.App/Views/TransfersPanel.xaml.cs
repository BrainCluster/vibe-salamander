using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VibeSalamander.ViewModels;

namespace VibeSalamander.Views;

/// <summary>
/// The transfer queue below the file panels (WinSCP-style): every copy, move and permanent delete
/// runs here in the background while the panels stay usable.
/// </summary>
public sealed partial class TransfersPanel : UserControl
{
    private TransfersViewModel? _vm;
    private bool _collapsed;

    public TransfersPanel()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the list is collapsed to its header or expanded again.</summary>
    public event EventHandler? CollapsedChanged;

    /// <summary>Raised after a button was clicked, so the window can give the focus back to the panels.</summary>
    public event EventHandler? Interacted;

    public bool IsCollapsed
    {
        get => _collapsed;
        set
        {
            if (_collapsed == value)
                return;
            _collapsed = value;
            List.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            CollapseGlyph.Glyph = value ? "" : ""; // chevron up / down
            ToolTipService.SetToolTip(CollapseButton, value ? "Expand" : "Collapse");
            CollapsedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Height of the header alone (the collapsed panel).</summary>
    public const double HeaderHeight = 32;

    public void Initialize(TransfersViewModel vm)
    {
        _vm = vm;
        List.ItemsSource = vm.Items;
        vm.StateChanged += (_, _) => UpdateHeader();
        UpdateHeader();
    }

    private void UpdateHeader()
    {
        if (_vm == null)
            return;
        SummaryText.Text = _vm.SummaryText;
        CancelAllButton.IsEnabled = _vm.ActiveCount > 0;
        ClearButton.IsEnabled = _vm.Items.Any(i => !i.IsActive);
    }

    private static TransferItemViewModel? ItemOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as TransferItemViewModel;

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        ItemOf(sender)?.Pause();
        Interacted?.Invoke(this, EventArgs.Empty);
    }

    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        ItemOf(sender)?.Resume();
        Interacted?.Invoke(this, EventArgs.Empty);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        ItemOf(sender)?.Cancel();
        Interacted?.Invoke(this, EventArgs.Empty);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
            _vm?.Remove(item);
        Interacted?.Invoke(this, EventArgs.Empty);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _vm?.ClearFinished();
        Interacted?.Invoke(this, EventArgs.Empty);
    }

    private void CancelAll_Click(object sender, RoutedEventArgs e)
    {
        _vm?.CancelAll();
        Interacted?.Invoke(this, EventArgs.Empty);
    }

    private void Collapse_Click(object sender, RoutedEventArgs e)
    {
        IsCollapsed = !IsCollapsed;
        Interacted?.Invoke(this, EventArgs.Empty);
    }
}
