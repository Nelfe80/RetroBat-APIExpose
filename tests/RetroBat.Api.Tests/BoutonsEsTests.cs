using System.Xml.Linq;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Les boutons « valider » et « retour » d'EmulationStation (2026-10-18) : le panneau de classement suit ceux que le
/// joueur a configures dans ES, inverses s'il le faut, et garde les slots pour un appareil qu'ES ne connait pas.
/// </summary>
public sealed class BoutonsEsTests
{
    private static readonly XDocument EsInput = XDocument.Parse("""
        <inputList>
          <inputConfig type="keyboard" deviceName="Keyboard" deviceGUID="-1"><input name="a" type="key" id="120" value="1" /></inputConfig>
          <inputConfig type="joystick" deviceName="Xbox 360 Controller" deviceGUID="030000005e0400008e02000000007200">
            <input name="a" type="button" id="1" value="1" />
            <input name="b" type="button" id="0" value="1" />
          </inputConfig>
          <inputConfig type="joystick" deviceName="Generic USB Joystick" deviceGUID="03000000790000000600000000000000">
            <input name="a" type="button" id="0" value="1" />
            <input name="b" type="button" id="1" value="1" />
          </inputConfig>
          <inputConfig type="joystick" deviceName="8Bitdo NES30 Pro" deviceGUID="03000000c82d00000190000000000000">
            <input name="a" type="axis" id="1" value="1" />
          </inputConfig>
        </inputList>
        """);

    [Fact]
    public void Le_panel_de_reference_valide_sur_le_bouton_0()
        => Assert.Equal((0, 1), CabinetInputReader.BoutonsEs(EsInput, "03000000790000000600000000000000", "Generic USB Joystick"));

    [Fact]
    public void Une_manette_xbox_se_retrouve_par_le_prefixe_de_son_guid()
        // SDL donne a la manette un suffixe de pilote different de celui qu'ES a enregistre.
        => Assert.Equal((1, 0), CabinetInputReader.BoutonsEs(EsInput, "030000005e0400008e02000014017801", "Xbox 360 Controller"));

    [Fact]
    public void Par_le_nom_si_le_guid_ne_dit_rien()
        => Assert.Equal((0, 1), CabinetInputReader.BoutonsEs(EsInput, "ffffffffffffffffffffffffffffffff", "Generic USB Joystick"));

    [Fact]
    public void Un_appareil_inconnu_d_ES_n_a_pas_de_boutons()
        => Assert.Equal(((int?) null, (int?) null), CabinetInputReader.BoutonsEs(EsInput, "0300000011112222333300000000", "Manette inconnue"));

    [Fact]
    public void Un_valider_sur_un_axe_ne_compte_pas()
        => Assert.Equal(((int?) null, (int?) null), CabinetInputReader.BoutonsEs(EsInput, "03000000c82d00000190000000000000", "8Bitdo NES30 Pro"));

    [Theory]
    [InlineData(1, false, "valider")]
    [InlineData(0, false, "retour")]
    [InlineData(0, true, "valider")]
    [InlineData(1, true, "retour")]
    [InlineData(2, false, null)]
    [InlineData(-1, false, null)]
    public void Le_role_suit_ES_et_son_inversion(int raw, bool inverse, string? attendu)
        // La manette du joueur : « a » d'ES sur le bouton 1 (droite), « b » sur le 0 (bas).
        => Assert.Equal(attendu, PanelInputWatcherService.RoleEs(raw, 1, 0, inverse));
}
