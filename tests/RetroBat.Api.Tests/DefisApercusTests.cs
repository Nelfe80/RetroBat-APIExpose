using RetroBat.Api.Leaderboard;
using Xunit;
using L = RetroBat.Api.Leaderboard.LeaderboardOverlayService;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES APERCUS DU PANNEAU DES DEFIS (lot P5) : le vrai rendu GDI+, dessine dans un PNG, sans la borne. Ne dessinent que si
/// NELFEPLAY_APERCUS donne un dossier de sortie ; sinon, ils verifient seulement que le dessin aboutit.
/// </summary>
public class DefisApercusTests
{
    private static readonly string? Sortie = Environment.GetEnvironmentVariable("NELFEPLAY_APERCUS");

    private static readonly string[] Onglets = ["RECORDS", "TRAINING", "CHALLENGES", "SPEEDRUNS"];

    private static readonly EsMenuStyle Style = EsMenuStyle.Lire(null);

    private static readonly L.Aide[] Aides = [new("dpad_leftright", "Onglet"), new("dpad_updown", "Épreuve"), new(Style.HelpIcon("b"), "Retour")];

    private static void Ecrire(string nom, byte[] png)
    {
        Assert.NotEmpty(png);
        if (Sortie is { Length: > 0 }) File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(Sortie).FullName, nom), png);
    }

    [Fact]
    public void Onglet_SPEEDRUNS_liste_et_fiche()
    {
        L.LigneDeDefi G(string n) => new(n, Groupe: true);
        L.LigneDeDefi N(string n, string toi = "--:--.--", string marque = "") => new(n, "[record]", toi, Marque: marque);
        var contenu = new L.ContenuDefis(Onglets, 3, true, false, ["RECORD", "TOI"],
            [G("Niveaux"), N("Green Hill · Acte 1", "0:47.48", "reussi"), N("Green Hill · Acte 2", "1:12.05", "reussi"),
             N("Green Hill · Acte 3", marque: "tente"), N("Marble · Acte 1"), N("Marble · Acte 2"), N("Marble · Acte 3"),
             G("Boss"), N("Robotnik · Green Hill"), G("Jeu complet"), N("Any%"), N("All Emeralds")],
            1,
            [new("Départ", "Début de l'acte, jeu normal"), new("But", "Toucher le panneau vivant"),
             new("Interdit", "Mourir, pause"), new("Chrono", "Calé sur le temps du jeu"),
             new("Record", "[record] · [joueur]"), new("Toi", "0:47.48 · 9e")],
            new(Style.HelpIcon("a"), "Lancer le speedrun"), Aides, Pseudo: "Nelfe", EnLigne: true);
        Ecrire("defis-speedruns.png", L.ApercuDefis(Style, contenu, null!));
    }

    [Fact]
    public void Onglet_CHALLENGES_bandeaux()
    {
        L.LigneDeDefi G(string n) => new(n, Groupe: true);
        L.LigneDeDefi B(string n, string regle, string mesure, string badge = "", string marque = "")
            => new(n, "RECORD [record]", "TOI --:--.--", Regle: regle, Mesure: mesure, Badge: badge, Marque: marque);
        var contenu = new L.ContenuDefis(Onglets, 2, true, true, [],
            [G("Boss"), B("Rootmars en une vie", "Dernier combat, 1 vie, sans grenade", "AU TEMPS", "TENTÉ × 3", "tente"),
             B("Galerie des boss", "Accès direct aux 8 grands boss", "AU TEMPS", "RÉUSSI", "reussi"),
             G("Règles folles"), B("Sans grenade", "Une mission sans une grenade", "AU TEMPS"),
             B("Pistolet seul", "Une mission sans ramasser d'arme", "AU TEMPS"),
             G("Montures et transformations"), B("Zombie jusqu'au bout", "Finir la mission 2 en zombie", "AU TEMPS")],
            1, [], new(Style.HelpIcon("a"), "Lancer le challenge"), Aides, Pseudo: "Nelfe", EnLigne: true);
        Ecrire("defis-challenges.png", L.ApercuDefis(Style, contenu, null!));
    }

    [Fact]
    public void Onglet_RECORDS_avec_ses_types()
    {
        var lignes = Enumerable.Range(1, 40).Select(i => new L.LigneDeDefi($"{i}   [joueur]", "", "[temps]")).ToList();
        var contenu = new L.ContenuDefis(Onglets, 0, true, false, ["", "TEMPS"], lignes, 0, [],
            new(Style.HelpIcon("x"), "Type de record"), Aides,
            Bandeau: "Voir la fin du jeu · partie neuve · morts et continues permis · sans pause",
            Types: ["GLOBAL", "ANY%", "100%", "NIVEAUX", "BOSS"], TypeCourant: 1, Pseudo: "Nelfe", EnLigne: true, LigneEpinglee: 31);
        Ecrire("defis-records.png", L.ApercuDefis(Style, contenu, null!));
    }

    [Fact]
    public void Onglet_MASS_RUN_salle_ouverte()
    {
        var salle = new L.SalleMassRun("Salle ouverte", true, "Mass Survival · Super Mario Bros. · Niveau 4-1",
            "Coureurs inscrits", "87", "Départ dans", "1:42", "la première mort élimine · tu cours à la couleur de ton avatar");
        var contenu = new L.ContenuDefis([.. Onglets, "MASS RUN"], 4, true, false, [], [], -1, [],
            new(Style.HelpIcon("y"), "Rejoindre la course"), Aides, Pseudo: "Nelfe", EnLigne: true, MassRun: salle);
        Ecrire("defis-massrun.png", L.ApercuDefis(Style, contenu, null!));
    }

    [Fact]
    public void Un_onglet_vide_dit_pourquoi()
    {
        var contenu = new L.ContenuDefis(["SPEEDRUNS"], 0, true, false, [], [], 0, [], null, Aides, Message: "Aucun défi ouvert pour ce jeu");
        Ecrire("defis-vide.png", L.ApercuDefis(Style, contenu, null!));
    }
}
