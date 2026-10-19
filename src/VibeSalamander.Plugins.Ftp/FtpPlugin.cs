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
        window.SetStatus(string.Format(T("Connecting to {0}..."), connection.Host));
        window.SetProgress(null);
        try
        {
            string path = await Task.Run(() => _fileSystem.OpenAsync(connection, CancellationToken.None));
            window.Close();
            await _host.ActivePanel.NavigateAsync(path);
        }
        catch (OperationCanceledException)
        {
            window.SetStatus(T("Not connected."));
        }
        catch (Exception ex)
        {
            window.SetStatus(string.Format(T("{0}: {1}"), connection.Host, ex.Message));
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
