using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// The rom name of the MAME cfg repair goes to the MAME command line and into file paths:
/// a MAME short name only.
/// </summary>
public class MameRomNameTests
{
    [Theory]
    [InlineData("sf2")]
    [InlineData("mslug3")]
    [InlineData("1944")]
    [InlineData("pacman_x")]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345")]
    public void Short_names_are_accepted(string rom)
    {
        Assert.True(MameCfgDeployService.IsValidRomName(rom));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sf2 -pluginspath \\\\host\\share -plugin evil")]
    [InlineData("x\" -inipath \"c:\\")]
    [InlineData("..\\..\\x")]
    [InlineData("../x")]
    [InlineData("-cfg_directory")]
    [InlineData("SF2")]
    [InlineData("sf2.zip")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456")]
    public void Anything_else_is_refused(string? rom)
    {
        Assert.False(MameCfgDeployService.IsValidRomName(rom));
    }

    [Fact]
    public void Repair_refuses_an_injected_name_without_starting_mame()
    {
        var service = new MameCfgDeployService(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        var report = service.Repair("sf2 -plugin evil");
        Assert.Equal("failed", report.Status);
        Assert.Contains("nom de rom invalide", report.Details);
    }

    [Fact]
    public void Repair_arguments_keep_the_rom_as_a_single_argument()
    {
        var args = MameCfgDeployService.RepairArguments("sf2", "c:\\x\\dump ports.lua");
        Assert.Equal("sf2", args[0]);
        Assert.Contains("-autoboot_script", args);
        Assert.Equal("c:\\x\\dump ports.lua", args[4]);
    }

    [Fact]
    public void Current_wiring_of_an_injected_name_is_empty()
    {
        Assert.Empty(new MameCfgDeployService(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()).CurrentWiring("..\\..\\x"));
    }
}
