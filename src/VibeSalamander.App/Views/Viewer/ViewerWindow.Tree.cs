// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VibeSalamander.Core.Viewing;
using VibeSalamander.Services;

namespace VibeSalamander.Views.Viewer;

// ViewerWindow: JSON, XML and YAML as a tree.
internal sealed partial class ViewerWindow
{
    /// <summary>A tree row: "name: value" or "name {3}" (the TreeView shows its text).</summary>
    private sealed class TreeRow
    {
        public TreeRow(TreeNode node) => Node = node;

        public TreeNode Node { get; }

        public override string ToString() => Node.Kind switch
        {
            TreeNodeKind.Object or TreeNodeKind.Array => $"{Node.Label}  {Node.Summary}",
            TreeNodeKind.Element when Node.Children.Count > 0 => Node.Value is { Length: > 0 } v ? $"{Node.Label}  {Shorten(v)}" : Node.Label,
            TreeNodeKind.String => $"{Node.Label}: \"{Shorten(Node.Value ?? string.Empty)}\"",
            _ => Node.Value is { } value ? $"{Node.Label}: {Shorten(value)}" : Node.Label,
        };

        private static string Shorten(string text)
        {
            text = text.ReplaceLineEndings(" ");
            return text.Length > 300 ? text[..300] + "…" : text;
        }
    }

    private TreeView? _tree;
    private Grid? _treeHost;
    private TreeNode? _treeRoot;
    private string? _treePath;
    private Dictionary<TreeNode, TreeNode>? _parents;
    private readonly Dictionary<TreeNode, TreeViewNode> _shownNodes = new();
    private TreeNode? _treeFound;

    private async Task ShowTreeAsync()
    {
        if (_treeHost == null)
        {
            _tree = new TreeView { SelectionMode = TreeViewSelectionMode.Single, Padding = new Thickness(8, 4, 8, 4) };
            _tree.Expanding += (_, e) => Realize(e.Node);
            _treeHost = new Grid();
            _treeHost.Children.Add(_tree);
            _content.Children.Add(_treeHost);
        }
        _treeHost.Visibility = Visibility.Visible;
        if (_treePath == _path && _treeRoot != null)
            return;
        _tree!.RootNodes.Clear();
        _shownNodes.Clear();
        _treeRoot = null;
        _treePath = _path;
        StructuredFormat format = Structured.FormatOf(_path) ?? StructuredFormat.Xml;
        string path = _path;
        _status.Text = Loc.T("Reading...");
        (TreeNode? root, ParseProblem? problem, string? error) = await Task.Run(() =>
        {
            try
            {
                LoadedText text = TextDocuments.Load(path, _chosenEncoding);
                var (node, parseProblem) = Structured.Parse(text.Text, format);
                return (node, parseProblem, (string?)null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ((TreeNode?)null, (ParseProblem?)null, ex.Message);
            }
        });
        if (_closed || path != _path || _mode != ViewerMode.Tree)
            return;
        _status.Text = string.Empty;
        if (error != null)
        {
            ShowMessage(error);
            return;
        }
        if (root == null)
        {
            // where the problem is: one key to see it in the text
            ShowMessage(Loc.F("The {0} can't be read as a tree: line {1}, column {2}: {3}", format.ToString().ToUpperInvariant(),
                problem?.Line ?? 0, problem?.Column ?? 0, problem?.Message ?? string.Empty) + "\n\n" + Loc.T("Text (on the toolbar) shows the text."));
            _treePath = null;
            if (problem is { Line: > 0 } p)
                _pendingLine = p.Line;
            return;
        }
        _treeRoot = root;
        _parents = new Dictionary<TreeNode, TreeNode>();
        foreach (TreeNode node in new[] { root }.Concat(root.Descendants()))
            foreach (TreeNode child in node.Children)
                _parents[child] = node;
        TreeViewNode top = NodeFor(root);
        _tree.RootNodes.Add(top);
        Realize(top);
        top.IsExpanded = true;
        ExpandLevels(top, 200);
        _status.Text = Loc.F("{0} nodes", (root.Descendants().Count() + 1).ToString("N0", System.Globalization.CultureInfo.CurrentCulture));
    }

    /// <summary>
    /// Opens the tree level by level while it stays short (about <paramref name="budget"/> rows): a small file is
    /// seen whole, a large one shows its outline.
    /// </summary>
    private void ExpandLevels(TreeViewNode top, int budget)
    {
        int shown = 1 + top.Children.Count;
        List<TreeViewNode> level = top.Children.ToList();
        while (level.Count > 0)
        {
            var next = new List<TreeViewNode>();
            foreach (TreeViewNode node in level)
            {
                if (node.Content is not TreeRow row || row.Node.Children.Count == 0)
                    continue;
                if (shown + row.Node.Children.Count > budget)
                    return;
                Realize(node);
                node.IsExpanded = true;
                shown += node.Children.Count;
                next.AddRange(node.Children);
            }
            level = next;
        }
    }

    /// <summary>A line to show when the text view opens (after a tree problem).</summary>
    private long _pendingLine;

    private TreeViewNode NodeFor(TreeNode node)
    {
        var viewNode = new TreeViewNode { Content = new TreeRow(node), HasUnrealizedChildren = node.Children.Count > 0 };
        _shownNodes[node] = viewNode;
        return viewNode;
    }

    /// <summary>The children of a tree node are made when it's expanded (large documents stay quick).</summary>
    private void Realize(TreeViewNode viewNode)
    {
        if (!viewNode.HasUnrealizedChildren || viewNode.Content is not TreeRow row)
            return;
        viewNode.HasUnrealizedChildren = false;
        foreach (TreeNode child in row.Node.Children)
            viewNode.Children.Add(NodeFor(child));
    }

    private void FindInTree(bool backwards)
    {
        if (_treeRoot == null || _tree == null)
            return;
        FindOptions options = FindOptions();
        if (TextSearch.Problem(options) is { } problem)
        {
            _findStatus.Text = problem;
            return;
        }
        var match = TextSearch.Matcher(options);
        bool Fits(TreeNode node) => match(node.Label, 0, false) != null || (node.Value != null && match(node.Value, 0, false) != null);
        List<TreeNode> all = new[] { _treeRoot }.Concat(_treeRoot.Descendants()).ToList();
        int start = _treeFound != null ? all.IndexOf(_treeFound) : -1;
        for (int step = 1; step <= all.Count; step++)
        {
            int index = ((start + (backwards ? -step : step)) % all.Count + all.Count) % all.Count;
            if (!Fits(all[index]))
                continue;
            _treeFound = all[index];
            RevealInTree(all[index]);
            bool wrapped = backwards ? index > start && start >= 0 : index < start;
            _findStatus.Text = wrapped ? Loc.T(backwards ? "Found (from the end again)" : "Found (from the top again)") : string.Empty;
            return;
        }
        _findStatus.Text = Loc.T("Not found");
    }

    /// <summary>Expands the way to a node, selects it and scrolls to it.</summary>
    private void RevealInTree(TreeNode node)
    {
        if (_tree == null || _parents == null)
            return;
        var path = new List<TreeNode>();
        for (TreeNode? n = node; n != null; n = _parents.TryGetValue(n, out TreeNode? parent) ? parent : null)
            path.Insert(0, n);
        foreach (TreeNode step in path.Take(path.Count - 1))
        {
            if (_shownNodes.TryGetValue(step, out TreeViewNode? viewNode))
            {
                Realize(viewNode);
                viewNode.IsExpanded = true;
            }
        }
        if (_shownNodes.TryGetValue(node, out TreeViewNode? target))
        {
            _tree.SelectedNode = target;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_tree.ContainerFromNode(target) is TreeViewItem item)
                    item.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.3 });
            });
        }
    }

    private void CloseTree()
    {
        _tree?.RootNodes.Clear();
        _shownNodes.Clear();
        _treeRoot = null;
        _treePath = null;
        _parents = null;
        _treeFound = null;
    }
}
