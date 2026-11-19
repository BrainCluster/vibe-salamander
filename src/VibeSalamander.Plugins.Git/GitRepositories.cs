// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using LibGit2Sharp;

namespace VibeSalamander.Plugins.Git;

/// <summary>
/// The repositories the panels have shown, read by libgit2 inside this program: opening one never runs a hook, a
/// filter, fsmonitor or any other program its configuration names (libgit2 runs none). Each is opened once and
/// used under its own lock (LibGit2Sharp isn't thread-safe); its .git folder is watched, so a commit or a change
/// to the index made elsewhere decorates the panels again.
/// </summary>
internal sealed class GitRepositories : IDisposable
{
    private readonly Dictionary<string, Entry> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    static GitRepositories()
    {
        // libgit2 refuses repositories owned by another user (git's safe.directory). That check protects programs that
        // run what a repository's configuration says; libgit2 runs nothing, so reading such a repository is safe.
        try
        {
            GlobalSettings.SetOwnerValidation(false);
        }
        catch (Exception)
        {
            // an older libgit2 without the check
        }
    }

    /// <summary>A repository's working tree changed in .git (index, HEAD, refs): its root.</summary>
    public event EventHandler<string>? Changed;

    private sealed class Entry : IDisposable
    {
        public required Repository Repository { get; init; }
        public required string Root { get; init; }
        public FileSystemWatcher? Watcher { get; set; }
        public Timer? Debounce { get; set; }
        public object Lock { get; } = new();

        public void Dispose()
        {
            Watcher?.Dispose();
            Debounce?.Dispose();
            Repository.Dispose();
        }
    }

    /// <summary>
    /// The working tree root of the repository a folder is in ("C:\src\app"), or null: none, a bare repository, or
    /// the folder is the .git folder itself (or inside it).
    /// </summary>
    public static string? RootOf(string folder)
    {
        if (string.IsNullOrEmpty(folder) || folder.Contains("://", StringComparison.Ordinal) || !Directory.Exists(folder))
            return null;
        string normal = folder.TrimEnd('\\') + "\\";
        if (normal.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase))
            return null;
        string? gitDir;
        try
        {
            gitDir = Repository.Discover(folder);
        }
        catch (Exception)
        {
            return null;
        }
        if (gitDir == null)
            return null;
        try
        {
            using var repository = new Repository(gitDir);
            return repository.Info.IsBare || repository.Info.WorkingDirectory == null ? null : repository.Info.WorkingDirectory.TrimEnd('\\');
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Runs <paramref name="read"/> on the repository at <paramref name="root"/> (opened once, then kept), under its lock.</summary>
    public T Read<T>(string root, Func<Repository, T> read)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_open.TryGetValue(root, out entry!))
            {
                entry = new Entry { Repository = new Repository(root), Root = root };
                Watch(entry);
                _open[root] = entry;
            }
        }
        lock (entry.Lock)
            return read(entry.Repository);
    }

    /// <summary>Forgets a repository (it's read again from disk next time: after git.exe changed it).</summary>
    public void Refresh(string root)
    {
        lock (_gate)
        {
            if (_open.Remove(root, out Entry? entry))
                lock (entry.Lock)
                    entry.Dispose();
        }
        Changed?.Invoke(this, root);
    }

    private void Watch(Entry entry)
    {
        try
        {
            string gitDir = entry.Repository.Info.Path;
            var watcher = new FileSystemWatcher(gitDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
            };
            entry.Debounce = new Timer(_ => Changed?.Invoke(this, entry.Root), null, Timeout.Infinite, Timeout.Infinite);
            void Touched(object? sender, FileSystemEventArgs e)
            {
                // what decorations depend on: the index, HEAD, refs (branches, the remote's), a merge or rebase going on
                string name = e.Name ?? string.Empty;
                if (name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) || name.StartsWith("objects", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("logs", StringComparison.OrdinalIgnoreCase))
                    return;
                entry.Debounce?.Change(400, Timeout.Infinite);
            }
            watcher.Changed += Touched;
            watcher.Created += Touched;
            watcher.Deleted += Touched;
            watcher.Renamed += (s, e) => Touched(s, e);
            watcher.EnableRaisingEvents = true;
            entry.Watcher = watcher;
        }
        catch (Exception)
        {
            // not watched (a network drive): refreshes of the panel still read it again
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (Entry entry in _open.Values)
                entry.Dispose();
            _open.Clear();
        }
    }
}
