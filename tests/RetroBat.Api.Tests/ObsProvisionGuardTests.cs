using RetroBat.Api.Controllers;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// OBS provisioning: the SRT address stays one INI line, profile and collection never add options.
/// </summary>
public class ObsProvisionGuardTests
{
    [Theory]
    [InlineData("srt://192.168.1.20:8890?streamid=publish:borne-1")]
    [InlineData("srt://127.0.0.1:8890?streamid=publish:abc%20def")]
    [InlineData("srt://hub.local:8890")]
    public void Hub_addresses_are_accepted(string url)
    {
        Assert.True(ObsController.IsValidSrtUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("rtmp://192.168.1.20/live")]
    [InlineData("srt://192.168.1.20:8890\n[Output]\nMode=Simple")]
    [InlineData("srt://192.168.1.20:8890\rFFURL=srt://evil")]
    [InlineData("srt://")]
    public void Anything_else_is_refused(string? url)
    {
        Assert.False(ObsController.IsValidSrtUrl(url));
    }

    [Fact]
    public void Profile_and_collection_are_single_arguments()
    {
        var args = ObsController.LaunchArguments("RetroBorne\" --verbose \"x", "Ma collection");
        Assert.Equal(new[] { "--minimize-to-tray", "--disable-shutdown-check", "--multi",
            "--profile", "RetroBorne\" --verbose \"x", "--collection", "Ma collection" }, args);
        Assert.Equal(3, ObsController.LaunchArguments(null, " ").Count);
    }
}
