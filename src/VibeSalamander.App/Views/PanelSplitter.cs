using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace VibeSalamander.Views;

/// <summary>Vertical splitter between the panels (shows the resize cursor).</summary>
public sealed partial class PanelSplitter : Grid
{
    public PanelSplitter()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}
