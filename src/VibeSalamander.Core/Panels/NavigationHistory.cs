namespace VibeSalamander.Core.Panels;

/// <summary>Back/forward history of one panel (Alt+Left / Alt+Right).</summary>
public sealed class NavigationHistory
{
    private const int MaxEntries = 50;
    private readonly List<string> _back = new();
    private readonly List<string> _forward = new();

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>Call when the panel leaves <paramref name="previousPath"/> by normal navigation.</summary>
    public void Push(string previousPath)
    {
        if (_back.Count > 0 && string.Equals(_back[^1], previousPath, StringComparison.OrdinalIgnoreCase))
            return;
        _back.Add(previousPath);
        if (_back.Count > MaxEntries)
            _back.RemoveAt(0);
        _forward.Clear();
    }

    public string? GoBack(string currentPath)
    {
        if (_back.Count == 0) return null;
        string target = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        _forward.Add(currentPath);
        return target;
    }

    public string? GoForward(string currentPath)
    {
        if (_forward.Count == 0) return null;
        string target = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        _back.Add(currentPath);
        return target;
    }
}
