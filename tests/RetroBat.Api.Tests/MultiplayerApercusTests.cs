using System.Runtime.Versioning;
using RetroBat.Api.Leaderboard;
using RetroBat.Api.Multiplayer;
using Xunit;
using L = RetroBat.Api.Leaderboard.LeaderboardOverlayService;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES APERCUS DU PANNEAU MULTIPLAYER, dessines hors ecran dans des PNG (outil de mise au point : juger une mise en page
/// sans installer l'API sur la borne). Joueurs fictifs, theme de la borne.
///
/// Ne tourne que si NELFEPLAY_APERCUS donne le dossier de sortie ; NELFEPLAY_APERCUS_BADGES, le cache des badges
/// RetroAchievements (portraits des personnages). Sinon il passe sans rien faire.
/// </summary>
[SupportedOSPlatform("windows")]
public class MultiplayerApercusTests
{
    private static readonly string? Sortie = Environment.GetEnvironmentVariable("NELFEPLAY_APERCUS");
    private static readonly string? Badges = Environment.GetEnvironmentVariable("NELFEPLAY_APERCUS_BADGES");
    private static readonly string? Planche = Environment.GetEnvironmentVariable("NELFEPLAY_APERCUS_PLANCHE");

    [Fact]
    public void Les_dispositions_du_panneau_se_dessinent()
    {
        if (Sortie is null)
        {
            return;   // outil de mise au point, hors des passes ordinaires
        }

        Directory.CreateDirectory(Sortie);
        var style = EsMenuStyle.Lire(null);
        string? Portrait(int badge) => Badges is null ? null : Path.Combine(Badges, badge + ".png");
        var noms = new[] { "RYU", "E. HONDA", "BLANKA", "GUILE", "KEN", "CHUN-LI", "ZANGIEF", "DHALSIM", "M. BISON", "SAGAT", "BALROG", "VEGA" };
        var bande = noms.Select((nom, i) => new L.PortraitDeChoix(Portrait(307536 + i), nom)).ToList();
        var onglets = new List<string> { "INVITE", "WAITING FOR FRIENDS", "FOR PLAYERS…" };
        var aidesNp = new List<L.Aide> { new("dpad_leftright", "TAB"), new("dpad_updown", "CHARACTER"), new("button_b", "BACK") };
        var aidesResultat = new List<L.Aide> { new("button_a", "PLAY AGAIN"), new("button_b", "CLOSE") };
        L.JoueurMultiplayer Libre(int place) => new("FREE SEAT", "", "", false, false, false, null, "waiting for a player", Place: place, Libre: true);
        var moi = new L.JoueurMultiplayer("Nelfe80", "FR", "A98BFF", false, true, true, 3, "ready", "HOST", "KEN",
            Portrait: Portrait(307540), Place: 1, Cote: "? · ELO 1016", Provisoire: "provisional", ChoixModifiable: true);
        var lui = new L.JoueurMultiplayer("RetroLife", "FR", "FF9F6B", true, false, true, 4, "ready", "HOST", "CHUN-LI",
            Portrait: Portrait(307541), Place: 2, Cote: "? · ELO 984", Provisoire: "provisional");
        var socle = new L.ContenuMultiplayer(onglets, 2, true, "", "1 VS 1", "1/2", null, [moi, Libre(2)], -1, "", "", aidesNp,
            Selection: bande, SelectionCourante: 4, Disposition: L.DispositionDesJoueurs.Colonnes);
        var stats1 = new List<L.StatDuJoueur> { new("HITS", "12"), new("K.O.", "2"), new("PERFECTS", "1"), new("DAMAGE", "288") };
        var stats2 = new List<L.StatDuJoueur> { new("HITS", "7"), new("K.O.", "0"), new("PERFECTS", "0"), new("DAMAGE", "90") };
        L.JoueurMultiplayer Classique(string nom, string pays, string couleur, int place, bool hote, int? barres)
            => new(nom, pays, couleur, hote, place == 1, true, barres, "ready", "HOST", Place: place, Avatar: place == 1 ? Planche : null);

        var vues = new Dictionary<string, L.ContenuMultiplayer>
        {
            ["np-2-attente"] = socle,
            ["np-2-plein"] = socle with { Compte = "2/2", DecompteFinUtc = DateTime.UtcNow.AddSeconds(4), Lignes = [moi, lui] },
            ["np-2-resultat"] = socle with
            {
                Onglets = ["VICTORY"], OngletCourant = 0, NousAvonsLaMain = false, Compte = "", Info = "PLAY TIME 1:01",
                DecompteFinUtc = DateTime.UtcNow.AddSeconds(12), Selection = null, Aides = aidesResultat,
                Lignes =
                [
                    moi with { Issue = "VICTORY", Gagnant = true, Evolution = "+16", Stats = stats1, ChoixModifiable = false },
                    lui with { Issue = "DEFEAT", Cote = "? · ELO 968", Evolution = "-16", Stats = stats2 },
                ],
                PartageDesDegats = new L.Partage(288 / 378d, "A98BFF", "FF9F6B", "DAMAGE DEALT"),
            },
            ["classique-4"] = socle with
            {
                Badge = "CLASSIC", Info = "Classic mode", Compte = "2/4", Selection = null, Disposition = L.DispositionDesJoueurs.Grille,
                Aides = [new("dpad_leftright", "TAB"), new("button_b", "BACK")],
                Lignes = [Classique("Nelfe80", "FR", "A98BFF", 1, false, 3), Classique("RetroLife", "BE", "FF9F6B", 2, true, 4), Libre(3), Libre(4)],
            },
            ["classique-3-resultat"] = socle with
            {
                Onglets = ["GAME OVER · CLASSIC"], OngletCourant = 0, NousAvonsLaMain = false, Badge = "CLASSIC", Compte = "",
                Info = "PLAY TIME 7:42", Selection = null, Aides = aidesResultat, Disposition = L.DispositionDesJoueurs.Grille,
                Lignes = [Classique("Nelfe80", "FR", "A98BFF", 1, false, 3) with { Etat = "P1" }, Classique("RetroLife", "BE", "FF9F6B", 2, true, 4) with { Etat = "P2" },
                    Classique("Arcadia", "JP", "78E5A3", 3, false, 2) with { Etat = "P3" }],
            },
            ["amis-liste"] = socle with
            {
                OngletCourant = 0, Compte = "1/2", Selection = null, Disposition = L.DispositionDesJoueurs.Liste, LigneCourante = 0,
                Lignes =
                [
                    new("Arcadia", "BE", "78E5A3", false, false, true, null, "in EmulationStation"),
                    new("PixelKid", "DE", "FFD37A", false, false, true, null, "invited"),
                ],
            },
            ["liste-6"] = socle with
            {
                Compte = "6/6", Selection = null, Disposition = L.DispositionDesJoueurs.Liste,
                Lignes = Enumerable.Range(1, 6).Select(i => Classique("Player " + i, i % 2 == 0 ? "JP" : "FR", "A98BFF", i, i == 2, 3)).ToList(),
            },
        };

        vues["np-2-abandon-moi"] = vues["np-2-resultat"] with
        {
            Onglets = ["ABANDON"], PartageDesDegats = null,
            Bandeau = "Abandon 2 of 3: at 3, 15 minutes without MULTIPLAYER. A match played to the end removes one.",
            Lignes =
            [
                moi with { Issue = "ABANDON", Abandon = true, Evolution = "-14", Cote = "? · ELO 984", Stats = stats1, ChoixModifiable = false },
                lui with { Issue = "", Stats = stats2 },
            ],
        };
        vues["np-2-abandon-lui"] = vues["np-2-abandon-moi"] with
        {
            Onglets = ["OPPONENT LEFT"], Bandeau = "Your opponent left before the end: your rating does not move.",
            Lignes =
            [
                moi with { Issue = "", Stats = stats1, ChoixModifiable = false },
                lui with { Issue = "ABANDON", Abandon = true, Evolution = "-14", Cote = "? · ELO 968", Stats = stats2 },
            ],
        };

        var cartes = MultiplayerToastOverlay.Apercu(style,
        [
            new("MULTIPLAYER SUSPENDED", "3 matches left before the end", "a match played to the end removes one", DateTime.UtcNow.AddSeconds(14 * 60 + 32), Alerte: true),
            new("YOUR SELECTION", "", "", Lignes: [new("Street Fighter II' Champion Edition", "1/2   waiting for players")]),
        ], new System.Drawing.Rectangle(0, 0, 1920, 1200));
        File.WriteAllBytes(Path.Combine(Sortie, "cartes-suspension.png"), cartes);

        foreach (var (nom, contenu) in vues)
        {
            var png = L.ApercuMultiplayer(style, contenu, "", "", "", null!);
            Assert.True(png.Length > 0, nom);
            File.WriteAllBytes(Path.Combine(Sortie, nom + ".png"), png);
        }
    }
}
