// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Plugins.Ftp;

namespace VibeSalamander.Core.Tests;

public sealed class FtpPluginTests
{
    [Theory]
    [InlineData("sftp://alice@example.com/home/alice", RemoteProtocol.Sftp, "alice", "example.com", 22, "/home/alice")]
    [InlineData("sftp://alice@example.com:2222/", RemoteProtocol.Sftp, "alice", "example.com", 2222, "/")]
    [InlineData("ftp://example.com", RemoteProtocol.Ftp, "", "example.com", 21, "/")]
    [InlineData("ftpes://bob@files.example.org/pub/", RemoteProtocol.FtpsExplicit, "bob", "files.example.org", 21, "/pub")]
    [InlineData("ftps://bob@files.example.org/a//b/../c", RemoteProtocol.FtpsImplicit, "bob", "files.example.org", 990, "/a/c")]
    [InlineData("sftp://me@[::1]:2200/tmp", RemoteProtocol.Sftp, "me", "::1", 2200, "/tmp")]
    [InlineData("ftp://user%40mail.com@host/x", RemoteProtocol.Ftp, "user@mail.com", "host", 21, "/x")]
    public void Paths_are_parsed(string text, RemoteProtocol protocol, string user, string host, int port, string path)
    {
        RemotePath? parsed = RemotePath.Parse(text);
        Assert.NotNull(parsed);
        Assert.Equal(protocol, parsed!.Protocol);
        Assert.Equal(user, parsed.User);
        Assert.Equal(host, parsed.Host);
        Assert.Equal(port, parsed.Port);
        Assert.Equal(path, parsed.Path);
    }

    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData("http://example.com/")]
    [InlineData("sftp://")]
    [InlineData("sftp://host:99999/")]
    [InlineData("thispc:")]
    public void Other_paths_are_not_servers(string text) => Assert.Null(RemotePath.Parse(text));

    [Fact]
    public void Paths_round_trip_and_leave_out_default_ports()
    {
        Assert.Equal("sftp://alice@example.com/home", RemotePath.Parse("sftp://alice@example.com:22/home")!.ToString());
        Assert.Equal("sftp://alice@example.com:2222/home", RemotePath.Parse("sftp://alice@example.com:2222/home")!.ToString());
        Assert.Equal("ftp://user%40mail.com@host/x", RemotePath.Parse("ftp://user%40mail.com@host/x")!.ToString());
        Assert.Equal("sftp://me@[::1]:2200/tmp", RemotePath.Parse("sftp://me@[::1]:2200/tmp")!.ToString());
    }

    [Fact]
    public void Parents_children_and_names()
    {
        RemotePath path = RemotePath.Parse("sftp://a@h/home/a/docs")!;
        Assert.Equal("docs", path.Name);
        Assert.Equal("sftp://a@h/home/a", path.Parent!.ToString());
        Assert.Equal("sftp://a@h/", path.Parent!.Parent!.Parent!.ToString());
        Assert.Null(path.Parent!.Parent!.Parent!.Parent);
        Assert.Equal("sftp://a@h/home/a/docs/new dir", path.Child("new dir").ToString());
        Assert.Equal("sftp://a@h/x", RemotePath.Parse("sftp://a@h/")!.Child("x").ToString());
        Assert.Equal("sftp://a@h", path.ServerKey);
    }

    [Theory]
    [InlineData("normal.txt", "normal.txt")]
    [InlineData("a:b*c?.txt", "a_b_c_.txt")]
    [InlineData("trailing. ", "trailing")]
    [InlineData("...", "_")]
    public void Server_names_become_valid_Windows_names(string name, string expected) =>
        Assert.Equal(expected, Transfer.LocalName(name));

    [Fact]
    public void Connections_match_their_server()
    {
        var connection = new Connection { Protocol = RemoteProtocol.Sftp, Host = "Example.com ", Port = 22, User = "alice" };
        Assert.True(connection.Matches(RemotePath.Parse("sftp://alice@example.com/x")!));
        Assert.False(connection.Matches(RemotePath.Parse("sftp://bob@example.com/x")!));
        Assert.False(connection.Matches(RemotePath.Parse("ftp://alice@example.com/x")!));
        Assert.Equal("sftp://alice@Example.com/", connection.Root.ToString());
    }
}
