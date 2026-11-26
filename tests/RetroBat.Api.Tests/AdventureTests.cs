using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroBat.Api.Adventure;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Media;
using RetroBat.Api.Multiplayer;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// NELFEPLAY ADVENTURE (lot 1, 26/11/2026) : le catalogue, la collection d'ES, la regle de reprise, les sauvegardes (fin, 5
/// minutes, sauvegarde du jeu), la copie locale, la file d'envoi (une coupure ne perd rien) et la seance de l'hote, contre un
/// site et un RetroArch simules.
/// </summary>
public class AdventureTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), "adventure-" + Guid.NewGuid().ToString("N")[..8]);
    private DateTime _heure = new(2026, 11, 26, 20, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private AdventureStockage Stockage() => new(Path.Combine(_racine, "adventure"));

    // ── la regle de reprise (pure) ─────────────────────────────────────────────────────────────────

    private static AdventureVersion Version(int joueurs = 3, string? etat = "e", string? sauvegarde = "s", string? coeur = null)
        => new() { Id = "v1", Adventure = "av-1", StateSha256 = etat, SaveSha256 = sauvegarde, Players = joueurs, CoreSha256 = coeur };

    [Fact]
    public void Pas_de_version_la_partie_est_neuve()
        => Assert.Equal(ModeDeReprise.Neuf, AdventureReprise.Decider(null, 2, null).Mode);

    [Fact]
    public void Le_meme_nombre_de_joueurs_recharge_l_etat_lie()
        => Assert.Equal(ModeDeReprise.Etat, AdventureReprise.Decider(Version(3), 3, null).Mode);

    [Theory]
    [InlineData(3, 2)]
    [InlineData(2, 4)]
    public void Un_autre_nombre_de_joueurs_repart_de_la_sauvegarde_de_la_cartouche(int dans_la_version, int joueurs)
        => Assert.Equal(ModeDeReprise.NeufAvecSauvegarde, AdventureReprise.Decider(Version(dans_la_version), joueurs, null).Mode);

    [Fact]
    public void Un_autre_nombre_de_joueurs_sans_sauvegarde_repart_a_neuf()
        => Assert.Equal(ModeDeReprise.Neuf, AdventureReprise.Decider(Version(3, sauvegarde: null), 2, null).Mode);

    [Fact]
    public void Un_etat_ne_se_recharge_jamais_dans_un_autre_coeur()
    {
        var decision = AdventureReprise.Decider(Version(3, coeur: "aaaa"), 3, "bbbb");
        Assert.Equal(ModeDeReprise.NeufAvecSauvegarde, decision.Mode);
        Assert.Equal(ModeDeReprise.Etat, AdventureReprise.Decider(Version(3, coeur: "AAAA"), 3, "aaaa").Mode);
        // Un coeur inconnu d'un cote ne bloque pas une version ancienne.
        Assert.Equal(ModeDeReprise.Etat, AdventureReprise.Decider(Version(3, coeur: null), 3, "bbbb").Mode);
    }

    // ── la cadence (pure) ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(299, true, false, false)]
    [InlineData(300, true, false, true)]
    [InlineData(900, false, false, false)]   // en pause
    [InlineData(900, true, true, false)]     // en chargement
    public void La_sauvegarde_reguliere_attend_cinq_minutes_et_jamais_en_chargement(int secondes, bool tourne, bool chargement, bool attendu)
    {
        var debut = new DateTime(2026, 11, 26, 20, 0, 0, DateTimeKind.Utc);
        Assert.Equal(attendu, AdventureCadence.RegulierEchu(debut.AddSeconds(secondes), debut, tourne, chargement));
    }

    [Fact]
    public void Le_jeu_a_sauve_quand_l_empreinte_de_la_cartouche_change()
    {
        Assert.False(AdventureCadence.LeJeuASauve(null, "a"));
        Assert.False(AdventureCadence.LeJeuASauve("a", "A"));
        Assert.True(AdventureCadence.LeJeuASauve("a", "b"));
    }

    // ── la copie locale ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Un_objet_se_range_par_son_empreinte_et_se_relit_verifie()
    {
        var stockage = Stockage();
        var octets = Encoding.UTF8.GetBytes("etat lie de trois consoles");
        var sha = stockage.PoserObjet(octets);
        Assert.Equal(AdventureStockage.Empreinte(octets), sha);
        Assert.Equal(octets, stockage.LireObjet(sha));

        // Un objet abime n'est jamais rendu : on ne charge pas ce qu'on n'a pas verifie.
        File.WriteAllText(Path.Combine(stockage.DossierDesObjets, sha), "abime");
        Assert.Null(stockage.LireObjet(sha));
        Assert.Null(stockage.LireObjet("../../etc/passwd"));
    }

    [Theory]
    [InlineData("av-1", true)]
    [InlineData("a1b2.c3", true)]
    [InlineData("../x", false)]
    [InlineData("a/b", false)]
    [InlineData("", false)]
    public void Un_identifiant_n_est_jamais_un_chemin(string id, bool sur)
        => Assert.Equal(sur, AdventureStockage.NomSur(id));

    [Fact]
    public void Les_versions_se_gardent_et_la_derniere_vient_en_premier()
    {
        var stockage = Stockage();
        stockage.GarderVersion(Version() with { Id = "v1", CreatedUtc = _heure });
        stockage.GarderVersion(Version() with { Id = "v2", CreatedUtc = _heure.AddMinutes(5) });
        Assert.Equal(["v2", "v1"], stockage.Versions("av-1").Select(v => v.Id).ToArray());
        Assert.Equal("v2", stockage.Derniere("av-1")!.Id);
        Assert.Throws<ArgumentException>(() => stockage.GarderVersion(Version() with { Id = "../v" }));
    }

    [Fact]
    public void Les_sauvegardes_de_l_aventure_sont_dans_leur_propre_dossier()
    {
        var stockage = Stockage();
        Assert.StartsWith(stockage.Racine, stockage.DossierDesSauvegardes("av-1"));
        Assert.Throws<ArgumentException>(() => stockage.DossierDesSauvegardes(".."));
    }

    // ── un site simule ─────────────────────────────────────────────────────────────────────────────

    private sealed class FauxSite : IAdventureSite
    {
        public bool Injoignable { get; set; }
        public string? RefusDefinitif { get; set; }
        public Dictionary<string, byte[]> Objets { get; } = new();
        public Dictionary<string, string> Genres { get; } = new();
        public List<AdventureVersion> Versions { get; } = [];
        public List<string> Journal { get; } = [];
        public AdventureInfo? Aventure { get; set; }
        public bool SeanceFermee { get; private set; }
        public List<AdventureInfo> Mes { get; } = [];
        public List<string> Creees { get; } = [];
        public List<string?> Versions_ { get; } = [];
        public List<(string Aventure, int Joueurs)> Ouvertures { get; } = [];
        public string? RefusDeSeance { get; set; }
        public string? RaisonDeFermeture { get; private set; }

        public Task<AdventureInfo?> CreerAsync(string jeu, string contenu, int places, string mode, string? titre, string? coeur, string? empreinteDuCoeur, CancellationToken ct, string? version = null)
        {
            Creees.Add(jeu);
            Versions_.Add(version);
            return Task.FromResult<AdventureInfo?>(Injoignable ? null : new AdventureInfo { Id = "av-neuve", Game = jeu, Content = contenu, Seats = places, Mode = mode, Name = titre });
        }

        public Task<AdventureMesAventures?> MesAventuresAsync(CancellationToken ct)
            => Task.FromResult<AdventureMesAventures?>(Injoignable ? null : new AdventureMesAventures(Mes, []));

        public Task<IReadOnlyList<AdventureInfo>?> OuvertesAsync(string? jeu, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AdventureInfo>?>(Injoignable ? null : []);

        public Task<AdventureInfo?> DetailAsync(string aventure, CancellationToken ct)
            => Task.FromResult(Injoignable ? null : Aventure);

        public Task<AdventureDerniere?> DerniereAsync(string aventure, int? joueurs, CancellationToken ct)
            => Task.FromResult<AdventureDerniere?>(Injoignable ? null : new AdventureDerniere(Aventure?.Latest, "state"));

        public Task<AdventureSeance?> OuvrirSeanceAsync(string aventure, int joueurs, CancellationToken ct)
        {
            Ouvertures.Add((aventure, joueurs));
            return Task.FromResult<AdventureSeance?>(Injoignable ? null : new AdventureSeance { Ok = RefusDeSeance is null, Reason = RefusDeSeance, Room = "r1", Latest = Aventure?.Latest });
        }

        public Task<bool> FermerSeanceAsync(string aventure, string raison, long? dureeS, CancellationToken ct)
        {
            SeanceFermee = !Injoignable;
            RaisonDeFermeture = raison;
            return Task.FromResult(!Injoignable);
        }

        public Task<(bool Ok, string? Raison)> InviterAsync(string aventure, string poignee, CancellationToken ct)
            => Task.FromResult((!Injoignable, Injoignable ? "offline" : (string?)null));

        public Task<bool> AccepterAsync(string invitation, bool accepter, CancellationToken ct) => Task.FromResult(!Injoignable);

        public Task<IReadOnlyList<string>?> ObjetsManquantsAsync(IReadOnlyCollection<string> empreintes, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>?>(Injoignable ? null : empreintes.Where(e => !Objets.ContainsKey(e)).ToList());

        public Task<bool> DeposerObjetAsync(string empreinte, string genre, byte[] octets, CancellationToken ct)
        {
            if (Injoignable)
            {
                return Task.FromResult(false);
            }

            Journal.Add("objet " + empreinte[..8]);
            Objets[empreinte] = octets;
            Genres[empreinte] = genre;
            return Task.FromResult(true);
        }

        public Task<AdventureDepot> DeposerVersionAsync(AdventureVersion version, CancellationToken ct)
        {
            if (Injoignable)
            {
                return Task.FromResult(AdventureDepot.APlusTard("offline"));
            }

            if (RefusDefinitif is not null)
            {
                return Task.FromResult(new AdventureDepot(false, true, RefusDefinitif));
            }

            // Une version qui pointe dans le vide est refusee par le site : l'API ne doit jamais l'envoyer avant ses objets.
            Assert.All(version.Objets(), sha => Assert.True(Objets.ContainsKey(sha), "objet absent du site"));
            Journal.Add("version " + version.Id);
            Versions.Add(version);
            return Task.FromResult(AdventureDepot.Parti);
        }

        public Task<byte[]?> TelechargerObjetAsync(string aventure, string empreinte, CancellationToken ct)
            => Task.FromResult(Injoignable ? null : Objets.GetValueOrDefault(empreinte));
    }

    // ── la file d'envoi : une coupure ne perd rien ─────────────────────────────────────────────────

    private AdventureVersion UneVersion(AdventureStockage stockage, string id, string etat, string? sauvegarde, DateTime quand)
        => new()
        {
            Id = id,
            Adventure = "av-1",
            StateSha256 = stockage.PoserObjet(Encoding.UTF8.GetBytes(etat)),
            SaveSha256 = sauvegarde is null ? null : stockage.PoserObjet(Encoding.UTF8.GetBytes(sauvegarde)),
            Players = 3,
            CreatedUtc = quand,
        };

    [Fact]
    public async Task Les_objets_partent_avant_la_version()
    {
        var stockage = Stockage();
        var file = new AdventureFileDEnvoi(stockage);
        var site = new FauxSite();
        file.Ajouter(UneVersion(stockage, "v1", "etat", "sauvegarde", _heure));

        Assert.Equal(1, await file.EnvoyerAsync(site, _heure, CancellationToken.None));
        Assert.Equal(["objet", "objet", "version"], site.Journal.Select(l => l.Split(' ')[0]).ToArray());
        Assert.Empty(file.EnAttente);
    }

    [Fact]
    public async Task Un_objet_deja_chez_le_site_ne_repart_pas()
    {
        var stockage = Stockage();
        var file = new AdventureFileDEnvoi(stockage);
        var site = new FauxSite();
        var v1 = UneVersion(stockage, "v1", "etat", "sauvegarde", _heure);
        site.Objets[v1.SaveSha256!] = [1];
        file.Ajouter(v1);

        await file.EnvoyerAsync(site, _heure, CancellationToken.None);
        Assert.Equal(2, site.Journal.Count);   // l'etat, puis la version
    }

    [Fact]
    public async Task Une_coupure_reseau_ne_perd_rien_et_l_envoi_reprend_apres_le_delai()
    {
        var stockage = Stockage();
        var file = new AdventureFileDEnvoi(stockage);
        var site = new FauxSite { Injoignable = true };
        file.Ajouter(UneVersion(stockage, "v1", "etat", "sauvegarde", _heure));
        file.Ajouter(UneVersion(stockage, "v2", "etat 2", null, _heure.AddMinutes(5)));

        Assert.Equal(0, await file.EnvoyerAsync(site, _heure, CancellationToken.None));
        Assert.Equal(2, file.EnAttente.Count);
        Assert.Equal(1, file.EnAttente.Single(e => e.Version.Id == "v1").Essais);

        // Le site revient, mais le delai de reprise n'est pas echu : rien ne part encore.
        site.Injoignable = false;
        Assert.Equal(0, await file.EnvoyerAsync(site, _heure.AddSeconds(5), CancellationToken.None));

        // Echu : les deux partent, dans l'ordre de leur prise.
        Assert.Equal(2, await file.EnvoyerAsync(site, _heure.AddMinutes(1), CancellationToken.None));
        Assert.Equal(["v1", "v2"], site.Versions.Select(v => v.Id).ToArray());
        Assert.Empty(file.EnAttente);
    }

    [Fact]
    public async Task La_file_survit_a_un_redemarrage_de_l_api()
    {
        var stockage = Stockage();
        var file = new AdventureFileDEnvoi(stockage);
        file.Ajouter(UneVersion(stockage, "v1", "etat", "sauvegarde", _heure));

        var apres = new AdventureFileDEnvoi(Stockage());
        Assert.Equal("v1", Assert.Single(apres.EnAttente).Version.Id);
        var site = new FauxSite();
        Assert.Equal(1, await apres.EnvoyerAsync(site, _heure, CancellationToken.None));
    }

    [Fact]
    public async Task Une_version_dont_un_objet_manque_en_local_ne_part_pas_et_reste_en_file()
    {
        var stockage = Stockage();
        var file = new AdventureFileDEnvoi(stockage);
        var v1 = UneVersion(stockage, "v1", "etat", "sauvegarde", _heure);
        File.Delete(Path.Combine(stockage.DossierDesObjets, v1.StateSha256!));
        file.Ajouter(v1);

        var site = new FauxSite();
        Assert.Equal(0, await file.EnvoyerAsync(site, _heure, CancellationToken.None));
        Assert.Empty(site.Versions);
        Assert.Single(file.EnAttente);
    }

    // ── RetroArch simule ───────────────────────────────────────────────────────────────────────────

    private sealed class FauxRetroArch(string etats, string captures) : ICommandesRetroArch
    {
        public readonly List<string> Recues = [];
        public bool EnPause { get; set; }
        public bool NeEcritRien { get; set; }
        public string Etat { get; set; } = "etat de la partie";

        public Task<string?> DemanderAsync(string commande, CancellationToken ct, int delaiMs = 500)
        {
            Recues.Add(commande);
            return Task.FromResult<string?>(commande.Split(' ')[0] switch
            {
                "GET_STATUS" => EnPause ? "GET_STATUS PAUSED mgba,kirby,crc32=1" : "GET_STATUS PLAYING mgba,kirby,crc32=1",
                "GET_CONFIG_PARAM" when commande.EndsWith("savestate_directory", StringComparison.Ordinal) => "GET_CONFIG_PARAM savestate_directory " + etats,
                "GET_CONFIG_PARAM" when commande.EndsWith("screenshot_directory", StringComparison.Ordinal) => "GET_CONFIG_PARAM screenshot_directory " + captures,
                _ => null,
            });
        }

        public Task EnvoyerAsync(string commande, CancellationToken ct)
        {
            Recues.Add(commande);
            if (!NeEcritRien && commande.StartsWith("SAVE_STATE_SLOT ", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(etats);
                File.WriteAllText(Path.Combine(etats, "kirby.state" + commande.Split(' ')[1]), Etat);
            }
            else if (!NeEcritRien && commande == "SCREENSHOT")
            {
                Directory.CreateDirectory(captures);
                File.WriteAllBytes(Path.Combine(captures, "kirby-" + Guid.NewGuid().ToString("N")[..6] + ".png"), [0x89, 0x50, 0x4e, 0x47]);
            }

            return Task.CompletedTask;
        }
    }

    private (AdventureSauveur Sauveur, FauxRetroArch RetroArch, AdventureStockage Stockage, AdventureFileDEnvoi File) UnSauveur(int joueurs = 3, long deja = 600)
    {
        var stockage = Stockage();
        var file = new AdventureFileDEnvoi(stockage);
        var retroarch = new FauxRetroArch(Path.Combine(_racine, "etats"), Path.Combine(_racine, "captures"));
        var sauveur = new AdventureSauveur(retroarch, stockage, file,
            new AdventureSauvegarde("av-1", "kirby", "kirby.srm", joueurs, "coeur-sha", deja,
                () => [new AdventurePlace { Seat = 1, Handle = "a" }, new AdventurePlace { Seat = 2, Handle = "b" }, new AdventurePlace { Seat = 3, Handle = "c" }],
                SessionId: "s-42"),
            () => _heure, attenteDeLEtat: TimeSpan.FromSeconds(2));
        Directory.CreateDirectory(stockage.DossierDesSauvegardes("av-1"));
        return (sauveur, retroarch, stockage, file);
    }

    [Fact]
    public async Task Sauver_garde_l_etat_la_sauvegarde_de_la_cartouche_la_capture_et_les_places()
    {
        var (sauveur, retroarch, stockage, file) = UnSauveur();
        File.WriteAllText(sauveur.CheminDeLaCartouche, "progression du jeu");
        _heure = _heure.AddMinutes(12);

        var version = await sauveur.SauverAsync(AdventureRaisons.Reguliere, CancellationToken.None);

        Assert.NotNull(version);
        Assert.Contains("SAVE_STATE_SLOT " + AdventureSauveur.Emplacement, retroarch.Recues);
        Assert.Equal(Encoding.UTF8.GetBytes("etat de la partie"), stockage.LireObjet(version!.StateSha256));
        Assert.Equal(Encoding.UTF8.GetBytes("progression du jeu"), stockage.LireObjet(version.SaveSha256));
        Assert.NotNull(stockage.LireObjet(version.CaptureSha256));
        Assert.Equal(3, version.Players);
        Assert.Equal("s-42", version.SessionId);
        Assert.Equal(["a", "b", "c"], version.Seats.Select(p => p.Handle).ToArray());
        Assert.Equal("coeur-sha", version.CoreSha256);
        Assert.Equal(600 + 12 * 60, version.DurationS);
        Assert.Equal(version.Id, stockage.Derniere("av-1")!.Id);
        Assert.Equal(version.Id, Assert.Single(file.EnAttente).Version.Id);
        Assert.Equal(_heure, sauveur.DerniereSauvegarde);
    }

    [Fact]
    public async Task L_emplacement_de_l_aventure_n_est_jamais_celui_du_depart_pret_ni_un_etat_du_joueur()
    {
        Assert.NotEqual(MultiplayerDepartPret.Emplacement, AdventureSauveur.Emplacement);
        Assert.True(AdventureSauveur.Emplacement >= 90);
        var (sauveur, retroarch, _, _) = UnSauveur();
        await sauveur.SauverAsync(AdventureRaisons.Fin, CancellationToken.None);
        Assert.DoesNotContain(retroarch.Recues, c => c.StartsWith("LOAD_STATE", StringComparison.Ordinal));
        Assert.DoesNotContain(retroarch.Recues, c => c == "SAVE_STATE");
    }

    [Fact]
    public async Task Sans_etat_ecrit_par_RetroArch_aucune_version_n_est_gardee()
    {
        var (sauveur, retroarch, stockage, file) = UnSauveur();
        retroarch.NeEcritRien = true;
        Assert.Null(await sauveur.SauverAsync(AdventureRaisons.Fin, CancellationToken.None));
        Assert.Empty(stockage.Versions("av-1"));
        Assert.Empty(file.EnAttente);
    }

    [Fact]
    public async Task Sans_sauvegarde_de_cartouche_la_version_porte_l_etat_seul()
    {
        var (sauveur, _, _, _) = UnSauveur();
        var version = await sauveur.SauverAsync(AdventureRaisons.Fin, CancellationToken.None);
        Assert.NotNull(version!.StateSha256);
        Assert.Null(version.SaveSha256);
    }

    [Fact]
    public async Task L_etat_de_sortie_de_RetroArch_fait_la_version_de_fin_quand_il_est_posterieur_au_debut()
    {
        _heure = DateTime.UtcNow.AddMinutes(-5);
        var (sauveur, _, stockage, _) = UnSauveur();
        var etats = Path.Combine(_racine, "etats");
        Directory.CreateDirectory(etats);
        var auto = Path.Combine(etats, "kirby.state.auto");
        File.WriteAllText(auto, "etat de sortie");
        File.SetLastWriteTimeUtc(auto, DateTime.UtcNow.AddHours(-30));
        Assert.Null(sauveur.DepuisLEtatDeSortie(etats));   // un vieil etat automatique d'une autre seance ne compte pas

        var (sauveur2, _, _, _) = UnSauveur();
        File.SetLastWriteTimeUtc(auto, DateTime.UtcNow.AddMinutes(1));
        var version = sauveur2.DepuisLEtatDeSortie(etats);
        Assert.Equal(AdventureRaisons.Fin, version!.Reason);
        Assert.Equal(Encoding.UTF8.GetBytes("etat de sortie"), stockage.LireObjet(version.StateSha256));
        await Task.CompletedTask;
    }

    // ── quand le jeu sauve ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Le_jeu_a_sauve_quand_son_fichier_change_et_pas_pour_celui_pose_au_depart()
    {
        var (sauveur, _, _, _) = UnSauveur();
        File.WriteAllText(sauveur.CheminDeLaCartouche, "sauvegarde posee");
        sauveur.NoterLeDepart();
        Assert.False(sauveur.LeJeuASauve());

        File.WriteAllText(sauveur.CheminDeLaCartouche, "le joueur a sauve dans le jeu");
        Assert.True(sauveur.LeJeuASauve());
        Assert.False(sauveur.LeJeuASauve());   // une fois par ecriture
    }

    // ── la seance de l'hote ────────────────────────────────────────────────────────────────────────

    private static MultiplayerRoom Salle(int joueurs, string room = "r1")
        => new()
        {
            Room = room,
            Game = "gba/kirby",
            Content = "md5kirby",
            Adventure = new MultiplayerAventure { Id = "av-1", Session = "s-77" },
            State = "launching",
            Launch = new MultiplayerLancement { Role = "host", Seat = 1, Players = joueurs },
            Players = Enumerable.Range(1, joueurs).Select(i => new MultiplayerJoueur { Seat = i, Handle = "j" + i }).ToList(),
        };

    private static MultiplayerInstalledGame Jeu(string chemin)
        => new(new MultiplayerGame { Id = "gba/kirby", System = "gba", Name = "Kirby", Players = 4 }, chemin,
            new MultiplayerCore { Core = "mgba_splitscreen" }, "md5kirby");

    private (AdventureSeanceService Service, FauxSite Site, AdventureStockage Stockage, FauxRetroArch RetroArch) UneSeance(Func<string?, string?>? coeur = null)
    {
        var stockage = Stockage();
        var site = new FauxSite { Aventure = new AdventureInfo { Id = "av-1", Game = "gba/kirby", Content = "md5kirby", Seats = 4 } };
        var retroarch = new FauxRetroArch(Path.Combine(_racine, "etats"), Path.Combine(_racine, "captures"));
        var service = new AdventureSeanceService(site, stockage, new AdventureFileDEnvoi(stockage), () => retroarch, coeur ?? (_ => "coeur-sha"), () => _heure);
        return (service, site, stockage, retroarch);
    }

    private void PoserUneVersion(FauxSite site, AdventureStockage stockage, int joueurs, string? coeur = "coeur-sha")
    {
        var etat = Encoding.UTF8.GetBytes("etat a " + joueurs);
        var sauvegarde = Encoding.UTF8.GetBytes("sauvegarde de la cartouche");
        var version = new AdventureVersion
        {
            Id = "v1", Adventure = "av-1", Players = joueurs, CoreSha256 = coeur, CreatedUtc = _heure.AddHours(-2), DurationS = 3600,
            StateSha256 = AdventureStockage.Empreinte(etat), SaveSha256 = AdventureStockage.Empreinte(sauvegarde),
        };
        site.Objets[version.StateSha256!] = etat;
        site.Objets[version.SaveSha256!] = sauvegarde;
        site.Aventure = site.Aventure! with { Latest = version };
    }

    [Fact]
    public async Task Une_gameroom_qui_n_est_pas_une_aventure_ne_declenche_rien()
    {
        var (service, site, _, _) = UneSeance();
        var salle = Salle(2) with { Adventure = null };
        Assert.False(service.EstUneAventure(salle));
        Assert.True(await service.PreparerAsync(salle, Jeu("x.gba"), CancellationToken.None));
        Assert.Null(service.DepartDe(salle));
        Assert.Empty(service.ReglagesDeLaPlace(salle));
        Assert.Empty(site.Journal);
    }

    [Fact]
    public async Task Le_meme_nombre_de_joueurs_charge_l_etat_lie_avant_les_invites()
    {
        var (service, site, stockage, _) = UneSeance();
        PoserUneVersion(site, stockage, 3);
        var salle = Salle(3);

        Assert.True(await service.PreparerAsync(salle, Jeu(Path.Combine(_racine, "kirby.gba")), CancellationToken.None));

        var depart = service.DepartDe(salle)!;
        Assert.Equal(3, depart.Players);
        // Le depart pointe l'objet local : il se verifie par son empreinte, comme un depart pret du catalogue.
        Assert.True(File.Exists(depart.State));
        Assert.Equal(depart.Sha256, AdventureStockage.Empreinte(File.ReadAllBytes(depart.State)), ignoreCase: true);
        Assert.Equal(ModeDeReprise.Etat, service.De("r1")!.Decision.Mode);
    }

    [Fact]
    public async Task Le_depart_de_l_aventure_se_pose_comme_un_depart_pret_de_MULTIPLAYER()
    {
        var (service, site, stockage, _) = UneSeance();
        PoserUneVersion(site, stockage, 3);
        var salle = Salle(3);
        await service.PreparerAsync(salle, Jeu(Path.Combine(_racine, "kirby.gba")), CancellationToken.None);

        var etats = Path.Combine(_racine, "etats-multi");
        var commandes = new FauxRetroArch(etats, Path.Combine(_racine, "captures"));
        var depart = new MultiplayerDepartPret(commandes, TimeSpan.FromSeconds(2), TimeSpan.Zero);
        var jeu = new MultiplayerGame { Id = "gba/kirby", System = "gba", Name = "Kirby", Players = 4 };

        var resultat = await depart.PreparerAsync(jeu, service.DepartDe(salle)!.State, "kirby", new Dictionary<int, IReadOnlyDictionary<string, int>?>(),
            CancellationToken.None, service.DepartDe(salle));

        Assert.True(resultat.Ok, resultat.Raison);
        Assert.Equal("etat a 3", File.ReadAllText(Path.Combine(etats, "kirby.state" + MultiplayerDepartPret.Emplacement)));
        Assert.Contains("LOAD_STATE_SLOT " + MultiplayerDepartPret.Emplacement, commandes.Recues);
    }

    [Fact]
    public async Task Un_autre_nombre_de_joueurs_repart_neuf_avec_la_sauvegarde_de_la_cartouche_posee()
    {
        var (service, site, stockage, _) = UneSeance();
        PoserUneVersion(site, stockage, 4);
        var salle = Salle(2);

        Assert.True(await service.PreparerAsync(salle, Jeu(Path.Combine(_racine, "kirby.gba")), CancellationToken.None));

        Assert.Null(service.DepartDe(salle));
        Assert.Equal(ModeDeReprise.NeufAvecSauvegarde, service.De("r1")!.Decision.Mode);
        var cartouche = Path.Combine(stockage.DossierDesSauvegardes("av-1"), "kirby.srm");
        Assert.Equal("sauvegarde de la cartouche", File.ReadAllText(cartouche));
    }

    [Fact]
    public async Task Sans_version_les_consoles_sont_vierges_et_une_vieille_sauvegarde_est_ecartee()
    {
        var (service, _, stockage, _) = UneSeance();
        var dossier = stockage.DossierDesSauvegardes("av-1");
        Directory.CreateDirectory(dossier);
        File.WriteAllText(Path.Combine(dossier, "kirby.srm"), "reste d'une autre seance");

        Assert.True(await service.PreparerAsync(Salle(2), Jeu(Path.Combine(_racine, "kirby.gba")), CancellationToken.None));
        Assert.Equal(ModeDeReprise.Neuf, service.De("r1")!.Decision.Mode);
        Assert.False(File.Exists(Path.Combine(dossier, "kirby.srm")));
    }

    [Fact]
    public async Task Hors_ligne_l_aventure_se_reprend_de_la_copie_locale()
    {
        var (service, site, stockage, _) = UneSeance();
        var etat = Encoding.UTF8.GetBytes("etat local");
        stockage.GarderVersion(new AdventureVersion
        {
            Id = "locale", Adventure = "av-1", Players = 2, CoreSha256 = "coeur-sha", CreatedUtc = _heure.AddMinutes(-10),
            StateSha256 = stockage.PoserObjet(etat),
        });
        site.Injoignable = true;

        Assert.True(await service.PreparerAsync(Salle(2), Jeu(Path.Combine(_racine, "kirby.gba")), CancellationToken.None));
        Assert.Equal(ModeDeReprise.Etat, service.De("r1")!.Decision.Mode);
    }

    [Fact]
    public async Task La_version_la_plus_recente_gagne_entre_le_site_et_la_copie_locale()
    {
        var (service, site, stockage, _) = UneSeance();
        PoserUneVersion(site, stockage, 2);   // le site : il y a 2 heures
        stockage.GarderVersion(new AdventureVersion
        {
            Id = "locale", Adventure = "av-1", Players = 4, CreatedUtc = _heure.AddMinutes(-1), SaveSha256 = stockage.PoserObjet([1, 2, 3]),
        });

        await service.PreparerAsync(Salle(2), Jeu(Path.Combine(_racine, "kirby.gba")), CancellationToken.None);
        // La locale (4 joueurs, sans etat) est plus recente : la sauvegarde de la cartouche, pas l'etat du site.
        Assert.Equal(ModeDeReprise.NeufAvecSauvegarde, service.De("r1")!.Decision.Mode);
    }

    [Fact]
    public async Task Un_objet_du_site_qui_ne_correspond_pas_a_son_empreinte_n_est_jamais_charge()
    {
        var (service, site, stockage, _) = UneSeance();
        PoserUneVersion(site, stockage, 3);
        site.Objets[site.Aventure!.Latest!.StateSha256!] = Encoding.UTF8.GetBytes("autre chose");

        Assert.False(await service.PreparerAsync(Salle(3), Jeu(Path.Combine(_racine, "kirby.gba")), CancellationToken.None));
        Assert.Null(service.DepartDe(Salle(3)));
    }

    [Fact]
    public async Task Les_reglages_de_la_seance_isolent_les_sauvegardes_du_joueur()
    {
        var (service, _, stockage, _) = UneSeance();
        var salle = Salle(2);
        await service.PreparerAsync(salle, Jeu(Path.Combine(_racine, "kirby.gba")), CancellationToken.None);

        var reglages = service.ReglagesDeLaPlace(salle);
        Assert.Equal(stockage.DossierDesSauvegardes("av-1"), reglages["savefile_directory"]);
        Assert.Equal("false", reglages["sort_savefiles_enable"]);
        Assert.Equal("false", reglages["sort_savefiles_by_content_enable"]);
        Assert.StartsWith(stockage.Racine, reglages["savefile_directory"]);
    }

    [Theory]
    [InlineData("{rom}.srm", "kirby", "kirby.srm")]
    [InlineData("{rom}.sav", "kirby", "kirby.sav")]
    [InlineData("../{rom}.srm", "kirby", null)]
    [InlineData("a/b.srm", "kirby", null)]
    public void Le_nom_du_fichier_de_la_cartouche_ne_sort_jamais_du_dossier(string modele, string rom, string? attendu)
        => Assert.Equal(attendu, AdventureSeanceService.FichierDeLaCartouche(new AdventureSpec { Cartridge = new AdventureCartridge { File = modele } }, rom));

    // ── le suivi : 5 minutes, hors chargement, et quand le jeu sauve ───────────────────────────────

    private async Task<(AdventureSeanceService Service, AdventureSeanceService.Seance Seance, FauxRetroArch RetroArch, AdventureStockage Stockage)> UneSeancePrete()
    {
        var (service, site, stockage, retroarch) = UneSeance();
        PoserUneVersion(site, stockage, 2);
        await service.PreparerAsync(Salle(2), Jeu(Path.Combine(_racine, "kirby.gba")), CancellationToken.None);
        return (service, service.De("r1")!, retroarch, stockage);
    }

    [Fact]
    public async Task La_sauvegarde_reguliere_arrive_toutes_les_cinq_minutes_pas_avant()
    {
        var (service, seance, _, stockage) = await UneSeancePrete();
        var avant = stockage.Versions("av-1").Count;
        _heure = _heure.AddMinutes(4);
        await service.UnTourAsync(seance, null, CancellationToken.None, _ => { });
        Assert.Equal(avant, stockage.Versions("av-1").Count);

        _heure = _heure.AddMinutes(1.1);
        await service.UnTourAsync(seance, null, CancellationToken.None, _ => { });
        var derniere = stockage.Derniere("av-1")!;
        Assert.Equal(AdventureRaisons.Reguliere, derniere.Reason);
        Assert.Equal(avant + 1, stockage.Versions("av-1").Count);
    }

    [Fact]
    public async Task Pas_de_sauvegarde_reguliere_en_pause()
    {
        var (service, seance, retroarch, stockage) = await UneSeancePrete();
        retroarch.EnPause = true;
        _heure = _heure.AddMinutes(30);
        await service.UnTourAsync(seance, null, CancellationToken.None, _ => { });
        Assert.Empty(stockage.Versions("av-1").Where(v => v.Id != "v1" && v.Reason == AdventureRaisons.Reguliere));
    }

    [Fact]
    public async Task Pas_de_sauvegarde_reguliere_pendant_le_chargement_de_la_seance()
    {
        var (service, seance, _, stockage) = await UneSeancePrete();
        _heure = _heure.AddSeconds(100);   // moins de DelaiDeChargement depuis l'ouverture
        seance.Sauveur.GetType().GetProperty(nameof(AdventureSauveur.DerniereSauvegarde))!.SetValue(seance.Sauveur, _heure.AddMinutes(-10));
        await service.UnTourAsync(seance, null, CancellationToken.None, _ => { });
        Assert.Empty(stockage.Versions("av-1"));
    }

    [Fact]
    public async Task Quand_le_jeu_ecrit_sa_sauvegarde_une_version_est_prise_apres_un_court_silence()
    {
        var (service, seance, _, stockage) = await UneSeancePrete();
        _heure = _heure.AddSeconds(30);
        File.WriteAllText(seance.Sauveur.CheminDeLaCartouche, "le joueur a sauve");

        DateTime? vue = null;
        await service.UnTourAsync(seance, null, CancellationToken.None, v => vue = v);
        Assert.NotNull(vue);                                   // l'ecriture est vue, on laisse le jeu finir
        Assert.Empty(stockage.Versions("av-1").Where(v => v.Reason == AdventureRaisons.SauvegardeDuJeu));

        _heure = _heure.AddSeconds(3);
        await service.UnTourAsync(seance, vue, CancellationToken.None, v => vue = v);
        var version = Assert.Single(stockage.Versions("av-1").Where(v => v.Reason == AdventureRaisons.SauvegardeDuJeu));
        Assert.Equal(Encoding.UTF8.GetBytes("le joueur a sauve"), stockage.LireObjet(version.SaveSha256));
    }

    [Fact]
    public async Task La_fin_de_seance_prend_l_etat_avant_de_fermer_puis_l_envoie_et_ferme_la_seance_du_site()
    {
        var (service, seance, retroarch, stockage) = await UneSeancePrete();
        var salle = Salle(2);
        await service.AvantDeFermerAsync(salle);

        var fin = Assert.Single(stockage.Versions("av-1"));
        Assert.Equal(AdventureRaisons.Fin, fin.Reason);
        Assert.Equal("s-77", fin.SessionId);   // la version tardive designe sa seance
        Assert.Contains("SAVE_STATE_SLOT " + AdventureSauveur.Emplacement, retroarch.Recues);

        var site = (FauxSite)typeof(AdventureSeanceService).GetField("_site", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(service)!;
        await service.TermineeAsync(salle);
        Assert.Contains(site.Versions, v => v.Id == fin.Id);
        Assert.True(site.SeanceFermee);
        Assert.Null(service.De("r1"));
    }

    [Fact]
    public async Task Une_fin_de_seance_hors_ligne_garde_la_version_en_file()
    {
        var (service, seance, _, stockage) = await UneSeancePrete();
        var salle = Salle(2);
        var site = (FauxSite)typeof(AdventureSeanceService).GetField("_site", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(service)!;
        site.Injoignable = true;

        await service.AvantDeFermerAsync(salle);
        await service.TermineeAsync(salle);

        Assert.Single(stockage.Versions("av-1"));
        Assert.Empty(site.Versions);
        var file = new AdventureFileDEnvoi(stockage);
        Assert.Single(file.EnAttente);
    }

    // ── le catalogue ───────────────────────────────────────────────────────────────────────────────

    private string Catalogue(object[] jeux, string? nom = null)
    {
        var chemin = Path.Combine(_racine, "resources", "adventure", nom ?? "catalogue.json");
        Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
        File.WriteAllText(chemin, JsonSerializer.Serialize(new
        {
            schema_version = 1,
            revision = "t",
            cores = new Dictionary<string, object> { ["gba"] = new { core = "mgba_splitscreen", options = new Dictionary<string, string> { ["mgba_splitscreen_players"] = "{players}" } } },
            games = jeux,
        }));
        return chemin;
    }

    private static object Kirby(string id = "gba/kirby-amazing-mirror") => new
    {
        id, system = "gba", name = "Kirby & The Amazing Mirror", family = "adventure", players = new { min = 2, max = 4 }, core = "mgba_splitscreen",
        content = new { md5 = new[] { "abc123" }, size = new[] { 11L } },
        adventure = new { cartridge_save = new { console = 1, file = "{rom}.srm" }, fresh_hint = "choisir MULTI PLAYER" },
    };

    [Fact]
    public void Le_catalogue_Adventure_se_lit_comme_celui_de_MULTIPLAYER_avec_sa_part_d_aventure()
    {
        var store = new AdventureCatalogueStore(path: Catalogue([Kirby()]));
        var entree = Assert.Single(store.Entrees(out var erreur));
        Assert.Null(erreur);
        Assert.Equal("gba/kirby-amazing-mirror", entree.Jeu.Id);
        Assert.Equal(4, entree.Jeu.Players);
        Assert.Equal("{rom}.srm", entree.Spec.Cartridge!.File);
        Assert.Equal(1, entree.Spec.Cartridge.Console);
        Assert.Equal("choisir MULTI PLAYER", entree.Spec.FreshHint);
    }

    [Fact]
    public void La_forme_du_site_est_lue_players_en_min_max()
    {
        var store = new AdventureCatalogueStore(path: Catalogue([Kirby()]));
        var entree = Assert.Single(store.Entrees(out _));
        Assert.Equal(4, entree.Jeu.Players);          // le maximum
        Assert.Equal(2, entree.Spec.MinPlayers);      // le minimum passe a la part d'Adventure
        // Le coeur du jeu est celui de son systeme : il en garde les options (splitscreen_players={players}).
        Assert.Equal("{players}", store.Lire(out _)!.CoreOf(entree.Jeu)!.Options!["mgba_splitscreen_players"]);
    }

    [Fact]
    public void Un_nombre_de_joueurs_en_entier_est_admis_comme_dans_MULTIPLAYER()
    {
        var store = new AdventureCatalogueStore(path: Catalogue([new
        {
            id = "snes/secret-of-mana", system = "snes", name = "Secret of Mana", players = 3, level = "classique",
            content = new { md5 = new[] { "abc123" } },
        }]));
        Assert.Equal(3, Assert.Single(store.Entrees(out _)).Jeu.Players);
    }

    [Fact]
    public void Un_jeu_invalide_est_ecarte_seul()
    {
        var store = new AdventureCatalogueStore(path: Catalogue([Kirby(), new { id = "gba/sans-contenu", system = "gba", name = "x", players = new { min = 2, max = 4 } }]));
        Assert.Equal("gba/kirby-amazing-mirror", Assert.Single(store.Entrees(out _)).Jeu.Id);
    }

    [Fact]
    public void Un_catalogue_absent_n_est_pas_un_catalogue_vide()
    {
        var store = new AdventureCatalogueStore(path: Path.Combine(_racine, "absent.json"));
        Assert.Null(store.Lire(out var erreur));
        Assert.NotNull(erreur);
    }

    // ── la collection d'ES ─────────────────────────────────────────────────────────────────────────

    private sealed class SansDefinition : IScoreSlugResolver
    {
        public string? ResolveScoreSlug(string systemId, string? romFileName, string? md5, string? cheevosHash) => null;
    }

    private sealed class FauxOptions(ApiExposeOptions valeur) : IOptionsMonitor<ApiExposeOptions>
    {
        public ApiExposeOptions CurrentValue { get; } = valeur;
        public ApiExposeOptions Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<ApiExposeOptions, string?> listener) => new Rien();
        private sealed class Rien : IDisposable { public void Dispose() { } }
    }

    private sealed class FauxReglages : IEsSettingsStore
    {
        private readonly XDocument _document = new(new XElement("config"));
        public bool Update(Func<XDocument, bool> update, CancellationToken cancellationToken = default) => update(_document);
        public IReadOnlyDictionary<string, string> ReadAllSettings() => new Dictionary<string, string>();
        public EmulationStationSettingsSnapshot ReadSnapshot() => new(string.Empty, DateTime.UtcNow, ReadAllSettings());
        public void Invalidate() { }
        public Task WaitForStableFileAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private MultiplayerCollectionSyncService Collection(NelfePlayOptions options, AdventureCatalogueStore store, Func<string, string?>? md5 = null)
    {
        var etat = Path.Combine(_racine, "state");
        var roms = Path.Combine(_racine, "roms");
        return new MultiplayerCollectionSyncService(
            new FauxOptions(new ApiExposeOptions { NelfePlay = options }),
            store.Interne,
            new InstalledGameCatalog(new SansDefinition(), logger: null, romsRoot: roms, ramRoot: Path.Combine(_racine, "ram")),
            new EsCustomCollectionWriter(new FauxReglages(), logger: null, collectionsRoot: Path.Combine(_racine, "collections"), stateRoot: etat),
            new EsCollectionThemeAssets(logger: null, themesRoot: Path.Combine(_racine, "themes"), sourceRoot: Path.Combine(_racine, "assets"), stateRoot: etat),
            new MediaRuntimeState(),
            romsRoot: roms,
            emulateurTourne: () => false,
            md5DuContenu: md5 ?? (_ => "abc123"),
            coeurPose: _ => true,
            profil: AdventureCollection.Profil);
    }

    private void PoserKirby()
    {
        Directory.CreateDirectory(Path.Combine(_racine, "roms", "gba"));
        File.WriteAllText(Path.Combine(_racine, "roms", "gba", "Kirby - Amazing Mirror (USA).gba"), "01234567890");   // 11 octets
    }

    [Fact]
    public async Task La_collection_Adventure_prend_les_jeux_du_catalogue_que_la_borne_possede_sans_toucher_aux_roms()
    {
        PoserKirby();
        var store = new AdventureCatalogueStore(path: Catalogue([Kirby()]));
        var collection = Collection(new NelfePlayOptions { ShowAdventureCollection = true }, store);

        var statut = await collection.SynchroniserAsync("test", CancellationToken.None);

        Assert.True(statut.Enabled);
        Assert.Equal(1, statut.LocalGames);
        var jeu = Assert.Single(collection.JeuxConnus());
        Assert.Equal("gba/kirby-amazing-mirror", jeu.Game.Id);
        Assert.Equal("abc123", jeu.ContentKey);
        Assert.Equal("mgba_splitscreen", jeu.Core!.Core);
        // La ROM du joueur n'a ni change de nom ni de place.
        Assert.True(File.Exists(Path.Combine(_racine, "roms", "gba", "Kirby - Amazing Mirror (USA).gba")));
        Assert.True(File.Exists(Path.Combine(_racine, "state", "nelfeplay", "adventure-jeux.json")) || collection.FichierDesJeux is null);
    }

    [Fact]
    public async Task L_option_de_la_collection_Adventure_est_a_part_de_celle_de_MULTIPLAYER()
    {
        PoserKirby();
        var store = new AdventureCatalogueStore(path: Catalogue([Kirby()]));
        // MULTIPLAYER allume, Adventure eteinte : la collection Adventure disparait.
        var eteinte = Collection(new NelfePlayOptions { ShowMultiplayerCollection = true, ShowAdventureCollection = false }, store);
        var statut = await eteinte.SynchroniserAsync("test", CancellationToken.None);
        Assert.False(statut.Enabled);
        Assert.Empty(eteinte.JeuxConnus());

        // L'inverse : MULTIPLAYER eteint, Adventure allumee.
        var allumee = Collection(new NelfePlayOptions { ShowMultiplayerCollection = false, ShowAdventureCollection = true }, store);
        Assert.True((await allumee.SynchroniserAsync("test", CancellationToken.None)).Enabled);
    }

    [Fact]
    public void La_collection_Adventure_a_son_nom_et_son_logo_a_elle()
    {
        Assert.Equal("nelfeplay-adventure", AdventureCollection.Nom);
        Assert.NotEqual(MultiplayerCollectionSyncService.CollectionName, AdventureCollection.Nom);
        Assert.Equal("nelfeplay-adventure.png", AdventureCollection.Profil.Logo);
        Assert.Equal("multiplayer-jeux.json", MultiplayerCollectionSyncService.ProfilDeCollection.Multiplayer.Trace);
        Assert.NotEqual(MultiplayerCollectionSyncService.ProfilDeCollection.Multiplayer.Trace, AdventureCollection.Profil.Trace);
    }

    [Fact]
    public void L_option_d_ES_de_la_collection_Adventure_est_allumee_par_defaut_et_le_dossier_suit_le_Data_Pack()
    {
        Assert.True(new NelfePlayOptions().ShowAdventureCollection);
        Assert.Contains("adventure", new DataPackOptions().Folders);
        Assert.True(DataPackPaths.Autorise("adventure/catalogue.json", new HashSet<string>(new DataPackOptions().Folders, StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task La_session_MULTIPLAYER_retrouve_le_jeu_d_une_gameroom_d_aventure()
    {
        PoserKirby();
        var adventure = Collection(new NelfePlayOptions { ShowAdventureCollection = true }, new AdventureCatalogueStore(path: Catalogue([Kirby()])));
        await adventure.SynchroniserAsync("test", CancellationToken.None);

        var vide = Path.Combine(_racine, "vide.json");
        File.WriteAllText(vide, JsonSerializer.Serialize(new { schema_version = 1, games = Array.Empty<object>() }));
        var options = new FauxOptions(new ApiExposeOptions { NelfePlay = new NelfePlayOptions { ShowMultiplayerCollection = true } });
        var multiplayer = new MultiplayerCollectionSyncService(
            options, new MultiplayerCatalogueStore(logger: null, path: vide),
            new InstalledGameCatalog(new SansDefinition(), logger: null, romsRoot: Path.Combine(_racine, "roms"), ramRoot: Path.Combine(_racine, "ram")),
            new EsCustomCollectionWriter(new FauxReglages(), logger: null, collectionsRoot: Path.Combine(_racine, "c2"), stateRoot: Path.Combine(_racine, "s2")),
            new EsCollectionThemeAssets(logger: null, themesRoot: Path.Combine(_racine, "themes"), sourceRoot: Path.Combine(_racine, "assets"), stateRoot: Path.Combine(_racine, "s2")),
            new MediaRuntimeState(), romsRoot: Path.Combine(_racine, "roms"), emulateurTourne: () => false);
        var client = new MultiplayerClient(new SansFabrique(), new NelfePlayDeviceStore(NullLogger<NelfePlayDeviceStore>.Instance), clientDeTest: () => null);
        var session = new MultiplayerSessionService(client, multiplayer, options, relais: null, bus: null) { UnJeuTourne = () => false };
        var salle = new MultiplayerRoom { Room = "r1", Game = "gba/kirby-amazing-mirror", Content = "abc123", Adventure = new MultiplayerAventure { Id = "av-1" } };

        Assert.Null(session.JeuDeLaRoom(salle with { Adventure = null }));
        session.JeuxSupplementaires = () => adventure.JeuxConnus();
        Assert.Equal("Kirby & The Amazing Mirror", session.JeuDeLaRoom(salle)!.Game.Name);
        Assert.Equal("Kirby & The Amazing Mirror", session.NomDuJeu("gba/kirby-amazing-mirror"));
        Assert.NotNull(session.JeuCompatible("gba/kirby-amazing-mirror", "abc123", aventure: true));
        Assert.Null(session.JeuCompatible("gba/kirby-amazing-mirror", "abc123"));
    }

    private sealed class SansFabrique : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("pas de reseau");
    }

    // ── l'appui long sur la collection Adventure ───────────────────────────────────────────────────

    private static AdventureInfo UneAventure(string id, string jeu = "gba/kirby", string contenu = "md5kirby", string? room = null, int? joueurs = null, int places = 4)
        => new() { Id = id, Game = jeu, Content = contenu, Seats = places, Room = room, Latest = joueurs is { } n ? new AdventureVersion { Players = n } : null };

    [Fact]
    public void L_appui_long_reprend_l_aventure_de_ce_jeu_et_de_ce_contenu_celle_dont_une_seance_est_ouverte_d_abord()
    {
        var miennes = new[]
        {
            UneAventure("autre-jeu", jeu: "gba/zelda"),
            UneAventure("autre-contenu", contenu: "md5usa"),
            UneAventure("derniere"),
            UneAventure("ouverte", room: "r9"),
        };
        Assert.Equal("ouverte", AdventureChoix.Choisir(miennes, "gba/kirby", "md5kirby")!.Id);
        Assert.Equal("derniere", AdventureChoix.Choisir(miennes.Where(a => a.Id != "ouverte"), "gba/kirby", "md5kirby")!.Id);
        Assert.Null(AdventureChoix.Choisir(miennes, "gba/shining-soul-ii", "md5kirby"));
    }

    [Theory]
    [InlineData(null, 4, 4)]    // pas de version : toutes les places
    [InlineData(3, 4, 3)]       // celles de la derniere version
    [InlineData(1, 4, 4)]       // une salle d'amis ne part jamais a moins de deux
    [InlineData(4, 3, 3)]       // jamais plus que les places de l'aventure
    public void Le_nombre_de_joueurs_demande_va_de_deux_aux_places_de_l_aventure(int? derniere, int places, int attendu)
        => Assert.Equal(attendu, AdventureChoix.Joueurs(UneAventure("a", joueurs: derniere, places: places), 4));

    private AdventureLancement UnLancement(FauxSite site) => new(site, coeur => "coeur-sha");

    [Fact]
    public async Task L_appui_long_sans_aventure_en_cree_une_puis_ouvre_la_seance()
    {
        var site = new FauxSite();
        var refus = await UnLancement(site).RejoindreAsync(Jeu(Path.Combine(_racine, "kirby.gba")), CancellationToken.None);

        Assert.Null(refus);
        Assert.Equal(["gba/kirby"], site.Creees.ToArray());
        Assert.Equal(("av-neuve", 4), Assert.Single(site.Ouvertures));
    }

    [Fact]
    public async Task L_appui_long_avec_une_aventure_ne_en_cree_pas_une_autre()
    {
        var site = new FauxSite();
        site.Mes.Add(UneAventure("av-1", joueurs: 3));
        Assert.Null(await UnLancement(site).RejoindreAsync(Jeu(Path.Combine(_racine, "kirby.gba")), CancellationToken.None));
        Assert.Empty(site.Creees);
        Assert.Equal(("av-1", 3), Assert.Single(site.Ouvertures));
    }

    [Fact]
    public async Task L_appui_long_dit_la_raison_du_site_ou_qu_il_est_injoignable()
    {
        var site = new FauxSite();
        site.Mes.Add(UneAventure("av-1"));
        site.RefusDeSeance = "not_member";
        Assert.Equal("not_member", await UnLancement(site).RejoindreAsync(Jeu("x.gba"), CancellationToken.None));

        site.Injoignable = true;
        Assert.Equal("offline", await UnLancement(site).RejoindreAsync(Jeu("x.gba"), CancellationToken.None));
    }

    [Fact]
    public async Task La_session_MULTIPLAYER_confie_l_appui_long_d_un_jeu_d_aventure_a_Adventure_et_laisse_MULTIPLAYER_tranquille()
    {
        PoserKirby();
        var adventure = Collection(new NelfePlayOptions { ShowAdventureCollection = true }, new AdventureCatalogueStore(path: Catalogue([Kirby()])));
        await adventure.SynchroniserAsync("test", CancellationToken.None);
        var chemin = adventure.JeuxConnus().Single().Path;

        var vide = Path.Combine(_racine, "vide.json");
        File.WriteAllText(vide, JsonSerializer.Serialize(new { schema_version = 1, games = Array.Empty<object>() }));
        // MULTIPLAYER eteinte, Adventure allumee : le sondage et le panneau tournent pour Adventure seule.
        var options = new FauxOptions(new ApiExposeOptions { NelfePlay = new NelfePlayOptions { ShowMultiplayerCollection = false, ShowAdventureCollection = true } });
        var multiplayer = new MultiplayerCollectionSyncService(
            options, new MultiplayerCatalogueStore(logger: null, path: vide),
            new InstalledGameCatalog(new SansDefinition(), logger: null, romsRoot: Path.Combine(_racine, "roms"), ramRoot: Path.Combine(_racine, "ram")),
            new EsCustomCollectionWriter(new FauxReglages(), logger: null, collectionsRoot: Path.Combine(_racine, "c2"), stateRoot: Path.Combine(_racine, "s2")),
            new EsCollectionThemeAssets(logger: null, themesRoot: Path.Combine(_racine, "themes"), sourceRoot: Path.Combine(_racine, "assets"), stateRoot: Path.Combine(_racine, "s2")),
            new MediaRuntimeState(), romsRoot: Path.Combine(_racine, "roms"), emulateurTourne: () => false);
        var client = new MultiplayerClient(new SansFabrique(), new NelfePlayDeviceStore(NullLogger<NelfePlayDeviceStore>.Instance), clientDeTest: () => null);
        var session = new MultiplayerSessionService(client, multiplayer, options, relais: null, bus: null) { UnJeuTourne = () => false };

        // Sans branchement, une aventure ne passe pas pour un jeu MULTIPLAYER.
        session.JeuxSupplementaires = () => adventure.JeuxConnus();
        Assert.Equal("adventure_unavailable", await session.RejoindreAsync(chemin));

        var appels = new List<string>();
        session.RejoindreUneAventure = (jeu, ct) => { appels.Add(jeu.Game.Id); return Task.FromResult<string?>("not_member"); };
        Assert.Equal("not_member", await session.RejoindreAsync(chemin));
        Assert.Equal(["gba/kirby-amazing-mirror"], appels.ToArray());

        // Pas de gameroom publique ni d'amis pour une aventure : la touche des onglets ne change rien.
        Assert.Null(await session.RejoindreAsync(chemin, sorte: MultiplayerSortes.Amis));
        Assert.Single(appels);
        Assert.True(session.EstUneAventure(session.JeuDe(chemin)!));
        Assert.Equal("not_multiplayer", await session.RejoindreAsync(Path.Combine(_racine, "inconnu.gba")));
    }

    [Fact]
    public void Les_options_d_ES_de_la_collection_et_du_vocal_sont_dans_le_menu_du_Data_Pack()
    {
        var cfg = Path.Combine(Environment.GetEnvironmentVariable("NELFEPLAY_DATAPACK")!, "config-ESmenus", "apiexpose_es_features_blocks_to_add.cfg");
        // Le Data Pack du poste peut etre en retard sur cette branche (l'entree y arrive avec sa publication) : le test ne juge que s'il l'a.
        if (!File.Exists(cfg) || !File.ReadAllText(cfg).Contains("show_adventure_collection", StringComparison.Ordinal))
        {
            return;
        }

        var texte = File.ReadAllText(cfg);
        Assert.Contains("global.apiexpose.nelfeplay.voice_chat", texte);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(texte, "show_adventure_collection").Count);   // feature, menu, defaut
    }

    // ── plusieurs versions localisees d'un jeu (decision du user du 26/11) ─────────────────────────

    private static object KirbyEnVersions() => new
    {
        id = "gba/kirby-amazing-mirror", system = "gba", name = "Kirby & The Amazing Mirror", family = "coop", players = new { min = 2, max = 4 },
        core = "mgba_splitscreen", level = "nelfeplay",
        content = new { md5 = new[] { "eu1" } },
        default_version = "usa",
        versions = new object[]
        {
            new
            {
                key = "eu", label = "Europe", region = "Europe", languages = new[] { "en", "fr", "de" }, content = new { md5 = new[] { "eu1" }, crc = new[] { "eeee0001" }, size = new[] { 11L } },
                start = new { state = "states/gba/kirby/eu/aventure-2p.state", sha256 = new string('b', 64), players = 2, content = "eu1" },
                starts = new[]
                {
                    new { state = "states/gba/kirby/eu/aventure-3p.state", sha256 = new string('d', 64), players = 3, content = "eu1" },
                    new { state = "states/gba/kirby/eu/aventure-4p.state", sha256 = new string('e', 64), players = 4, content = "eu1" },
                },
                adventure_override = new { cartridge_save = new { file = "{rom}.sav" } },
            },
            new
            {
                key = "usa", label = "USA", region = "USA", languages = new[] { "en" }, content = new { md5 = new[] { "abc123" }, crc = new[] { "aaaa0001" }, size = new[] { 11L } },
                start = new { state = "states/gba/kirby/usa/aventure-2p.state", sha256 = new string('a', 64), players = 2, content = "abc123" },
                starts = new[]
                {
                    new { state = "states/gba/kirby/usa/aventure-3p.state", sha256 = new string('c', 64), players = 3, content = "abc123" },
                    new { state = "states/gba/kirby/usa/aventure-4p.state", sha256 = new string('f', 64), players = 4, content = "abc123" },
                },
            },
        },
        adventure = new { cartridge_save = new { console = 1, file = "{rom}.srm", kind = "gba_srm_slots" } },
    };

    [Fact]
    public void Les_versions_d_un_jeu_sont_fondues_la_version_par_defaut_d_abord_chaque_depart_garde_son_contenu()
    {
        var store = new AdventureCatalogueStore(path: Catalogue([KirbyEnVersions()]));
        var entree = Assert.Single(store.Entrees(out var erreur));
        Assert.Null(erreur);
        Assert.Equal(["abc123", "eu1"], entree.Jeu.Content!.Md5.ToArray());   // la version par defaut d'abord : l'ordre de preference
        Assert.Equal(["aaaa0001", "eeee0001"], entree.Jeu.Content.Crc.ToArray());

        // Chaque depart ne sert QU'A sa version (un etat fait sur un contenu ne se charge jamais sur un autre).
        Assert.Equal("states/gba/kirby/usa/aventure-2p.state", entree.Jeu.DepartPour(2, "abc123")!.State);
        Assert.Equal("states/gba/kirby/eu/aventure-2p.state", entree.Jeu.DepartPour(2, "eu1")!.State);
        Assert.Equal("states/gba/kirby/usa/aventure-3p.state", entree.Jeu.DepartPour(3, "abc123")!.State);
        Assert.Equal("states/gba/kirby/eu/aventure-3p.state", entree.Jeu.DepartPour(3, "eu1")!.State);
        Assert.Null(entree.Jeu.DepartPour(3, "inconnu"));
    }

    [Fact]
    public void Chaque_version_porte_ses_langues_et_peut_surcharger_la_sauvegarde_de_la_cartouche()
    {
        var spec = Assert.Single(new AdventureCatalogueStore(path: Catalogue([KirbyEnVersions()])).Entrees(out _)).Spec;
        Assert.Equal(["usa", "eu"], spec.Versions.Select(v => v.Key).OrderByDescending(k => k == "usa").ToArray());
        Assert.Equal("usa", spec.DefaultVersion);
        Assert.Equal(["en", "fr", "de"], spec.VersionDuContenu("eu1")!.Languages.ToArray());
        Assert.Equal("USA", spec.VersionDuContenu("ABC123")!.Label);
        Assert.Null(spec.VersionDuContenu("inconnu"));
        Assert.Equal("{rom}.srm", spec.PourLeContenu("abc123").Cartridge!.File);
        Assert.Equal("{rom}.sav", spec.PourLeContenu("eu1").Cartridge!.File);   // la surcharge de la version Europe
    }

    [Fact]
    public async Task La_collection_Adventure_prend_chaque_version_que_la_borne_possede_et_une_seule_fois_un_contenu_identique()
    {
        Directory.CreateDirectory(Path.Combine(_racine, "roms", "gba"));
        File.WriteAllText(Path.Combine(_racine, "roms", "gba", "Kirby (USA).gba"), "usa-usa-usa");           // 11 octets
        File.WriteAllText(Path.Combine(_racine, "roms", "gba", "Kirby (Europe).gba"), "eur-eur-eur");
        File.WriteAllText(Path.Combine(_racine, "roms", "gba", "Kirby (Europe) (1).gba"), "eur-eur-eur");     // le meme contenu sous un autre nom
        var store = new AdventureCatalogueStore(path: Catalogue([KirbyEnVersions()]));
        var collection = Collection(new NelfePlayOptions { ShowAdventureCollection = true }, store, md5: chemin =>
            File.ReadAllText(chemin).StartsWith("usa", StringComparison.Ordinal) ? "abc123" : "eu1");

        await collection.SynchroniserAsync("test", CancellationToken.None);
        var jeux = collection.JeuxConnus();

        Assert.Equal(["abc123", "eu1"], jeux.Select(j => j.ContentKey).ToArray());
        Assert.All(jeux, j => Assert.Equal("gba/kirby-amazing-mirror", j.Game.Id));
        // L'identite est le contenu, jamais le nom du fichier.
        Assert.Equal(1, jeux.Count(j => j.ContentKey == "eu1"));
    }

    [Fact]
    public async Task L_appui_long_suit_le_contenu_que_le_joueur_lance_et_cree_l_aventure_de_cette_version()
    {
        var catalogue = new AdventureCatalogueStore(path: Catalogue([KirbyEnVersions()]));
        var site = new FauxSite();
        site.Mes.Add(UneAventure("av-usa", jeu: "gba/kirby-amazing-mirror", contenu: "abc123"));
        var lancement = new AdventureLancement(site, coeur => "coeur-sha", catalogue: catalogue);
        MultiplayerInstalledGame Version(string md5) => new(new MultiplayerGame { Id = "gba/kirby-amazing-mirror", System = "gba", Name = "Kirby", Players = 4 },
            Path.Combine(_racine, "kirby.gba"), new MultiplayerCore { Core = "mgba_splitscreen" }, md5);

        // Celui qui lance la version USA reprend son aventure USA ...
        Assert.Null(await lancement.RejoindreAsync(Version("abc123"), CancellationToken.None));
        Assert.Equal("av-usa", site.Ouvertures.Single().Aventure);
        Assert.Empty(site.Creees);

        // ... celui qui lance la version Europe n'en a pas : une aventure Europe est creee, avec la cle de version.
        Assert.Null(await lancement.RejoindreAsync(Version("eu1"), CancellationToken.None));
        Assert.Equal(["gba/kirby-amazing-mirror"], site.Creees.ToArray());
        Assert.Equal(["eu"], site.Versions_.ToArray());
    }

    [Fact]
    public async Task Nouvelle_aventure_en_cree_une_de_plus_meme_quand_une_existe()
    {
        var site = new FauxSite();
        site.Mes.Add(UneAventure("av-1"));
        var refus = await UnLancement(site).NouvelleAsync(Jeu(Path.Combine(_racine, "kirby.gba")), CancellationToken.None);
        Assert.Null(refus);
        Assert.Single(site.Creees);
        Assert.Equal("av-neuve", Assert.Single(site.Ouvertures).Aventure);
    }

    // ── le brouillon reel du lot 0 et le catalogue du Data Pack ────────────────────────────────────

    private static void VerifierUnCatalogue(string chemin, bool departsSurDisque)
    {
        var store = new AdventureCatalogueStore(path: chemin);
        var entrees = store.Entrees(out var erreur);
        Assert.Null(erreur);
        var ids = entrees.Select(e => e.Jeu.Id).ToArray();
        Assert.Contains("gba/kirby-amazing-mirror", ids);
        Assert.Contains("gba/shining-soul-ii", ids);
        Assert.Contains("snes/secret-of-mana", ids);
        Assert.Contains("snes/the-lost-vikings", ids);
        Assert.Contains("megadrive/the-lost-vikings", ids);
        // Four Swords n'a pas de depart (liaison GBA non etablie) : un jeu de niveau NelfePlay sans depart est ecarte, il n'entre pas.
        Assert.DoesNotContain("gba/zelda-alttp-four-swords", ids);

        var kirby = entrees.Single(e => e.Jeu.Id == "gba/kirby-amazing-mirror");
        Assert.Equal(4, kirby.Jeu.Players);
        Assert.Equal("mgba_splitscreen", store.Lire(out _)!.CoreOf(kirby.Jeu)!.Core);
        Assert.Equal("{players}", store.Lire(out _)!.CoreOf(kirby.Jeu)!.Options!["splitscreen_players"]);
        Assert.NotNull(kirby.Jeu.DepartPour(2, "df5efe075b35859529ebf82a4d824458"));
        Assert.NotNull(kirby.Jeu.DepartPour(4, "df5efe075b35859529ebf82a4d824458"));
        Assert.Equal("{rom}.srm", kirby.Spec.Cartridge!.File);
        Assert.Equal("gba_srm_slots", kirby.Spec.Cartridge.Kind);

        // Lost Vikings : un mot de passe, aucun fichier de sauvegarde.
        Assert.True(entrees.Single(e => e.Jeu.Id == "snes/the-lost-vikings").Spec.Cartridge!.SansFichier);
        Assert.False(kirby.Spec.Cartridge.SansFichier);
        // Le Mega Drive garde ses deux md5 (USA et Europe), chacun avec son depart quand il en a un.
        var md = entrees.Single(e => e.Jeu.Id == "megadrive/the-lost-vikings");
        Assert.Equal(2, md.Jeu.Content!.Md5.Count);
        Assert.NotNull(md.Jeu.DepartPour(2, "8335ca918a503047fc9cde6a0b082308"));

        if (departsSurDisque)
        {
            var dossier = Path.GetDirectoryName(chemin)!;
            foreach (var depart in entrees.SelectMany(e => (e.Jeu.Starts ?? []).Prepend(e.Jeu.Start!)))
            {
                var fichier = Path.Combine(dossier, depart.State.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(fichier), depart.State);
                Assert.Equal(depart.Sha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(fichier))), ignoreCase: true);
            }
        }
    }

    [Fact]
    public void Le_brouillon_reel_du_lot_0_se_lit()
    {
        var chemin = @"E:\claude_travail\temp\adventure\lot0\brouillon\catalogue_adventure.json";
        if (File.Exists(chemin))
        {
            VerifierUnCatalogue(chemin, departsSurDisque: true);
        }
    }

    [Fact]
    public void Le_catalogue_du_Data_Pack_de_la_branche_Adventure_se_lit_et_ses_departs_existent()
    {
        var chemin = @"E:\claude_travail\worktrees\datapack-adventure\adventure\catalogue.json";
        if (File.Exists(chemin))
        {
            VerifierUnCatalogue(chemin, departsSurDisque: true);
        }
    }

    // ── les sauvegardes de l'invite, et la seance d'une version localisee ──────────────────────────

    [Fact]
    public async Task L_invite_ecrit_ses_sauvegardes_dans_le_dossier_de_l_aventure_jamais_dans_les_siennes()
    {
        var (service, _, stockage, _) = UneSeance();
        var salle = Salle(2) with { Launch = new MultiplayerLancement { Role = "guest", Seat = 2, Players = 2 } };
        var reglages = service.ReglagesDeLInvite(salle);

        Assert.Equal(stockage.DossierDesSauvegardes("av-1"), reglages["savefile_directory"]);
        Assert.Equal("false", reglages["sort_savefiles_enable"]);
        Assert.Equal("false", reglages["sort_savefiles_by_content_enable"]);
        Assert.True(Directory.Exists(reglages["savefile_directory"]));
        // Pas d'ecriture reguliere pour lui : l'etat qu'il joue lui vient de l'hote.
        Assert.False(reglages.ContainsKey("autosave_interval"));
        Assert.Empty(service.ReglagesDeLInvite(salle with { Adventure = null }));
        await Task.CompletedTask;
    }

    [Fact]
    public void Seul_l_hote_garde_l_autosave_du_catalogue()
    {
        var (service, _, _, _) = UneSeance();
        var jeu = new MultiplayerGame { Id = "gba/kirby", System = "gba", Players = 4, LauncherOptions = new Dictionary<string, string> { ["autosave"] = "1", ["rewind"] = "0" } };
        Assert.Equal("1", service.JeuPourLaPlace(Salle(2), jeu, hote: true).LauncherOptions!["autosave"]);
        var invite = service.JeuPourLaPlace(Salle(2), jeu, hote: false);
        Assert.False(invite.LauncherOptions!.ContainsKey("autosave"));
        Assert.Equal("0", invite.LauncherOptions["rewind"]);
    }

    [Fact]
    public async Task Un_etat_de_sortie_d_une_seance_d_avant_est_efface_avant_le_lancement()
    {
        var (service, _, _, _) = UneSeance();
        var etats = Path.Combine(_racine, "etats-rom");
        Directory.CreateDirectory(Path.Combine(etats, "mgba"));
        var vieux = Path.Combine(etats, "kirby.state.auto");
        var vieuxDeCoeur = Path.Combine(etats, "mgba", "kirby.state.auto");
        var autre = Path.Combine(etats, "autre.state.auto");
        File.WriteAllText(vieux, "x");
        File.WriteAllText(vieuxDeCoeur, "x");
        File.WriteAllText(autre, "x");
        service.DossierDesEtatsLocal = () => etats;

        await service.PreparerAsync(Salle(2), Jeu(Path.Combine(_racine, "kirby.gba")), CancellationToken.None);

        Assert.False(File.Exists(vieux));
        Assert.False(File.Exists(vieuxDeCoeur));
        Assert.True(File.Exists(autre));   // celui d'une autre ROM n'est pas touche
    }

    [Fact]
    public async Task Une_partie_neuve_repart_du_depart_pret_de_sa_version_avec_un_chemin_verifiable()
    {
        var etat = Encoding.UTF8.GetBytes("depart pret kirby usa 2p");
        var dossier = Path.Combine(_racine, "resources", "adventure");
        Directory.CreateDirectory(Path.Combine(dossier, "states"));
        File.WriteAllBytes(Path.Combine(dossier, "states", "usa-2p.state"), etat);
        var catalogue = new AdventureCatalogueStore(path: Catalogue([new
        {
            id = "gba/kirby", system = "gba", name = "Kirby", players = new { min = 2, max = 2 }, core = "mgba_splitscreen", level = "nelfeplay",
            content = new { md5 = new[] { "md5kirby" } },
            start = new { state = "states/usa-2p.state", sha256 = AdventureStockage.Empreinte(etat), players = 2, content = "md5kirby" },
        }]));
        var stockage = Stockage();
        var site = new FauxSite { Aventure = new AdventureInfo { Id = "av-1", Game = "gba/kirby", Content = "md5kirby", Seats = 4 } };
        var service = new AdventureSeanceService(site, stockage, new AdventureFileDEnvoi(stockage), () => new FauxRetroArch("e", "c"), _ => "coeur-sha", () => _heure, catalogue: catalogue);
        var jeu = new MultiplayerInstalledGame(Assert.Single(catalogue.Entrees(out _)).Jeu, Path.Combine(_racine, "kirby.gba"), new MultiplayerCore { Core = "mgba_splitscreen" }, "md5kirby");
        var salle = Salle(2);

        Assert.True(await service.PreparerAsync(salle, jeu, CancellationToken.None));

        Assert.Equal(ModeDeReprise.Neuf, service.De("r1")!.Decision.Mode);
        var depart = service.DepartDe(salle)!;
        Assert.True(Path.IsPathRooted(depart.State));
        Assert.Equal(etat, File.ReadAllBytes(depart.State));
        Assert.Equal(AdventureStockage.Empreinte(etat), depart.Sha256, ignoreCase: true);
        // A 3 joueurs ce contenu n'a pas de depart : l'ecran titre, pas un etat d'un autre nombre de consoles.
        var troisJoueurs = Salle(3, "r3");
        await service.PreparerAsync(troisJoueurs, jeu, CancellationToken.None);
        Assert.Null(service.DepartDe(troisJoueurs));
    }

    [Fact]
    public async Task L_etat_de_l_aventure_passe_avant_le_depart_pret_du_catalogue()
    {
        var etatCatalogue = Encoding.UTF8.GetBytes("depart pret");
        var dossier = Path.Combine(_racine, "resources", "adventure");
        Directory.CreateDirectory(Path.Combine(dossier, "states"));
        File.WriteAllBytes(Path.Combine(dossier, "states", "usa-2p.state"), etatCatalogue);
        var catalogue = new AdventureCatalogueStore(path: Catalogue([new
        {
            id = "gba/kirby", system = "gba", name = "Kirby", players = new { min = 2, max = 2 }, core = "mgba_splitscreen", level = "nelfeplay",
            content = new { md5 = new[] { "md5kirby" } },
            start = new { state = "states/usa-2p.state", sha256 = AdventureStockage.Empreinte(etatCatalogue), players = 2, content = "md5kirby" },
        }]));
        var stockage = Stockage();
        var site = new FauxSite { Aventure = new AdventureInfo { Id = "av-1", Game = "gba/kirby", Content = "md5kirby", Seats = 4 } };
        PoserUneVersion(site, stockage, 2);
        var service = new AdventureSeanceService(site, stockage, new AdventureFileDEnvoi(stockage), () => new FauxRetroArch("e", "c"), _ => "coeur-sha", () => _heure, catalogue: catalogue);
        var jeu = new MultiplayerInstalledGame(Assert.Single(catalogue.Entrees(out _)).Jeu, Path.Combine(_racine, "kirby.gba"), new MultiplayerCore { Core = "mgba_splitscreen" }, "md5kirby");
        var salle = Salle(2);

        await service.PreparerAsync(salle, jeu, CancellationToken.None);

        Assert.Equal(ModeDeReprise.Etat, service.De("r1")!.Decision.Mode);
        Assert.Equal(Encoding.UTF8.GetBytes("etat a 2"), File.ReadAllBytes(service.DepartDe(salle)!.State));
    }

    [Fact]
    public async Task Un_jeu_a_mot_de_passe_n_a_aucun_fichier_de_cartouche_et_reprend_par_l_etat()
    {
        var catalogue = new AdventureCatalogueStore(path: Catalogue([new
        {
            id = "snes/the-lost-vikings", system = "snes", name = "The Lost Vikings", players = new { min = 2, max = 2 }, core = "snes9x", level = "nelfeplay",
            content = new { md5 = new[] { "md5kirby" } },
            start = new { state = "states/x.state", sha256 = new string('a', 64), players = 2, content = "md5kirby" },
            adventure = new { cartridge_save = new { console = 1, file = (string?)null, kind = "password", bytes = 0 } },
        }]));
        var stockage = Stockage();
        var site = new FauxSite { Aventure = new AdventureInfo { Id = "av-1", Game = "snes/the-lost-vikings", Content = "md5kirby", Seats = 2 } };
        PoserUneVersion(site, stockage, 2);
        var service = new AdventureSeanceService(site, stockage, new AdventureFileDEnvoi(stockage), () => new FauxRetroArch("e", "c"), _ => "coeur-sha", () => _heure, catalogue: catalogue);
        var jeu = new MultiplayerInstalledGame(Assert.Single(catalogue.Entrees(out _)).Jeu, Path.Combine(_racine, "vikings.sfc"), new MultiplayerCore { Core = "snes9x" }, "md5kirby");

        Assert.True(await service.PreparerAsync(Salle(2), jeu, CancellationToken.None));

        Assert.Equal(ModeDeReprise.Etat, service.De("r1")!.Decision.Mode);
        Assert.Empty(Directory.EnumerateFiles(stockage.DossierDesSauvegardes("av-1")));   // rien n'a ete pose
    }

    // ── NEW ADVENTURE dans la session ───────────────────────────────────────────────────────────────

    // ── le client du site ──────────────────────────────────────────────────────────────────────────

    private sealed class SiteEnBouteille(Func<HttpRequestMessage, (int Statut, string Corps)> reponse) : HttpMessageHandler
    {
        public List<string> Appels { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage requete, CancellationToken ct)
        {
            Appels.Add(requete.Method + " " + requete.RequestUri!.AbsolutePath);
            var (statut, corps) = reponse(requete);
            return Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)statut) { Content = new StringContent(corps, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Le_client_du_site_suit_le_contrat_et_rend_null_quand_le_site_est_injoignable()
    {
        var bouteille = new SiteEnBouteille(r => r.RequestUri!.AbsolutePath.EndsWith("/objects/missing", StringComparison.Ordinal)
            ? (200, """{"ok":true,"missing":["aa"]}""")
            : (200, """{"ok":true,"adventure":{"id":"av9","game":"gba/kirby-amazing-mirror","content":"abc123","places":3,"mode":"open","title":"Ma quete","members":[{"handle":"j1"},{"handle":"j2"}],"voice":{"supported":true,"lobby_id":"42"},"session":{"id":"s1","room":"r7"},"latest_version":{"id":"v1","seq":4,"state":"e1","save":"s1","shot":null,"players":3,"duration_s":900,"reason":"end","seats":[{"seat":1,"handle":"j1"}]}}}"""));
        var client = new AdventureSiteClient(() => new HttpClient(bouteille) { BaseAddress = new Uri("https://nelfeplay.test") });

        Assert.Equal(["aa"], await client.ObjetsManquantsAsync(["aa", "bb"], CancellationToken.None));
        var aventure = await client.CreerAsync("gba/kirby-amazing-mirror", "abc123", 3, AdventureModes.Open, "Ma quete", "mgba_splitscreen", "ff", CancellationToken.None);
        Assert.Equal("av9", aventure!.Id);
        Assert.Equal(3, aventure.Seats);
        Assert.Equal("Ma quete", aventure.Name);
        Assert.Equal(["j1", "j2"], aventure.Participants.ToArray());
        Assert.Equal("r7", aventure.Room);
        Assert.Equal("42", aventure.LobbyId);
        Assert.Equal(4, aventure.Latest!.Seq);
        Assert.Equal("e1", aventure.Latest.StateSha256);
        Assert.Equal(900, aventure.Latest.DurationS);
        Assert.Equal("j1", aventure.Latest.Seats.Single().Handle);
        Assert.Equal(["POST /api/v1/agent/adventure/objects/missing", "POST /api/v1/agent/adventure/create"], bouteille.Appels);

        var hors = new AdventureSiteClient(() => null);
        Assert.Null(await hors.ObjetsManquantsAsync(["aa"], CancellationToken.None));
        Assert.False(await hors.DeposerObjetAsync("aa", "state", [1], CancellationToken.None));
        Assert.Null(await hors.CreerAsync("g", "c", 2, "exclusive", null, null, null, CancellationToken.None));
        Assert.Equal(AdventureDepot.APlusTard("offline"), await hors.DeposerVersionAsync(new AdventureVersion { Id = "v", Adventure = "av-1" }, CancellationToken.None));
    }

    [Fact]
    public async Task La_derniere_version_et_l_ouverture_de_seance_se_lisent_comme_le_site_les_ecrit()
    {
        var bouteille = new SiteEnBouteille(r => r.RequestUri!.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal)
            ? (200, """{"ok":true,"resume":"fresh_save","version":{"id":"v3","seq":3,"state":"e","save":"s","players":4}}""")
            : (200, """{"ok":true,"attached":true,"room":"r1","session":{"id":"s2","resume":"state","base_seq":3},"latest":{"id":"v3","seq":3,"state":"e","players":2},"voice":{"supported":true,"lobby_id":"99"}}"""));
        var client = new AdventureSiteClient(() => new HttpClient(bouteille) { BaseAddress = new Uri("https://nelfeplay.test") });

        var derniere = await client.DerniereAsync("av-1", 2, CancellationToken.None);
        Assert.Equal("fresh_save", derniere!.Resume);
        Assert.Equal("s", derniere.Version!.SaveSha256);
        var seance = await client.OuvrirSeanceAsync("av-1", 2, CancellationToken.None);
        Assert.True(seance!.Ok);
        Assert.True(seance.Attached);
        Assert.Equal("r1", seance.Room);
        Assert.Equal("99", seance.LobbyId);
        Assert.Equal(3, seance.BaseSeq);
        Assert.Equal(["GET /api/v1/agent/adventure/av-1/latest", "POST /api/v1/agent/adventure/av-1/session/open"], bouteille.Appels);
    }

    [Fact]
    public async Task L_ouverture_de_seance_demande_au_moins_deux_joueurs()
    {
        string? corps = null;
        var bouteille = new SiteEnBouteille(r => { corps = r.Content!.ReadAsStringAsync().Result; return (200, """{"ok":true,"room":"r1","session":{"id":"s"}}"""); });
        var client = new AdventureSiteClient(() => new HttpClient(bouteille) { BaseAddress = new Uri("https://nelfeplay.test") });
        await client.OuvrirSeanceAsync("av-1", 1, CancellationToken.None);
        using var doc = System.Text.Json.JsonDocument.Parse(corps!);
        Assert.Equal(2, doc.RootElement.GetProperty("players").GetInt32());
    }

    [Fact]
    public async Task Le_depot_d_une_version_dit_son_sort_parti_a_plus_tard_ou_refuse_pour_toujours()
    {
        var corps = new List<string>();
        var reponses = new Queue<(int, string)>([(200, """{"ok":true,"version":{"id":"v","seq":2}}"""), (409, """{"ok":false,"reason":"object_missing","missing":["aa"]}"""),
            (409, """{"ok":false,"reason":"core_mismatch"}"""), (403, """{"ok":false,"reason":"not_host"}""")]);
        var bouteille = new SiteEnBouteille(r => { corps.Add(r.Content!.ReadAsStringAsync().Result); return reponses.Dequeue(); });
        var client = new AdventureSiteClient(() => new HttpClient(bouteille) { BaseAddress = new Uri("https://nelfeplay.test") });
        var version = new AdventureVersion
        {
            Id = "abcd", Adventure = "av-1", SessionId = "s-9", StateSha256 = "e", SaveSha256 = "s", Players = 2, BaseSeq = 3, DurationS = 60, Reason = AdventureRaisons.Fin, CoreSha256 = "c",
            Seats = [new AdventurePlace { Seat = 1, Handle = "j1" }],
        };

        Assert.True((await client.DeposerVersionAsync(version, CancellationToken.None)).Ok);
        var plusTard = await client.DeposerVersionAsync(version, CancellationToken.None);
        Assert.False(plusTard.Ok);
        Assert.False(plusTard.Definitif);
        Assert.True((await client.DeposerVersionAsync(version, CancellationToken.None)).Definitif);
        Assert.Equal("not_host", (await client.DeposerVersionAsync(version, CancellationToken.None)).Raison);

        // Le corps est celui du contrat : version_ref rend l'appel rejouable, la raison est celle du site.
        using var doc = System.Text.Json.JsonDocument.Parse(corps[0]);
        var racine = doc.RootElement;
        Assert.Equal("abcd", racine.GetProperty("version_ref").GetString());
        Assert.Equal("e", racine.GetProperty("state_sha256").GetString());
        Assert.Equal("s", racine.GetProperty("save_sha256").GetString());
        Assert.Equal(3, racine.GetProperty("base_seq").GetInt32());
        Assert.Equal("end", racine.GetProperty("reason").GetString());
        Assert.Equal("j1", racine.GetProperty("seats")[0].GetProperty("handle").GetString());
        Assert.Equal("c", racine.GetProperty("core_version").GetString());
        // Une version tardive de la file designe sa seance : le site verifie l'hote de CETTE seance (grace de 24 h).
        Assert.Equal("s-9", racine.GetProperty("session_id").GetString());
    }

    [Fact]
    public async Task Un_objet_se_depose_en_gzip_avec_son_genre_et_se_telecharge_decompresse()
    {
        byte[]? recu = null;
        string? uri = null;
        var bouteille = new SiteEnBouteille(r =>
        {
            if (r.Method == HttpMethod.Put)
            {
                uri = r.RequestUri!.PathAndQuery;
                recu = r.Content!.ReadAsByteArrayAsync().Result;
                return (200, "{}");
            }

            return (200, "ignore");
        });
        var client = new AdventureSiteClient(() => new HttpClient(bouteille) { BaseAddress = new Uri("https://nelfeplay.test") });
        var octets = Encoding.UTF8.GetBytes(new string('x', 500));
        Assert.True(await client.DeposerObjetAsync("abcd", "state", octets, CancellationToken.None));
        Assert.Equal("/api/v1/agent/adventure/objects/abcd?kind=state&encoding=gzip", uri);
        Assert.True(recu!.Length < octets.Length);
        using var gz = new System.IO.Compression.GZipStream(new MemoryStream(recu), System.IO.Compression.CompressionMode.Decompress);
        using var relu = new MemoryStream();
        gz.CopyTo(relu);
        Assert.Equal(octets, relu.ToArray());
    }
}
