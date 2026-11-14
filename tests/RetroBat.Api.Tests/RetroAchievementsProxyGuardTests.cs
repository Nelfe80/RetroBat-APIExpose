using System.Net;
using Microsoft.AspNetCore.Http;
using RetroBat.Api.Controllers;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// The RetroAchievements proxy adds the player's credentials: RetroArch on this machine only.
/// </summary>
public class RetroAchievementsProxyGuardTests
{
    private static DefaultHttpContext Context(string ip, params (string Name, string Value)[] headers)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        foreach (var (name, value) in headers)
        {
            context.Request.Headers[name] = value;
        }

        return context;
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void RetroArch_on_loopback_is_accepted(string ip)
    {
        Assert.True(RetroAchievementsProxyController.IsLocalNativeCaller(Context(ip, ("User-Agent", "RetroArch/1.22.2"))));
    }

    [Fact]
    public void A_network_client_is_refused()
    {
        Assert.False(RetroAchievementsProxyController.IsLocalNativeCaller(Context("192.168.1.20")));
    }

    [Theory]
    [InlineData("Sec-Fetch-Site", "cross-site")]
    [InlineData("Sec-Fetch-Mode", "no-cors")]
    [InlineData("Origin", "http://evil.example")]
    [InlineData("Origin", "null")]
    public void A_web_page_is_refused_even_on_loopback(string header, string value)
    {
        Assert.False(RetroAchievementsProxyController.IsLocalNativeCaller(Context("127.0.0.1", (header, value))));
    }
}
