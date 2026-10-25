// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Plugins.Ftp;

/// <summary>
/// The built-in FTP / SFTP plugin (Open Salamander's FTP Client, with SFTP): saved servers in the
/// Connections window (Commands → Connect to FTP / SFTP Server), and the server in the panel like a
/// directory, with copies, moves and deletes in the transfer panel.
/// </summary>
public sealed class FtpPlugin : IVibePlugin, ICommandContributor
{
    public string Id => "vibe.ftp";
    public string DisplayName => "FTP / SFTP";
    public Version Version => new(1, 0);
    public string Description => "FTP, FTPS and SFTP servers in the panels: browse, copy both ways, move, delete, rename and create directories.";
    public string Author => "Vibe Salamander";
    public string? IconGlyph => "\uE968";

    private IPluginHost? _host;
    private ConnectionStore? _store;
    private RemoteFileSystem? _fileSystem;
    private IPluginListWindow? _window;

    public IReadOnlyList<PluginCommand> Commands { get; private set; } = Array.Empty<PluginCommand>();

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _store = new ConnectionStore(host.GetDataDirectory(this));
        _fileSystem = new RemoteFileSystem(host, _store);
        host.RegisterFileSystem(_fileSystem);
        Commands = new[]
        {
            new PluginCommand("connect", "Connect to FTP / SFTP Server...", null, h =>
            {
                ShowConnections(h);
                return Task.CompletedTask;
            }, HostCommand: "ConnectFtp"),
            new PluginCommand("disconnect", "Disconnect", null, DisconnectAsync, SeparatorBefore: true),
            new PluginCommand("bookmark", "Add bookmark...", null, AddBookmarkAsync),
            new PluginCommand("log", "Show log", null, h =>
            {
                ShowLog(h);
                return Task.CompletedTask;
            }, SeparatorBefore: true),
            new PluginCommand("identity", "Show certificate / host key", null, ShowIdentityAsync),
            new PluginCommand("command", "Send command...", null, SendCommandAsync, SeparatorBefore: true),
            new PluginCommand("raw", "Show raw listing", null, ShowRawListingAsync),
        };
        host.RegisterCommands(this);
    }

    public void Shutdown() => _fileSystem?.Dispose();

    private string T(string text) => _host!.Ui.T(text);

    private static string ProtocolName(RemoteProtocol protocol) => protocol switch
    {
        RemoteProtocol.Sftp => "SFTP (SSH)",
        RemoteProtocol.FtpsExplicit => "FTPS (explicit TLS)",
        RemoteProtocol.FtpsImplicit => "FTPS (implicit TLS)",
        _ => "FTP",
    };

    private static readonly RemoteProtocol[] Protocols = { RemoteProtocol.Sftp, RemoteProtocol.Ftp, RemoteProtocol.FtpsExplicit, RemoteProtocol.FtpsImplicit };

    // ---- the server tools (they work on the server the active panel shows) -------------------------------

    /// <summary>The active panel's path when it shows an FTP / SFTP server; else says so and returns null.</summary>
    private string? ServerPath(IPluginHost host)
    {
        string path = host.ActivePanel.Path;
        if (_fileSystem != null && _fileSystem.CanHandle(path))
            return path;
        host.Notify(T("The active panel doesn't show an FTP or SFTP server."), isError: true);
        return null;
    }

    private static string ServerName(string path) => RemotePath.Parse(path)?.ServerKey.Replace("%40", "@") ?? path;

    /// <summary>Closes the connection; the panel goes to This PC (no question: that is what was asked for).</summary>
    private async Task DisconnectAsync(IPluginHost host)
    {
        if (ServerPath(host) is not { } path)
            return;
        _fileSystem!.Disconnect(path);
        await host.ActivePanel.NavigateAsync("thispc:");
        if (host.InactivePanel.Path is { } other && RemotePath.Parse(other) is { } o && RemotePath.Parse(path) is { } p && o.ServerKey == p.ServerKey)
            await host.InactivePanel.NavigateAsync("thispc:");
        host.Notify(string.Format(T("Disconnected from {0}."), ServerName(path)));
    }

    /// <summary>The server and the directory shown, saved as a server in the Connections window.</summary>
    private async Task AddBookmarkAsync(IPluginHost host)
    {
        if (ServerPath(host) is not { } path || RemotePath.Parse(path) is not { } where || _store == null)
            return;
        Connection? saved = _store.Find(where);
        Connection bookmark = saved?.Clone() ?? new Connection
        {
            Protocol = where.Protocol,
            Host = where.Host,
            Port = where.Port,
            User = where.User,
        };
        bookmark.InitialPath = where.Path;
        string folder = where.IsRoot ? "/" : where.Name;
        var name = new PluginField { Label = T("Name"), Value = $"{saved?.DisplayName ?? where.Host} – {folder}" };
        if (!await host.Ui.EditFieldsAsync(T("Add Bookmark"), where.ToString().Replace("%40", "@"), new[] { name }))
            return;
        bookmark.Name = name.Value.Trim();
        _store.Connections.Add(bookmark);
        _store.SaveConnections();
        if (_window is { IsClosed: false } window)
            AddRow(window, bookmark);
        host.Notify(string.Format(T("\"{0}\" was added to the FTP / SFTP connections."), bookmark.DisplayName));
    }

    /// <summary>The commands and answers of the server's connections, live.</summary>
    private void ShowLog(IPluginHost host)
    {
        if (ServerPath(host) is not { } path)
            return;
        RemoteFileSystem.SessionLog? log = _fileSystem!.LogOf(path);
        IPluginListWindow window = host.Ui.OpenListWindow(string.Format(T("Log: {0}"), ServerName(path)), new[]
        {
            new PluginColumn(T("Message"), 0, Monospace: true),
        });
        window.RowMenu = row => new[] { new PluginMenuItem(T("Copy"), "Ctrl+C", () => host.Ui.CopyToClipboard(row[0])) };
        window.SetButtons(new[]
        {
            new PluginButton("copy", T("Copy all"), () => host.Ui.CopyToClipboard(string.Join("\r\n", window.Rows.Select(r => r[0])))),
            new PluginButton("close", T("Close"), window.Close, IsDefault: true),
        });
        if (log == null)
        {
            window.SetStatus(T("Nothing was logged yet: the server isn't connected."));
            return;
        }
        foreach (string line in log.Lines)
            window.AddRow(new[] { line });
        void Added(string line) => window.AddRow(new[] { line });
        log.Added += Added;
        window.Closed += (_, _) => log.Added -= Added;
        window.SetStatus(T("New lines appear as they come."));
    }

    /// <summary>Who the server is: its SSH host key or TLS certificate (Salamander: Show Certificate).</summary>
    private async Task ShowIdentityAsync(IPluginHost host)
    {
        if (ServerPath(host) is not { } path)
            return;
        string title = T("Certificate / Host Key");
        try
        {
            string? identity = await Task.Run(() => _fileSystem!.IdentityAsync(path, CancellationToken.None));
            await host.Ui.ShowMessageAsync(title, ServerName(path) + "\n\n" +
                (identity ?? T("Plain FTP isn't encrypted: the server shows no certificate. FTPS (TLS) or SFTP connections are encrypted.")));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await host.Ui.ShowMessageAsync(title, ex.Message);
        }
    }

    /// <summary>A raw command (FTP: "SITE CHMOD 644 file"; SFTP: a shell command over SSH) and the answer.</summary>
    private async Task SendCommandAsync(IPluginHost host)
    {
        if (ServerPath(host) is not { } path)
            return;
        bool sftp = RemotePath.Parse(path)?.Protocol == RemoteProtocol.Sftp;
        var command = new PluginField
        {
            Label = T("Command"),
            Value = _lastCommand,
            Placeholder = sftp ? "ls -la" : "SITE CHMOD 644 file.txt",
        };
        string description = ServerName(path) + "\n" + (sftp
            ? T("SFTP has no commands of its own: the command runs in the server's shell (SSH).")
            : T("A command of the FTP protocol; the server's answer is shown."));
        if (!await host.Ui.EditFieldsAsync(T("Send Command"), description, new[] { command }) || command.Value.Trim().Length == 0)
            return;
        _lastCommand = command.Value.Trim();
        IPluginListWindow window = host.Ui.OpenListWindow(T("Send Command") + " – " + _lastCommand, new[]
        {
            new PluginColumn(T("Answer"), 0, Monospace: true),
        });
        window.SetButtons(new[]
        {
            new PluginButton("copy", T("Copy all"), () => host.Ui.CopyToClipboard(string.Join("\r\n", window.Rows.Select(r => r[0])))),
            new PluginButton("close", T("Close"), window.Close, IsDefault: true),
        });
        window.SetStatus(T("Waiting for the server..."));
        window.SetProgress(0);
        try
        {
            string answer = await Task.Run(() => _fileSystem!.SendCommandAsync(path, _lastCommand, CancellationToken.None));
            foreach (string line in answer.Replace("\r\n", "\n").Split('\n'))
                window.AddRow(new[] { line });
            window.SetStatus(T("Done. The panel is read again (the command may have changed something)."));
            await host.ActivePanel.RefreshAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            window.AddRow(new[] { ex.Message });
            window.SetStatus(T("The command failed."));
        }
        finally
        {
            window.SetProgress(null);
        }
    }

    private string _lastCommand = string.Empty;

    /// <summary>The directory listing exactly as the server sends it (to see what the panel makes of it).</summary>
    private async Task ShowRawListingAsync(IPluginHost host)
    {
        if (ServerPath(host) is not { } path)
            return;
        IPluginListWindow window = host.Ui.OpenListWindow(string.Format(T("Raw Listing: {0}"), path.Replace("%40", "@")), new[]
        {
            new PluginColumn(T("Line"), 0, Monospace: true),
        });
        window.SetButtons(new[]
        {
            new PluginButton("copy", T("Copy all"), () => host.Ui.CopyToClipboard(string.Join("\r\n", window.Rows.Select(r => r[0])))),
            new PluginButton("close", T("Close"), window.Close, IsDefault: true),
        });
        window.SetProgress(0);
        try
        {
            List<string> lines = await Task.Run(() => _fileSystem!.RawListingAsync(path, CancellationToken.None));
            foreach (string line in lines)
                window.AddRow(new[] { line });
            window.SetStatus(string.Format(T("{0} lines"), lines.Count));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            window.SetStatus(ex.Message);
        }
        finally
        {
            window.SetProgress(null);
        }
    }

    // ---- the Connections window -------------------------------------------------------------------------

    private void ShowConnections(IPluginHost host)
    {
        if (_store == null)
            return;
        if (_window is { IsClosed: false })
            _window.Close();
        IPluginListWindow window = host.Ui.OpenListWindow(T("FTP / SFTP Connections"), new[]
        {
            new PluginColumn(T("Name"), 0),
            new PluginColumn(T("Protocol"), 140),
            new PluginColumn(T("Server"), 240),
            new PluginColumn(T("User"), 140),
        });
        _window = window;
        foreach (Connection connection in _store.Connections)
            AddRow(window, connection);
        window.RowMenu = row => new[]
        {
            new PluginMenuItem(T("Connect"), "Enter", () => _ = ConnectAsync(window, row)),
            new PluginMenuItem(T("Edit..."), "F4", () => _ = EditAsync(window, row)),
            new PluginMenuItem(T("Duplicate"), null, () => _ = DuplicateAsync(window, row)),
            new PluginMenuItem(T("Delete"), "Delete", () => _ = DeleteAsync(window, row)),
        };
        window.SetButtons(new[]
        {
            new PluginButton("connect", T("Connect"), () =>
            {
                if (window.SelectedRow is { } row)
                    _ = ConnectAsync(window, row);
            }, IsDefault: true),
            new PluginButton("new", T("New..."), () => _ = NewAsync(window)),
            new PluginButton("edit", T("Edit..."), () =>
            {
                if (window.SelectedRow is { } row)
                    _ = EditAsync(window, row);
            }),
            new PluginButton("delete", T("Delete"), () =>
            {
                if (window.SelectedRow is { } row)
                    _ = DeleteAsync(window, row);
            }),
        });
        window.SetStatus(_store.Connections.Count == 0
            ? T("No servers yet: add one with New.")
            : T("Enter connects to the selected server; it opens in the active panel."));
        if (_store.Connections.Count == 0)
            _ = NewAsync(window);
    }

    private void AddRow(IPluginListWindow window, Connection connection)
    {
        IPluginRow row = window.AddRow(Cells(connection));
        row.Tag = connection;
    }

    private static string[] Cells(Connection c) =>
        new[] { c.DisplayName, ProtocolName(c.Protocol), c.Port == RemotePath.DefaultPort(c.Protocol) ? c.Host : $"{c.Host}:{c.Port}", c.User };

    private static void Update(IPluginRow row, Connection c)
    {
        string[] cells = Cells(c);
        for (int i = 0; i < cells.Length; i++)
            row[i] = cells[i];
    }

    private async Task ConnectAsync(IPluginListWindow window, IPluginRow row)
    {
        if (row.Tag is not Connection connection || _fileSystem == null || _host == null)
            return;
        // the connection log, below the list
        window.ClearLog();
        void Log(string line) => window.AppendLog($"{DateTime.Now:HH:mm:ss}  {line}");
        window.SetStatus(string.Format(T("Connecting to {0}..."), connection.Host));
        window.UpdateButton("connect", enabled: false);
        try
        {
            string path = await Task.Run(() => _fileSystem.OpenAsync(connection, CancellationToken.None, Log));
            Log(T("Connected."));
            window.SetStatus(T("Connected."));
            await Task.Delay(700); // a moment to see the log
            window.Close();
            await _host.ActivePanel.NavigateAsync(path);
        }
        catch (OperationCanceledException)
        {
            Log(T("Not connected."));
            window.SetStatus(T("Not connected."));
        }
        catch (Exception ex)
        {
            Log(ex.Message);
            window.SetStatus(string.Format(T("{0}: {1}"), connection.Host, ex.Message));
        }
        finally
        {
            if (!window.IsClosed)
                window.UpdateButton("connect", enabled: true);
        }
    }

    private async Task NewAsync(IPluginListWindow window)
    {
        var connection = new Connection();
        if (!await EditConnectionAsync(window, connection, T("New Server")))
            return;
        _store!.Connections.Add(connection);
        _store.SaveConnections();
        AddRow(window, connection);
        window.SetStatus(T("Enter connects to the selected server; it opens in the active panel."));
    }

    private async Task EditAsync(IPluginListWindow window, IPluginRow row)
    {
        if (row.Tag is not Connection connection)
            return;
        Connection copy = connection.Clone();
        if (!await EditConnectionAsync(window, copy, T("Edit Server")))
            return;
        int index = _store!.Connections.IndexOf(connection);
        if (index >= 0)
            _store.Connections[index] = copy;
        _store.SaveConnections();
        row.Tag = copy;
        Update(row, copy);
    }

    private async Task DuplicateAsync(IPluginListWindow window, IPluginRow row)
    {
        if (row.Tag is not Connection connection)
            return;
        Connection copy = connection.Clone();
        copy.Name = connection.DisplayName + " (2)";
        if (!await EditConnectionAsync(window, copy, T("New Server")))
            return;
        _store!.Connections.Add(copy);
        _store.SaveConnections();
        AddRow(window, copy);
    }

    private async Task DeleteAsync(IPluginListWindow window, IPluginRow row)
    {
        if (row.Tag is not Connection connection)
            return;
        if (!await _host!.Ui.ConfirmAsync(T("Delete"), string.Format(T("Delete the server \"{0}\" from the list?"), connection.DisplayName), window))
            return;
        _store!.Connections.Remove(connection);
        _store.SaveConnections();
        row.Remove();
    }

    /// <summary>The server's form; false when cancelled. The password is saved only when asked to.</summary>
    private async Task<bool> EditConnectionAsync(IPluginListWindow window, Connection c, string title)
    {
        RemoteProtocol current = c.Protocol;
        var name = new PluginField { Label = T("Name"), Value = c.Name, Placeholder = T("(the server's name)") };
        var port = new PluginField { Label = T("Port"), Value = (c.Port > 0 ? c.Port : RemotePath.DefaultPort(c.Protocol)).ToString() };
        var protocol = new PluginField
        {
            Label = T("Protocol"),
            Kind = PluginFieldKind.Choice,
            Choices = Protocols.Select(ProtocolName).ToList(),
            Value = ProtocolName(c.Protocol),
            Changed = value =>
            {
                // the port follows the protocol unless it was changed by hand
                RemoteProtocol chosen = Protocols.FirstOrDefault(p => ProtocolName(p) == value);
                if (port.Value.Length == 0 || port.Value == RemotePath.DefaultPort(current).ToString())
                    port.Value = RemotePath.DefaultPort(chosen).ToString();
                current = chosen;
            },
        };
        var host = new PluginField { Label = T("Server"), Value = c.Host, Placeholder = "example.com" };
        var user = new PluginField { Label = T("User name"), Value = c.User, Placeholder = T("(empty: anonymous FTP)") };
        string? oldPassword = c.Password;
        var password = new PluginField { Label = T("Password (or the private key's passphrase)"), Kind = PluginFieldKind.Password, Value = oldPassword ?? string.Empty };
        var savePassword = new PluginField { Label = T("Save the password (encrypted for your Windows account)"), Kind = PluginFieldKind.Check,
            Value = c.ProtectedPassword != null || c.Host.Length == 0 ? "true" : "false" };
        var key = new PluginField { Label = T("Private key file (SFTP, optional)"), Kind = PluginFieldKind.File, Value = c.PrivateKeyFile ?? string.Empty };
        var initial = new PluginField { Label = T("Initial directory (optional)"), Value = c.InitialPath ?? string.Empty, Placeholder = T("(the home directory)") };

        PluginField[] fields = { name, protocol, host, port, user, password, savePassword, key, initial };
        string? Validate()
        {
            string server = host.Value.Trim();
            if (server.Length == 0)
                return T("Type the server's name or address.");
            if (server.Contains("://", StringComparison.Ordinal) || server.Contains('/'))
                return T("Type only the server's name or address (like example.com), without a path.");
            if (!int.TryParse(port.Value, out int p) || p is < 1 or > 65535)
                return T("The port is a number from 1 to 65535.");
            if (key.Value.Length > 0 && !File.Exists(key.Value))
                return T("The private key file doesn't exist.");
            return null;
        }
        if (!await _host!.Ui.EditFieldsAsync(title, null, fields, Validate, window))
            return false;

        c.Name = name.Value.Trim();
        c.Protocol = Protocols.FirstOrDefault(p => ProtocolName(p) == protocol.Value);
        c.Host = host.Value.Trim();
        c.Port = int.Parse(port.Value);
        c.User = user.Value.Trim();
        c.PrivateKeyFile = key.Value.Length > 0 ? key.Value : null;
        c.InitialPath = initial.Value.Length > 0 ? initial.Value : null;
        c.Password = savePassword.Value == "true" && password.Value.Length > 0 ? password.Value : null;
        return true;
    }
}
