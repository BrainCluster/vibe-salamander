// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.Diagnostics;
using VibeSalamander.Plugins;
using VibeSalamander.Services;

namespace VibeSalamander.ViewModels;

// MainViewModel: plugins' panel decorations (IPluginPanelDecorator: Git's colors, its column, the branch).
public sealed partial class MainViewModel
{
    private readonly Dictionary<PaneViewModel, CancellationTokenSource> _decorating = new();

    /// <summary>After the plugins have loaded: panels are decorated after each listing and when a decorator says so.</summary>
    private void StartDecorations()
    {
        var decorators = Plugins.Decorators.ToList();
        if (decorators.Count == 0)
            return;
        foreach (PaneViewModel pane in new[] { Left, Right })
        {
            PaneViewModel p = pane;
            p.ListingChanged += (_, _) => _ = DecorateAsync(p);
            _ = DecorateAsync(p);
        }
        foreach ((IPluginPanelDecorator decorator, PluginInfo _) in decorators)
            decorator.Changed += (_, folder) => _dispatcher.TryEnqueue(() => DecorateUnder(folder));
    }

    /// <summary>The panels showing the folder or one inside it, decorated again.</summary>
    private void DecorateUnder(string folder)
    {
        string root = folder.TrimEnd('\\') + "\\";
        foreach (PaneViewModel pane in new[] { Left, Right })
        {
            string path = pane.Path.TrimEnd('\\') + "\\";
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                _ = DecorateAsync(pane);
        }
    }

    /// <summary>
    /// Asks the decorators about the folder shown (local disks only; the first with an answer wins, 3 s at most each).
    /// The last decoration of the same folder shows at once, so a refresh doesn't flicker.
    /// </summary>
    private async Task DecorateAsync(PaneViewModel pane)
    {
        if (_decorating.Remove(pane, out CancellationTokenSource? previous))
            previous.Cancel();
        string path = pane.Path;
        if (!pane.IsLocalDisk || pane.IsThisPc || string.IsNullOrEmpty(path))
        {
            pane.ApplyDecoration(null, null);
            return;
        }
        if (pane.LastDecoration is { } last && string.Equals(last.Path, path, StringComparison.OrdinalIgnoreCase))
            pane.ApplyDecoration(last.Decoration, last.Header);
        var cancel = new CancellationTokenSource();
        _decorating[pane] = cancel;
        foreach ((IPluginPanelDecorator decorator, PluginInfo plugin) in Plugins.Decorators.ToList())
        {
            PanelDecoration? decoration = null;
            try
            {
                Task<PanelDecoration?> asked = Task.Run(() => decorator.DecorateAsync(path, cancel.Token), cancel.Token);
                Task done = await Task.WhenAny(asked, Task.Delay(TimeSpan.FromSeconds(3), cancel.Token));
                if (cancel.IsCancellationRequested)
                    return; // the panel went elsewhere (or a newer request runs)
                if (done == asked && asked.IsCompletedSuccessfully)
                    decoration = asked.Result;
                else if (asked.IsFaulted)
                    ErrorLog.Exception($"Panel decorator {plugin.Name}/{decorator.Id}", asked.Exception!.GetBaseException());
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (decoration == null)
                continue;
            if (string.Equals(pane.Path, path, StringComparison.OrdinalIgnoreCase))
                pane.ApplyDecoration(decoration, decorator.ColumnHeader);
            return;
        }
        if (!cancel.IsCancellationRequested && string.Equals(pane.Path, path, StringComparison.OrdinalIgnoreCase))
            pane.ApplyDecoration(null, null);
    }
}
