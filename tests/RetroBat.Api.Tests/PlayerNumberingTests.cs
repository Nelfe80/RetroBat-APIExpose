using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le panel de la borne est toujours le joueur 1, quel que soit l'ordre dans lequel SDL enumere
/// les appareils : une manette Xbox branchee arrive en tete (XInput), elle passe pourtant derriere.
/// </summary>
public class PlayerNumberingTests
{
    private static readonly PlayerNumbering.Device Panel = new("03000000790000000600000000000000", "Generic USB Joystick");
    private static readonly PlayerNumbering.Device Panel2 = new("03000000790000001100000000000000", "USB Gamepad");
    private static readonly PlayerNumbering.Device XboxXInput = new("030000005e0400008e02000000007801", "Xbox 360 Controller");
    private static readonly PlayerNumbering.Device XboxOldXInput = new("78696e70757401000000000000000000", "XInput Controller #1");
    private static readonly PlayerNumbering.Device DualSense = new("030000004c050000e60c000000006800", "DualSense Wireless Controller");
    private static readonly PlayerNumbering.Device Bluetooth8BitDo = new("05000000c82d00002038000000000000", "Bluetooth Wireless Controller");
    private static readonly IReadOnlyDictionary<int, string> SansEpingle = new Dictionary<int, string>();

    [Fact]
    public void Une_manette_xbox_en_tete_ne_prend_pas_la_place_du_panel()
    {
        Assert.Equal(new[] { 2, 1 }, PlayerNumbering.Assign(new[] { XboxXInput, Panel }, SansEpingle));
        Assert.Equal(new[] { 2, 1 }, PlayerNumbering.Assign(new[] { XboxOldXInput, Panel }, SansEpingle));
    }

    [Fact]
    public void Deux_panels_font_les_joueurs_1_et_2_dans_l_ordre_de_sdl()
    {
        Assert.Equal(new[] { 3, 1, 2 }, PlayerNumbering.Assign(new[] { DualSense, Panel, Panel2 }, SansEpingle));
    }

    [Fact]
    public void Sans_panel_la_manette_reste_joueur_1()
    {
        Assert.Equal(new[] { 1, 2 }, PlayerNumbering.Assign(new[] { XboxXInput, Bluetooth8BitDo }, SansEpingle));
    }

    [Fact]
    public void Une_epingle_l_emporte_sur_la_regle()
    {
        // Le second panel est epingle joueur 1 : il passe devant le premier, la manette reste derriere.
        var epingles = new Dictionary<int, string> { [1] = "030000007900000011000000" };
        Assert.Equal(new[] { 3, 2, 1 }, PlayerNumbering.Assign(new[] { XboxXInput, Panel, Panel2 }, epingles));
    }

    [Theory]
    [InlineData("030000005e0400008e02000000007801", true)]    // XInput, « x » en 15e octet
    [InlineData("78696e70757401000000000000000000", true)]    // « xinput » en hexadecimal
    [InlineData("030000005e0400008e02000000007200", true)]    // Microsoft, via RawInput
    [InlineData("05000000c82d00002038000000000000", true)]    // Bluetooth
    [InlineData("030000004c050000cc09000000006800", true)]    // Sony
    [InlineData("03000000790000000600000000000000", false)]   // encodeur DragonRise
    [InlineData("03000000c0160000e105000000000000", false)]   // Xin-Mo
    [InlineData("", false)]
    public void Reconnait_une_manette_du_commerce(string guid, bool attendu)
        => Assert.Equal(attendu, PlayerNumbering.LooksLikeGamepad(guid));

    [Fact]
    public void La_description_dit_qui_est_quoi()
    {
        var devices = new[] { XboxXInput, Panel };
        Assert.Equal("2 = Xbox 360 Controller (manette), 1 = Generic USB Joystick (panel)", PlayerNumbering.Describe(devices, PlayerNumbering.Assign(devices, SansEpingle)));
    }
}
