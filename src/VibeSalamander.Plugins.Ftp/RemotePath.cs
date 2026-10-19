// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Plugins.Ftp;

public enum RemoteProtocol
{
    Sftp,
    Ftp,
    /// <summary>FTP with explicit TLS (AUTH TLS on port 21).</summary>
    FtpsExplicit,
    /// <summary>FTP over TLS from the start (port 990).</summary>
    FtpsImplicit,
}

/// <summary>
/// A path on a server as the panels show it: "sftp://user@host:2222/home/user/dir". The port is left out
/// when it is the protocol's default; '@', ':', '/' and '%' in the user name are written as %40 and so on.
/// The path part is the server's own path ('/' separated, not encoded).
/// </summary>
public sealed record RemotePath(RemoteProtocol Protocol, string User, string Host, int Port, string Path)
{
    public static string SchemeOf(RemoteProtocol protocol) => protocol switch
    {
        RemoteProtocol.Sftp => "sftp",
        RemoteProtocol.FtpsExplicit => "ftpes",
        RemoteProtocol.FtpsImplicit => "ftps",
        _ => "ftp",
    };

    public static int DefaultPort(RemoteProtocol protocol) => protocol switch
    {
        RemoteProtocol.Sftp => 22,
        RemoteProtocol.FtpsImplicit => 990,
        _ => 21,
    };

    public static bool TryParseScheme(string scheme, out RemoteProtocol protocol)
    {
        switch (scheme.ToLowerInvariant())
        {
            case "sftp":
                protocol = RemoteProtocol.Sftp;
                return true;
            case "ftp":
                protocol = RemoteProtocol.Ftp;
                return true;
            case "ftpes":
                protocol = RemoteProtocol.FtpsExplicit;
                return true;
            case "ftps":
                protocol = RemoteProtocol.FtpsImplicit;
                return true;
            default:
                protocol = RemoteProtocol.Ftp;
                return false;
        }
    }

    /// <summary>"sftp://user@host:2222/dir" → the parts; null when it isn't such a path.</summary>
    public static RemotePath? Parse(string text)
    {
        int schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0 || !TryParseScheme(text[..schemeEnd], out RemoteProtocol protocol))
            return null;
        string rest = text[(schemeEnd + 3)..];
        int slash = rest.IndexOf('/');
        string authority = slash < 0 ? rest : rest[..slash];
        string path = slash < 0 ? "/" : rest[slash..];
        if (authority.Length == 0)
            return null;

        string user = string.Empty;
        int at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            user = Uri.UnescapeDataString(authority[..at]);
            authority = authority[(at + 1)..];
        }
        int port = DefaultPort(protocol);
        string host = authority;
        if (authority.StartsWith('['))
        {
            // [IPv6]:port
            int close = authority.IndexOf(']');
            if (close < 0)
                return null;
            host = authority[1..close];
            if (close + 1 < authority.Length)
            {
                if (authority[close + 1] != ':' || !int.TryParse(authority[(close + 2)..], out port))
                    return null;
            }
        }
        else
        {
            int colon = authority.LastIndexOf(':');
            if (colon >= 0)
            {
                if (!int.TryParse(authority[(colon + 1)..], out port))
                    return null;
                host = authority[..colon];
            }
        }
        if (host.Length == 0 || port is < 1 or > 65535)
            return null;
        return new RemotePath(protocol, user, host, port, NormalizePath(path));
    }

    /// <summary>"/a//b/" → "/a/b"; always starts with '/'.</summary>
    public static string NormalizePath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>();
        foreach (string part in parts)
        {
            if (part == ".")
                continue;
            if (part == "..")
            {
                if (kept.Count > 0)
                    kept.RemoveAt(kept.Count - 1);
                continue;
            }
            kept.Add(part);
        }
        return "/" + string.Join('/', kept);
    }

    /// <summary>The server, without the path: "sftp://user@host:2222" (one session per server and user).</summary>
    public string ServerKey
    {
        get
        {
            string user = User.Length > 0 ? EscapeUser(User) + "@" : string.Empty;
            string host = Host.Contains(':') ? "[" + Host + "]" : Host;
            string port = Port != DefaultPort(Protocol) ? ":" + Port : string.Empty;
            return $"{SchemeOf(Protocol)}://{user}{host}{port}";
        }
    }

    private static string EscapeUser(string user) =>
        user.Replace("%", "%25").Replace("@", "%40").Replace(":", "%3A").Replace("/", "%2F");

    public override string ToString() => ServerKey + Path;

    public bool IsRoot => Path == "/";

    /// <summary>The parent directory, or null at the root.</summary>
    public RemotePath? Parent
    {
        get
        {
            if (IsRoot)
                return null;
            int slash = Path.LastIndexOf('/');
            return this with { Path = slash <= 0 ? "/" : Path[..slash] };
        }
    }

    /// <summary>The last part of the path ("" at the root).</summary>
    public string Name => IsRoot ? string.Empty : Path[(Path.LastIndexOf('/') + 1)..];

    public RemotePath Child(string name) => this with { Path = IsRoot ? "/" + name : Path + "/" + name };

    public RemotePath WithPath(string path) => this with { Path = NormalizePath(path) };
}
