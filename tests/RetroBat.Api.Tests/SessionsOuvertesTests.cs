using RetroBat.Api.Multiplayer;
using RetroBat.Api.Replay.Video;
using RetroBat.Api.VocalRoom;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>Une session ouverte (salle MULTIPLAYER ou Adventure, vocal) interdit le rendu de station (28/11).</summary>
public sealed class SessionsOuvertesTests
{
    private static MultiplayerEtat Etat(params MultiplayerRoom[] salles) => new(salles, [], null, null);

    private static MultiplayerRoom Salle(string etat, bool moi = false, bool lancement = false, bool aventure = false) => new()
    {
        Room = "r", State = etat,
        Players = [new MultiplayerJoueur { Handle = "h", You = moi }],
        Launch = lancement ? new MultiplayerLancement() : null,
        Adventure = aventure ? new MultiplayerAventure() : null,
    };

    [Theory]
    [InlineData("waiting", true)]
    [InlineData("countdown", true)]
    [InlineData("launching", true)]
    [InlineData("playing", true)]
    [InlineData("finished", false)]
    public void Une_salle_ou_la_borne_est_assise_occupe_selon_son_etat(string etat, bool occupe)
        => Assert.Equal(occupe, SessionsOuvertes.Raison(Etat(Salle(etat, moi: true)), VocalRoomEtat.Arrete) is not null);

    [Fact]
    public void Une_salle_des_autres_sans_lancement_pour_la_borne_n_occupe_pas()
    {
        Assert.Null(SessionsOuvertes.Raison(Etat(Salle("waiting")), VocalRoomEtat.Arrete));
        Assert.NotNull(SessionsOuvertes.Raison(Etat(Salle("launching", lancement: true)), VocalRoomEtat.Arrete));
    }

    [Fact]
    public void Une_aventure_est_nommee()
        => Assert.Contains("Adventure", SessionsOuvertes.Raison(Etat(Salle("waiting", moi: true, aventure: true)), VocalRoomEtat.Arrete));

    [Fact]
    public void Le_vocal_occupe_demarre_en_marche_ou_en_appel()
    {
        Assert.Null(SessionsOuvertes.Raison(MultiplayerEtat.Vide, VocalRoomEtat.Arrete));
        Assert.Null(SessionsOuvertes.Raison(MultiplayerEtat.Vide, new VocalRoomEtat(VocalRoomEtat.Indisponible, "X")));
        Assert.NotNull(SessionsOuvertes.Raison(MultiplayerEtat.Vide, new VocalRoomEtat(VocalRoomEtat.Demarrage, "X")));
        Assert.NotNull(SessionsOuvertes.Raison(MultiplayerEtat.Vide, new VocalRoomEtat(VocalRoomEtat.EnMarche, "X")));
        Assert.NotNull(SessionsOuvertes.Raison(MultiplayerEtat.Vide, new VocalRoomEtat(VocalRoomEtat.Arret, "X", Connecte: true)));
    }
}
