using RetroBat.Api.Controllers;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// retroarch/command targets RetroArch on this machine, never another host of the network.
/// </summary>
public class RetroArchCommandHostTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.2")]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    public void Loopback_is_accepted(string host)
    {
        Assert.True(CommandsController.IsLocalRetroArchHost(host));
    }

    [Theory]
    [InlineData("192.168.1.20")]
    [InlineData("8.8.8.8")]
    [InlineData("evil.example")]
    [InlineData("localhost.evil.example")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_refused(string? host)
    {
        Assert.False(CommandsController.IsLocalRetroArchHost(host));
    }
}
