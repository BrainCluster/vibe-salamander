// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.Operations;

namespace VibeSalamander.Plugins.Ftp;

/// <summary>
/// FTP, FTPS and SFTP servers in the panels. A panel lists through one connection per server and user
/// (kept for a while, reconnected when the server dropped it); every copy, move or delete in the
/// transfer panel opens its own connection, so browsing goes on while files are copied.
/// </summary>
internal sealed class RemoteFileSystem : IPluginFileSystem, IDisposable
{
    private readonly IPluginHost _host;
    private readonly ConnectionStore _store;
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Timer _idle;

    /// <summary>A server the user logged in to: its credentials and the connection the panels list through.</summary>
    private sealed class Session
    {
        public required RemotePath Server { get; init; }
        public required Credentials Credentials { get; set; }
        public IRemoteClient? Client { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public DateTime LastUse { get; set; } = DateTime.UtcNow;
        public SessionLog Log { get; } = new();
    }

    /// <summary>What happened on a server's connections: the commands and the answers (Show Log).</summary>
    internal sealed class SessionLog
    {
        private const int MaxLines = 5000;
        private readonly List<string> _lines = new();

        /// <summary>A new line (any thread).</summary>
        public event Action<string>? Added;

        public void Add(string line)
        {
            string stamped = $"{DateTime.Now:HH:mm:ss}  {line}";
            lock (_lines)
            {
                _lines.Add(stamped);
                if (_lines.Count > MaxLines)
                    _lines.RemoveRange(0, _lines.Count - MaxLines);
            }
            Added?.Invoke(stamped);
        }

        public List<string> Lines
        {
            get
            {
                lock (_lines)
                    return _lines.ToList();
            }
        }
    }

    public RemoteFileSystem(IPluginHost host, ConnectionStore store)
    {
        _host = host;
        _store = store;
        // idle panel connections are closed (servers drop them anyway); the next listing reconnects
        _idle = new Timer(_ => CloseIdle(TimeSpan.FromMinutes(3)), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public string DisplayName => "FTP / SFTP";

    private string T(string text) => _host.Ui.T(text);

    public bool CanHandle(string path) => RemotePath.Parse(path) != null;

    public string GetDisplayPath(string path) => RemotePath.Parse(path)?.ToString() ?? path;

    public string? GetParent(string path) => RemotePath.Parse(path)?.Parent?.ToString();

    public string Combine(string directory, string name) =>
        RemotePath.Parse(directory) is { } parsed ? parsed.Child(name).ToString() : directory.TrimEnd('/') + "/" + name;

    private static RemotePath ParseOrThrow(string path) =>
        RemotePath.Parse(path) ?? throw new ArgumentException($"\"{path}\" is not an FTP or SFTP path.");

    // ---- sessions -----------------------------------------------------------------------------------

    private void CloseIdle(TimeSpan after)
    {
        List<Session> sessions;
        lock (_sessions)
            sessions = _sessions.Values.ToList();
        foreach (Session session in sessions)
        {
            if (DateTime.UtcNow - session.LastUse < after || session.Client == null || !session.Gate.Wait(0))
                continue;
            try
            {
                session.Client?.Dispose();
                session.Client = null;
            }
            finally
            {
                session.Gate.Release();
            }
        }
    }

    /// <summary>Remembers the credentials of a server (from the Connections window).</summary>
    public void Remember(RemotePath server, Credentials credentials)
    {
        lock (_sessions)
        {
            if (_sessions.TryGetValue(server.ServerKey, out Session? session))
            {
                session.Credentials = credentials;
                session.Client?.Dispose();
                session.Client = null;
            }
            else
            {
                _sessions[server.ServerKey] = new Session { Server = server with { Path = "/" }, Credentials = credentials };
            }
        }
    }

    /// <summary>
    /// The session of a server: known from before, from a saved connection, or with a password asked now.
    /// </summary>
    private Session GetSession(RemotePath path)
    {
        lock (_sessions)
        {
            if (_sessions.TryGetValue(path.ServerKey, out Session? known))
                return known;
        }
        Credentials credentials;
        Connection? saved = _store.Find(path);
        if (saved != null && (saved.Password != null || !string.IsNullOrEmpty(saved.PrivateKeyFile)))
            credentials = new Credentials(saved.Password ?? string.Empty, saved.PrivateKeyFile);
        else if (path.Protocol != RemoteProtocol.Sftp && path.User.Length == 0)
            credentials = new Credentials(string.Empty, null); // anonymous FTP
        else
            credentials = AskPassword(path, saved?.PrivateKeyFile, wrong: false) ?? throw new OperationCanceledException();
        var session = new Session { Server = path with { Path = "/" }, Credentials = credentials };
        lock (_sessions)
            _sessions[path.ServerKey] = session;
        return session;
    }

    /// <summary>Asks for the password (or the key's passphrase); null when cancelled. Background thread.</summary>
    private Credentials? AskPassword(RemotePath server, string? keyFile, bool wrong)
    {
        var password = new PluginField { Label = T(keyFile != null ? "Passphrase of the private key" : "Password"), Kind = PluginFieldKind.Password };
        string description = (wrong ? T("The login failed. Check the password and try again.") + "\n" : string.Empty) +
                             server.ServerKey.Replace("%40", "@");
        bool ok = _host.Ui.EditFieldsAsync(T("Log in"), description, new[] { password }).GetAwaiter().GetResult();
        return ok ? new Credentials(password.Value, keyFile) : null;
    }

    /// <summary>A new connection to the session's server (asks for the password again when the login fails).</summary>
    private async Task<IRemoteClient> ConnectAsync(Session session, CancellationToken ct, Action<string>? log = null)
    {
        for (int attempt = 0; ; attempt++)
        {
            IRemoteClient client = RemoteClients.Create(session.Server, session.Credentials, Trust);
            client.Log = line =>
            {
                session.Log.Add(line);
                log?.Invoke(line);
            };
            try
            {
                await client.ConnectAsync(ct);
                client.Log = session.Log.Add; // later commands go to the server's log only (the window may be closed)
                return client;
            }
            catch (UnauthorizedAccessException ex) when (attempt < 3)
            {
                log?.Invoke(ex.Message);
                client.Dispose();
                Credentials? again = AskPassword(session.Server, session.Credentials.PrivateKeyFile, wrong: true);
                if (again == null)
                    throw new OperationCanceledException();
                session.Credentials = again;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }

    /// <summary>Runs an operation on the session's panel connection (connecting first, once more when it was dropped).</summary>
    private async Task<T> WithSessionAsync<T>(RemotePath path, Func<IRemoteClient, Task<T>> work, CancellationToken ct)
    {
        Session session = GetSession(path);
        await session.Gate.WaitAsync(ct);
        try
        {
            session.LastUse = DateTime.UtcNow;
            for (int attempt = 0; ; attempt++)
            {
                if (session.Client is not { IsConnected: true })
                {
                    session.Client?.Dispose();
                    session.Client = null;
                    session.Client = await ConnectAsync(session, ct);
                }
                try
                {
                    return await work(session.Client);
                }
                catch (Exception ex) when (attempt == 0 && IsConnectionLost(ex, session.Client))
                {
                    session.Client?.Dispose();
                    session.Client = null; // the server closed it while idle: once more with a new one
                }
            }
        }
        finally
        {
            session.LastUse = DateTime.UtcNow;
            session.Gate.Release();
        }
    }

    private static bool IsConnectionLost(Exception ex, IRemoteClient? client) =>
        ex is not OperationCanceledException && ex is not UnauthorizedAccessException &&
        (client == null || !client.IsConnected || ex is IOException or System.Net.Sockets.SocketException or TimeoutException ||
         ex.GetType().Name.Contains("Connection", StringComparison.Ordinal));

    /// <summary>A server's key or certificate: trusted before, or asked now (and remembered when trusted).</summary>
    private bool Trust(string server, string fingerprint, string description, bool changed)
    {
        string? known = _store.TrustedKey(server + " " + description.Split(' ')[0]);
        if (known == fingerprint)
            return true;
        string message = known == null
            ? string.Format(T("{0} is not known yet. Its {1} fingerprint is:\n\n{2}\n\nDo you trust this server? The answer is remembered."), server, description, fingerprint)
            : string.Format(T("WARNING: the {1} of {0} has CHANGED. Someone may be intercepting the connection, or the server was reinstalled.\n\nNew fingerprint:\n{2}\n\nTrust the new one?"), server, description, fingerprint);
        bool trusted = _host.Ui.ConfirmAsync(T("Server identity"), message).GetAwaiter().GetResult();
        if (trusted)
            _store.Trust(server + " " + description.Split(' ')[0], fingerprint);
        return trusted;
    }

    // ---- the server tools (FTP / SFTP menu) -----------------------------------------------------------

    /// <summary>Logged in to the path's server (a panel shows it, or it was used since the last Disconnect).</summary>
    public bool IsConnected(string path)
    {
        if (RemotePath.Parse(path) is not { } server)
            return false;
        lock (_sessions)
            return _sessions.ContainsKey(server.ServerKey);
    }

    /// <summary>The log of the path's server, or null when there is no session.</summary>
    public SessionLog? LogOf(string path)
    {
        if (RemotePath.Parse(path) is not { } server)
            return null;
        lock (_sessions)
            return _sessions.TryGetValue(server.ServerKey, out Session? session) ? session.Log : null;
    }

    /// <summary>The server's SSH host key or TLS certificate (connecting when needed); null for plain FTP.</summary>
    public Task<string?> IdentityAsync(string path, CancellationToken ct) =>
        WithSessionAsync(ParseOrThrow(path), client => Task.FromResult(client.Identity), ct);

    public Task<string> SendCommandAsync(string path, string command, CancellationToken ct) =>
        WithSessionAsync(ParseOrThrow(path), client => client.SendCommandAsync(command, ct), ct);

    public Task<List<string>> RawListingAsync(string path, CancellationToken ct)
    {
        RemotePath directory = ParseOrThrow(path);
        return WithSessionAsync(directory, client => client.RawListingAsync(directory.Path, ct), ct);
    }

    /// <summary>The panel left the server: its connection and the password typed for it are let go.</summary>
    public void Disconnect(string path)
    {
        if (RemotePath.Parse(path) is not { } server)
            return;
        Session? session;
        lock (_sessions)
        {
            if (_sessions.Remove(server.ServerKey, out session) && session.Client != null)
            {
                try
                {
                    session.Client.Dispose();
                }
                catch (Exception)
                {
                    // closing anyway
                }
                session.Client = null;
            }
        }
    }

    /// <summary>
    /// Connects to a saved server and returns the directory to open (Connections window → Connect).
    /// <paramref name="log"/> gets the connection's steps.
    /// </summary>
    public async Task<string> OpenAsync(Connection connection, CancellationToken ct, Action<string>? log = null)
    {
        RemotePath root = connection.Root;
        string? password = connection.Password;
        if (password == null && string.IsNullOrEmpty(connection.PrivateKeyFile) && (connection.Protocol == RemoteProtocol.Sftp || connection.User.Length > 0))
        {
            Credentials? asked = AskPassword(root, null, wrong: false);
            if (asked == null)
                throw new OperationCanceledException();
            password = asked.Password;
        }
        Remember(root, new Credentials(password ?? string.Empty, string.IsNullOrWhiteSpace(connection.PrivateKeyFile) ? null : connection.PrivateKeyFile));
        Session session = GetSession(root);
        await session.Gate.WaitAsync(ct);
        try
        {
            session.Client?.Dispose();
            session.Client = null;
            session.Client = await ConnectAsync(session, ct, log);
            session.LastUse = DateTime.UtcNow;
            string directory = string.IsNullOrWhiteSpace(connection.InitialPath) ? await session.Client.GetHomeAsync(ct) : connection.InitialPath!;
            if (string.IsNullOrEmpty(directory))
                directory = "/";
            log?.Invoke(string.Format(T("Directory: {0}"), directory));
            return root.WithPath(directory).ToString();
        }
        finally
        {
            session.Gate.Release();
        }
    }

    // ---- listing ------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken cancellationToken)
    {
        RemotePath directory = ParseOrThrow(path);
        List<RemoteEntry> entries = await WithSessionAsync(directory, client => client.ListAsync(directory.Path, cancellationToken), cancellationToken);
        return entries.Select(e => ToItem(directory, e)).ToList();
    }

    private static RemoteItem ToItem(RemotePath directory, RemoteEntry e) =>
        new(e.Name, directory.Child(e.Name).ToString(), e.IsDirectory, e.Size, e.LastWrite, IsHidden: e.Name.StartsWith('.'), IsLink: e.IsLink);

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        RemotePath target = ParseOrThrow(path);
        return WithSessionAsync(target, async client =>
        {
            await client.CreateDirectoryAsync(target.Path, cancellationToken);
            return true;
        }, cancellationToken);
    }

    public Task RenameAsync(string path, string newPath, CancellationToken cancellationToken)
    {
        RemotePath from = ParseOrThrow(path), to = ParseOrThrow(newPath);
        return WithSessionAsync(from, async client =>
        {
            if (await client.StatAsync(to.Path, cancellationToken) != null)
                throw new IOException(string.Format(T("\"{0}\" already exists."), to.Name));
            await client.RenameAsync(from.Path, to.Path, cancellationToken);
            return true;
        }, cancellationToken);
    }

    // ---- transfers (each with its own connection) ---------------------------------------------------

    private async Task<OperationResult> RunJobAsync(RemotePath server, IArchiveJob job, Func<IRemoteClient, OperationResult, Task> work)
    {
        var result = new OperationResult();
        Session session = GetSession(server);
        IRemoteClient client;
        try
        {
            client = await ConnectAsync(session, job.Cancellation);
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
            return result;
        }
        using (client)
        {
            try
            {
                await work(client, result);
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
            }
        }
        return result;
    }

    public Task<OperationResult> DownloadAsync(IReadOnlyList<RemoteItem> items, string targetDirectory, IArchiveJob job)
    {
        if (items.Count == 0)
            return Task.FromResult(new OperationResult());
        RemotePath server = ParseOrThrow(items[0].Path);
        return RunJobAsync(server, job, (client, result) => new Transfer(client, job, result).DownloadAsync(server, items, targetDirectory));
    }

    public Task<OperationResult> UploadAsync(IReadOnlyList<string> sources, string targetDirectory, IArchiveJob job)
    {
        RemotePath target = ParseOrThrow(targetDirectory);
        return RunJobAsync(target, job, (client, result) => new Transfer(client, job, result).UploadAsync(sources, target));
    }

    public Task<OperationResult> DeleteAsync(IReadOnlyList<RemoteItem> items, IArchiveJob job)
    {
        if (items.Count == 0)
            return Task.FromResult(new OperationResult());
        RemotePath server = ParseOrThrow(items[0].Path);
        return RunJobAsync(server, job, (client, result) => new Transfer(client, job, result).DeleteAsync(items));
    }

    internal string Text(string english) => T(english);

    public void Dispose()
    {
        _idle.Dispose();
        lock (_sessions)
        {
            foreach (Session session in _sessions.Values)
                session.Client?.Dispose();
            _sessions.Clear();
        }
    }
}
