using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NelfePlay.VocalRoom.Protocol;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Multiplayer;
using RetroBat.Api.VocalRoom;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE VOCAL DISCORD DES PARTIES MULTIPLAYER (lot B, 12/11/2026) : le processus (lancement, appairage, refus d'un exe
/// incompatible, relances bornees, arret), le cycle (lancement, ecran de fin, detach) et le contrat du site.
/// </summary>
public class VocalRoomTests
{
    private const string Room = "0123456789abcdef0123456789abcdef";
    private const string Lobby = "123456789012345678";

    private static MultiplayerRoom Salle(string etat, MultiplayerVoix? voix = null, string room = Room)
        => new() { Room = room, Game = "sf2", State = etat, Voice = voix };

    private sealed class Banc : IAsyncDisposable
    {
        public RacineDuVocal Racine { get; }
        public FauxVocalRoom Exe { get; } = new();
        public FauxSiteVocal Site { get; } = new();
        public ApiExposeOptions Options { get; } = new();
        public JournalCapture<VocalRoomCoordinator> Journal { get; } = new();
        public JournalCapture<VocalRoomSiteClient> JournalDuSite { get; } = new();
        public JournalCapture<VocalRoomProcessService> JournalDuProcessus { get; } = new();
        public VocalRoomProcessService Processus { get; }
        public VocalRoomCoordinator Coordinateur { get; }
        public List<MultiplayerRoom> Rooms { get; } = [];
        public DateTime Heure { get; set; } = new(2026, 11, 12, 22, 0, 0, DateTimeKind.Utc);

        public Banc(bool avecLeLot = true)
        {
            Racine = new RacineDuVocal(avecLeLot);
            Processus = new VocalRoomProcessService(Exe, Racine.Chemin, new OptionsDuVocal(Options), journal: JournalDuProcessus)
            {
                PauseAvantRelance = _ => TimeSpan.FromMilliseconds(10),
            };
            var site = new VocalRoomSiteClient(Site.Client, JournalDuSite);
            Coordinateur = new VocalRoomCoordinator(() => Rooms.ToList(), site, Processus, new OptionsDuVocal(Options), Journal)
            {
                Maintenant = () => Heure,
            };
        }

        public Task TourAsync() => Coordinateur.TourAsync(CancellationToken.None);

        public IEnumerable<string> CommandesRecues => Exe.Commandes.Select(c => c.Commande);

        public async ValueTask DisposeAsync()
        {
            await Processus.DisposeAsync();
            Racine.Dispose();
        }
    }

    // ── la verification de l'exe (pure) ───────────────────────────────────────────────────────────

    private static JsonObject Hello(int protocole = 1, string version = "0.2.0+x", bool jeton = true, string? sdk = "1.10.19337")
    {
        var caps = new JsonArray("session.attach", "session.detach", "voice.mute", "voice.deafen");
        if (jeton) caps.Add(JsonValue.Create("auth.apply_token"));
        return Messages.Ok("c-1", new JsonObject
        {
            ["protocol"] = protocole, ["app_version"] = version, ["sdk_version"] = sdk, ["capabilities"] = caps,
        });
    }

    [Fact]
    public void Un_exe_conforme_est_accepte()
        => Assert.Null(VocalRoomProcessService.Verifier(Hello(), preuveValide: true));

    [Fact]
    public void La_preuve_fausse_refuse_l_exe()
        => Assert.Equal("pairing_failed", VocalRoomProcessService.Verifier(Hello(), preuveValide: false));

    [Fact]
    public void Un_autre_protocole_est_incompatible()
    {
        Assert.Equal("protocol_incompatible", VocalRoomProcessService.Verifier(Hello(protocole: 2), true));
        Assert.Equal("protocol_incompatible", VocalRoomProcessService.Verifier(
            Messages.Error("c-1", ErrorCodes.UnsupportedVersion, "non"), true));
    }

    [Theory]
    [InlineData("0.1.0+20261112", true)]
    [InlineData("0.2.0", true)]
    [InlineData("pas une version", true)]
    public void Un_exe_trop_ancien_est_refuse(string version, bool sansJeton)
        => Assert.Equal("exe_too_old", VocalRoomProcessService.Verifier(Hello(version: version, jeton: !sansJeton), true));

    [Fact]
    public void Un_exe_sans_sdk_est_indisponible()
        => Assert.Equal("sdk_unavailable", VocalRoomProcessService.Verifier(Hello(sdk: null), true));

    [Theory]
    [InlineData("0.2.0+20261112.f3f0095", "0.2.0")]
    [InlineData("1.4", "1.4.0")]
    [InlineData(null, null)]
    public void La_version_de_l_exe_se_lit_sans_son_suffixe(string? texte, string? attendu)
        => Assert.Equal(attendu, VocalRoomProcessService.VersionDe(texte)?.ToString());

    // ── les fichiers du lot ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("NelfePlay-VocalRoom.exe", "NelfePlay-VocalRoom.exe")]
    [InlineData("nelfeplay-vocalroom.exe", "NelfePlay-VocalRoom.exe")]
    [InlineData("discord_partner_sdk.dll", "discord_partner_sdk.dll")]
    [InlineData("Discord-Social-SDK-License-Notices.txt", "Discord-Social-SDK-License-Notices.txt")]
    [InlineData("discord_krisp.dll", "discord_krisp.dll")]
    [InlineData("krisp-nc-o-med-v7.kef", "krisp-nc-o-med-v7.kef")]
    [InlineData("tools/vocalroom/NelfePlay-VocalRoom.exe", null)]
    [InlineData("..\\discord_partner_sdk.dll", null)]
    [InlineData("krisp-../x.kef", null)]
    [InlineData("autre.dll", null)]
    [InlineData("krisp-x.dll", null)]
    public void Seuls_les_fichiers_du_lot_a_la_racine_sont_du_vocal(string chemin, string? attendu)
        => Assert.Equal(attendu, VocalRoomFichiers.FichierDuVocal(chemin));

    [Fact]
    public void Outils_json_accepte_le_lot_vocal_et_rien_d_autre()
    {
        const string sha = "628f18c2e0e7aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string Outil(string fichier) => $$"""{"fichier":"{{fichier}}","version":"0.2.0","sha256":"{{sha}}","taille":10,"url":"https://github.com/Nelfe80/RetroBat-APIExpose/releases/download/v1/x"}""";
        var outils = SelfUpdateService.LireOutils(
            $$"""{"outils":[{{Outil("NelfePlay-VocalRoom.exe")}},{{Outil("discord_partner_sdk.dll")}},{{Outil("krisp-vad-o-v2.kef")}},{{Outil("tools/x/discord_partner_sdk.dll")}}]}""",
            "Nelfe80/RetroBat-APIExpose");
        Assert.Equal(["NelfePlay-VocalRoom.exe", "discord_partner_sdk.dll", "krisp-vad-o-v2.kef"], outils.Select(o => o.Fichier).ToArray());
    }

    [Theory]
    [InlineData(false, false, SelfUpdateService.DecisionVocal.Poser)]
    [InlineData(true, false, SelfUpdateService.DecisionVocal.Remettre)]
    [InlineData(false, true, SelfUpdateService.DecisionVocal.Remettre)]
    public void Le_lot_vocal_ne_se_remplace_pas_sous_une_session(bool exeOuvert, bool sessionVocale, SelfUpdateService.DecisionVocal attendu)
        => Assert.Equal(attendu, SelfUpdateService.DecisionDuVocal(exeOuvert, sessionVocale));

    // ── le processus ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sans_le_lot_rien_ne_se_lance_et_le_vocal_est_indisponible()
    {
        await using var b = new Banc(avecLeLot: false);
        Assert.False(await b.Processus.DemarrerAsync(CancellationToken.None));
        Assert.Equal(0, b.Exe.Lancements);
        Assert.Equal(VocalRoomEtat.Indisponible, b.Processus.Etat.Processus);
        Assert.Equal("exe_missing", b.Processus.Etat.Raison);
    }

    [Fact]
    public async Task Lancement_appairage_health_puis_arret_propre()
    {
        await using var b = new Banc();
        Assert.True(await b.Processus.DemarrerAsync(CancellationToken.None), b.Processus.Etat.Raison + " | " + string.Join(" / ", b.JournalDuProcessus.Lignes) + " | " + b.Exe.Dernier?.Erreur + " | " + string.Join(",", b.CommandesRecues));
        Assert.True(b.Processus.Tourne);
        Assert.Equal("0.2.0+test", b.Processus.Etat.VersionExe);
        Assert.Equal("AUTH_REQUIRED", b.Processus.Etat.Voix);
        Assert.Equal(["hello", "health"], b.CommandesRecues.ToArray());

        // Deja lance : pas un second exe.
        Assert.True(await b.Processus.DemarrerAsync(CancellationToken.None));
        Assert.Equal(1, b.Exe.Lancements);

        await b.Processus.ArreterAsync("test");
        Assert.Contains("shutdown", b.CommandesRecues);
        Assert.Equal(0, b.Exe.Dernier!.CodeDeSortie);
        Assert.False(b.Processus.Tourne);
        Assert.Equal(VocalRoomEtat.Arret, b.Processus.Etat.Processus);
    }

    [Fact]
    public async Task Un_exe_qui_ne_prouve_pas_le_secret_est_refuse_et_arrete()
    {
        await using var b = new Banc();
        b.Exe.PreuveFausse = true;
        Assert.False(await b.Processus.DemarrerAsync(CancellationToken.None));
        Assert.Equal("pairing_failed", b.Processus.Etat.Raison);
        Assert.True(b.Exe.Dernier!.Sortie.IsCompleted);
        Assert.Null(await b.Processus.CommanderAsync(Commands.Status));
    }

    [Fact]
    public async Task Un_protocole_incompatible_est_refuse_sans_rien_bloquer()
    {
        await using var b = new Banc();
        b.Exe.Protocole = 2;
        var debut = DateTime.UtcNow;
        Assert.False(await b.Processus.DemarrerAsync(CancellationToken.None));
        Assert.True(DateTime.UtcNow - debut < TimeSpan.FromSeconds(4));
        Assert.Equal("protocol_incompatible", b.Processus.Etat.Raison);
    }

    [Fact]
    public async Task Un_exe_sans_auth_apply_token_est_trop_ancien()
    {
        await using var b = new Banc();
        b.Exe.SansJeton = true;
        Assert.False(await b.Processus.DemarrerAsync(CancellationToken.None));
        Assert.Equal("exe_too_old", b.Processus.Etat.Raison);
    }

    [Fact]
    public async Task Voice_state_remonte_a_l_etat()
    {
        await using var b = new Banc();
        var vus = new List<string>();
        b.Processus.Change += e => { lock (vus) vus.Add(e.Voix); };
        await b.Processus.DemarrerAsync(CancellationToken.None);
        await b.Exe.Dernier!.EmettreAsync("IN_CALL", participants: 2);
        await Attendre.QueAsync(() => b.Processus.Etat.Voix == "IN_CALL");
        Assert.True(b.Processus.Etat.Connecte);
        Assert.Equal(2, b.Processus.Etat.Participants);
        lock (vus) Assert.Contains("IN_CALL", vus);
    }

    [Fact]
    public async Task Apres_un_plantage_l_exe_est_relance_un_nombre_limite_de_fois()
    {
        await using var b = new Banc();
        b.Options.NelfePlay.Voice.MaxRestarts = 2;
        var relances = 0;
        b.Processus.Relance += () => Interlocked.Increment(ref relances);
        Assert.True(await b.Processus.DemarrerAsync(CancellationToken.None));

        b.Exe.Dernier!.Planter();
        await Attendre.QueAsync(() => b.Exe.Lancements == 2 && b.Processus.Tourne);
        b.Exe.Dernier!.Planter();
        await Attendre.QueAsync(() => b.Exe.Lancements == 3 && b.Processus.Tourne);
        b.Exe.Dernier!.Planter();
        await Attendre.QueAsync(() => b.Processus.Etat.Processus == VocalRoomEtat.Indisponible);

        Assert.Equal(3, b.Exe.Lancements);
        Assert.Equal(2, relances);
        Assert.Equal("crashed", b.Processus.Etat.Raison);
    }

    [Fact]
    public async Task Un_arret_voulu_n_est_jamais_relance()
    {
        await using var b = new Banc();
        await b.Processus.DemarrerAsync(CancellationToken.None);
        await b.Processus.ArreterAsync("test");
        await Task.Delay(100);
        Assert.Equal(1, b.Exe.Lancements);
    }

    // ── le site ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Le_profil_dit_si_le_vocal_est_permis()
    {
        var site = new FauxSiteVocal();
        var client = new VocalRoomSiteClient(site.Client);
        Assert.True((await client.ProfilAsync(CancellationToken.None)).Permis);

        site.Profil["voice_preference"] = "off";
        Assert.False((await client.ProfilAsync(CancellationToken.None)).Permis);
        site.Profil["voice_preference"] = "ask";
        site.Profil["voice_consent"] = false;
        Assert.False((await client.ProfilAsync(CancellationToken.None)).Permis);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "voice_unsupported")]
    [InlineData(HttpStatusCode.Unauthorized, "unauthorized")]
    public async Task Un_site_sans_vocal_ou_une_borne_non_liee_se_taisent(HttpStatusCode statut, string raison)
    {
        var site = new FauxSiteVocal { Statut = statut };
        var client = new VocalRoomSiteClient(site.Client);
        var p = await client.ProfilAsync(CancellationToken.None);
        Assert.False(p.Permis);
        Assert.Equal(raison, p.Reason);
    }

    [Fact]
    public void Le_jeton_n_apparait_jamais_dans_son_texte()
    {
        var j = new VocalJeton { Ok = true, AccessToken = "jeton-secret-de-test-123" };
        Assert.DoesNotContain("jeton-secret", j.ToString());
    }

    [Theory]
    [InlineData("open", 2, false, false)]
    [InlineData("open", 1, false, true)]
    [InlineData("closing", 2, false, true)]
    [InlineData("open", 2, true, true)]
    public void Les_stats_disent_quand_detacher(string lobby, int restants, bool detach, bool attendu)
        => Assert.Equal(attendu, new VocalStats { Ok = true, Lobby = lobby, Remaining = restants, Detach = detach }.ADetacher);

    // ── le cycle ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task En_attente_ou_en_navigation_rien_ne_se_lance()
    {
        await using var b = new Banc();
        b.Rooms.Add(Salle("waiting"));
        await b.TourAsync();
        b.Rooms[0] = Salle("countdown");
        await b.TourAsync();
        Assert.Equal(0, b.Exe.Lancements);
        Assert.Empty(b.Site.Appels);
        Assert.Null(b.Coordinateur.Affichage);
    }

    [Fact]
    public async Task Partie_lancee_jeton_puis_salon_et_le_micro_part_coupe()
    {
        await using var b = new Banc();
        b.Rooms.Add(Salle("launching"));
        await b.TourAsync();

        Assert.Equal(1, b.Exe.Lancements);
        Assert.Equal(["hello", "health", "auth.apply_token", "session.attach", "voice.mute"], b.CommandesRecues.ToArray());
        var jeton = b.Exe.Commandes.First(c => c.Commande == "auth.apply_token").Charge;
        Assert.Equal("jeton-secret-de-test-123", Messages.Str(jeton, "access_token"));
        var attache = b.Exe.Commandes.First(c => c.Commande == "session.attach").Charge;
        Assert.Equal(Room, Messages.Str(attache, "room_id"));
        Assert.Equal(1, Messages.Long(attache, "generation"));
        Assert.Equal(Lobby, Messages.Str(attache, "lobby_id"));
        Assert.True(Messages.Bool(attache, "auto_join"));
        Assert.True(Messages.Bool(attache, "mic_muted"));
        var muet = b.Exe.Commandes.First(c => c.Commande == "voice.mute").Charge;
        Assert.True(Messages.Bool(muet, "muted"));
        Assert.Equal(1, Messages.Long(muet, "generation"));
        Assert.Contains(b.Coordinateur.Affichage!.Etat, new[] { EtatDeLaVoix.Connexion, EtatDeLaVoix.Pret });

        await b.Exe.Dernier!.EmettreAsync("IN_CALL", 2);
        await Attendre.QueAsync(() => b.Coordinateur.Affichage?.Etat == EtatDeLaVoix.Connecte);
        Assert.Equal(2, b.Coordinateur.Affichage!.Participants);

        // Le jeton n'est passe que par l'IPC : ni le journal du coordinateur, ni celui du site, ni celui du processus.
        Assert.DoesNotContain(b.Journal.Lignes.Concat(b.JournalDuSite.Lignes).Concat(b.JournalDuProcessus.Lignes),
            l => l.Contains("jeton-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task L_etat_vocal_du_sondage_evite_la_route_session()
    {
        await using var b = new Banc();
        b.Rooms.Add(Salle("playing", new MultiplayerVoix { Generation = 1, Status = "ready", LobbyId = Lobby, Join = "auto", MicMuted = true }));
        await b.TourAsync();
        Assert.Equal(0, b.Site.Nombre("session"));
        Assert.Equal(1, b.Site.Nombre("token"));
        Assert.Contains("session.attach", b.CommandesRecues);
    }

    [Fact]
    public async Task Sans_liaison_ni_consentement_rien_et_pas_de_relance()
    {
        await using var b = new Banc();
        b.Site.Profil["discord_linked"] = false;
        b.Rooms.Add(Salle("playing"));
        await b.TourAsync();
        await b.TourAsync();
        Assert.Equal(0, b.Exe.Lancements);
        Assert.Equal(1, b.Site.Nombre("profile"));
        Assert.Equal(0, b.Site.Nombre("session"));
        Assert.Null(b.Coordinateur.Affichage);
    }

    [Fact]
    public async Task Vocal_coupe_sur_la_borne_rien_ne_part()
    {
        await using var b = new Banc();
        b.Options.NelfePlay.Voice.Enabled = false;
        b.Rooms.Add(Salle("playing"));
        await b.TourAsync();
        Assert.Empty(b.Site.Appels);
        Assert.Equal(0, b.Exe.Lancements);
    }

    [Fact]
    public async Task Vocal_propose_rejoint_seulement_sur_Y()
    {
        await using var b = new Banc();
        b.Site.Session = room => FauxSiteVocal.Voix(room, 1, "ready", "offer");
        b.Rooms.Add(Salle("playing"));
        await b.TourAsync();
        Assert.Equal(0, b.Exe.Lancements);
        Assert.Equal(EtatDeLaVoix.Propose, b.Coordinateur.Affichage!.Etat);

        Assert.True(await b.Coordinateur.GesteAsync(maintenu: false));
        Assert.Equal(1, b.Exe.Lancements);
        Assert.Contains("session.attach", b.CommandesRecues);
    }

    [Fact]
    public async Task Y_coupe_le_micro_et_Y_maintenu_le_son()
    {
        await using var b = new Banc();
        b.Rooms.Add(Salle("playing"));
        await b.TourAsync();
        Assert.True(b.Coordinateur.MicroCoupe);

        await b.Coordinateur.GesteAsync(maintenu: false);
        Assert.False(b.Coordinateur.MicroCoupe);
        Assert.False(Messages.Bool(b.Exe.Commandes.Last(c => c.Commande == "voice.mute").Charge, "muted"));

        await b.Coordinateur.GesteAsync(maintenu: true);
        Assert.True(b.Coordinateur.CasqueCoupe);
        Assert.True(Messages.Bool(b.Exe.Commandes.Last(c => c.Commande == "voice.deafen").Charge, "deafened"));
    }

    [Fact]
    public async Task L_ecran_de_fin_garde_le_vocal_puis_le_detache_a_la_sortie()
    {
        await using var b = new Banc();
        b.Rooms.Add(Salle("playing"));
        await b.TourAsync();
        b.Rooms[0] = Salle("finished");
        await b.TourAsync();
        Assert.True(b.Processus.Tourne);   // finished ne ferme pas le vocal

        await b.Coordinateur.EcranDeFinMontreAsync(Room);
        var shown = b.Site.Appels.Single(a => a.Route == "stats").Corps!;
        Assert.Equal("shown", Messages.Str(shown, "status"));
        Assert.Equal(1, Messages.Long(shown, "generation"));
        Assert.True(b.Processus.Tourne);

        // Un battement toutes les 15 s tant que l'ecran est la.
        b.Heure = b.Heure.AddSeconds(16);
        await b.TourAsync();
        Assert.Contains(b.Site.Appels, a => a.Route == "stats" && Messages.Str(a.Corps, "status") == "heartbeat");

        await b.Coordinateur.EcranDeFinFermeAsync(Room, "ok");
        var closed = b.Site.Appels.Last(a => a.Route == "stats").Corps!;
        Assert.Equal("closed", Messages.Str(closed, "status"));
        Assert.Equal("ok", Messages.Str(closed, "reason"));
        Assert.Contains("session.detach", b.CommandesRecues);
        Assert.Contains("shutdown", b.CommandesRecues);
        Assert.False(b.Processus.Tourne);
        Assert.Null(b.Coordinateur.Affichage);

        // La room finie reste dans le sondage : rien ne se rattache.
        await b.TourAsync();
        Assert.Equal(1, b.Exe.Lancements);
    }

    [Fact]
    public async Task Quand_le_site_dit_detacher_a_l_ecran_de_fin_le_vocal_s_arrete()
    {
        await using var b = new Banc();
        b.Site.Stats = _ => new JsonObject { ["ok"] = true, ["lobby"] = "closing", ["remaining"] = 1, ["detach"] = true };
        b.Rooms.Add(Salle("playing"));
        await b.TourAsync();
        b.Rooms[0] = Salle("finished");
        await b.Coordinateur.EcranDeFinMontreAsync(Room);
        Assert.Contains("session.detach", b.CommandesRecues);
        Assert.False(b.Processus.Tourne);
    }

    [Fact]
    public async Task Une_room_finie_sans_ecran_de_fin_ne_garde_pas_le_vocal()
    {
        await using var b = new Banc();
        b.Rooms.Add(Salle("playing"));
        await b.TourAsync();
        b.Rooms[0] = Salle("finished");
        await b.TourAsync();
        b.Heure = b.Heure + VocalRoomCoordinator.FinSansEcran + TimeSpan.FromSeconds(1);
        await b.TourAsync();
        Assert.Contains("session.detach", b.CommandesRecues);
        Assert.False(b.Processus.Tourne);
    }

    [Fact]
    public async Task Une_room_qui_disparait_quitte_le_vocal()
    {
        await using var b = new Banc();
        b.Rooms.Add(Salle("playing"));
        await b.TourAsync();
        b.Rooms.Clear();
        await b.TourAsync();
        Assert.Contains("session.detach", b.CommandesRecues);
        var leave = b.Site.Appels.Single(a => a.Route == "leave").Corps!;
        Assert.Equal("user", Messages.Str(leave, "reason"));
    }

    [Fact]
    public async Task Une_nouvelle_generation_ne_reprend_jamais_l_ancien_salon()
    {
        await using var b = new Banc();
        b.Rooms.Add(Salle("playing"));
        await b.TourAsync();
        b.Site.Session = room => FauxSiteVocal.Voix(room, 2, "ready", "auto");
        b.Heure = b.Heure.AddSeconds(3);
        await b.TourAsync();

        var detache = b.Exe.Commandes.Single(c => c.Commande == "session.detach").Charge;
        Assert.Equal(1, Messages.Long(detache, "generation"));
        var attaches = b.Exe.Commandes.Where(c => c.Commande == "session.attach").Select(c => Messages.Long(c.Charge, "generation")).ToArray();
        Assert.Equal([1L, 2L], attaches);
        Assert.Equal(2, b.Site.Nombre("token"));
    }

    [Fact]
    public async Task Le_site_ferme_le_lobby_on_detache()
    {
        await using var b = new Banc();
        b.Rooms.Add(Salle("playing"));
        await b.TourAsync();
        b.Site.Session = room => FauxSiteVocal.Voix(room, 1, "closing", "none");
        b.Heure = b.Heure.AddSeconds(3);
        await b.TourAsync();
        Assert.Contains("session.detach", b.CommandesRecues);
        Assert.False(b.Processus.Tourne);
    }

    [Fact]
    public async Task Jeton_refuse_par_le_site_vocal_indisponible_sans_exe_qui_reste()
    {
        await using var b = new Banc();
        b.Site.Jeton = new JsonObject { ["ok"] = false, ["reason"] = "revoked" };
        b.Rooms.Add(Salle("playing"));
        await b.TourAsync();
        Assert.DoesNotContain("session.attach", b.CommandesRecues);
        Assert.Equal(EtatDeLaVoix.Indisponible, b.Coordinateur.Affichage!.Etat);
        Assert.Equal("revoked", b.Coordinateur.Affichage.Raison);
        Assert.False(b.Processus.Tourne);
    }

    [Fact]
    public async Task Exe_absent_vocal_indisponible_la_partie_continue()
    {
        await using var b = new Banc(avecLeLot: false);
        b.Rooms.Add(Salle("playing"));
        await b.TourAsync();
        Assert.Equal(EtatDeLaVoix.Indisponible, b.Coordinateur.Affichage!.Etat);
        Assert.Equal("exe_missing", b.Coordinateur.Affichage.Raison);
        Assert.Equal(0, b.Site.Nombre("token"));
    }

    [Fact]
    public async Task Apres_un_plantage_l_exe_relance_retrouve_jeton_et_salon()
    {
        await using var b = new Banc();
        b.Rooms.Add(Salle("playing"));
        await b.TourAsync();
        b.Exe.Dernier!.Planter();
        await Attendre.QueAsync(() => b.Exe.Commandes.Count(c => c.Commande == "session.attach") == 2);
        Assert.Equal(2, b.Exe.Lancements);
        Assert.Equal(2, b.Site.Nombre("token"));
        Assert.All(b.Exe.Commandes.Where(c => c.Commande == "session.attach"), c => Assert.True(Messages.Bool(c.Charge, "mic_muted")));
    }

    [Fact]
    public async Task Un_exe_qui_plante_toujours_finit_indisponible_et_le_site_le_sait()
    {
        await using var b = new Banc();
        b.Options.NelfePlay.Voice.MaxRestarts = 1;
        b.Rooms.Add(Salle("playing"));
        await b.TourAsync();
        b.Exe.PlanterA = "session.attach";   // chaque relance replante en rattachant
        b.Exe.Dernier!.Planter();
        await Attendre.QueAsync(() => b.Processus.Etat.Processus == VocalRoomEtat.Indisponible);
        await b.TourAsync();
        Assert.Equal(EtatDeLaVoix.Indisponible, b.Coordinateur.Affichage!.Etat);
        Assert.Contains(b.Site.Appels, a => a.Route == "leave");
    }

    // ── le micro a l'arrivee (preference open_with_prior_opt_in de /account) ──────────────────────

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    public void Le_micro_au_salon_n_est_jamais_plus_ouvert_que_le_site(bool site, bool joueur, bool attendu)
        => Assert.Equal(attendu, VocalRoomCoordinator.MicroAuSalon(site, joueur));

    private static bool? MicroAttache(Banc b, long generation)
        => Messages.Bool(b.Exe.Commandes.Last(c => c.Commande == "session.attach" && Messages.Long(c.Charge, "generation") == generation).Charge, "mic_muted");

    private static JsonObject MuetAttache(Banc b, long generation)
        => b.Exe.Commandes.Last(c => c.Commande == "voice.mute" && Messages.Long(c.Charge, "generation") == generation).Charge;

    [Fact]
    public async Task Le_site_ouvre_le_micro_il_s_ouvre_a_l_arrivee_dans_le_salon()
    {
        await using var b = new Banc();
        b.Site.Session = room => FauxSiteVocal.Voix(room, 1, "ready", "auto", micro: false);
        b.Rooms.Add(Salle("launching"));
        await b.TourAsync();

        Assert.Equal(["hello", "health", "auth.apply_token", "session.attach", "voice.mute"], b.CommandesRecues.ToArray());
        Assert.False(MicroAttache(b, 1));
        var muet = MuetAttache(b, 1);
        Assert.False(Messages.Bool(muet, "muted"));
        Assert.Equal(Room, Messages.Str(muet, "room_id"));
        Assert.False(b.Coordinateur.MicroCoupe);
        Assert.Contains(b.Journal.Lignes, l => l.Contains("attachee (micro ouvert)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Le_site_ouvre_le_micro_par_le_sondage_aussi()
    {
        await using var b = new Banc();
        b.Rooms.Add(Salle("playing", new MultiplayerVoix { Generation = 1, Status = "ready", LobbyId = Lobby, Join = "auto", MicMuted = false }));
        await b.TourAsync();
        Assert.False(MicroAttache(b, 1));
        Assert.False(Messages.Bool(MuetAttache(b, 1), "muted"));
        Assert.False(b.Coordinateur.MicroCoupe);
    }

    [Fact]
    public async Task Le_joueur_coupe_avec_Y_le_salon_suivant_reste_coupe_malgre_le_site()
    {
        await using var b = new Banc();
        b.Site.Session = room => FauxSiteVocal.Voix(room, 1, "ready", "auto", micro: false);
        b.Rooms.Add(Salle("playing"));
        await b.TourAsync();
        Assert.False(b.Coordinateur.MicroCoupe);

        Assert.True(await b.Coordinateur.GesteAsync(maintenu: false));   // Y : le joueur coupe
        Assert.True(b.Coordinateur.MicroCoupe);

        // Nouvelle generation de la meme room (nouveau salon), le site dit toujours « ouvert » : le choix du joueur passe avant.
        b.Site.Session = room => FauxSiteVocal.Voix(room, 2, "ready", "auto", micro: false);
        b.Heure = b.Heure.AddSeconds(3);
        await b.TourAsync();
        Assert.True(MicroAttache(b, 2));
        Assert.True(Messages.Bool(MuetAttache(b, 2), "muted"));
        Assert.True(b.Coordinateur.MicroCoupe);
    }

    [Fact]
    public async Task Le_joueur_rouvre_avec_Y_le_salon_suivant_suit_le_site()
    {
        await using var b = new Banc();
        b.Site.Session = room => FauxSiteVocal.Voix(room, 1, "ready", "auto", micro: false);
        b.Rooms.Add(Salle("playing"));
        await b.TourAsync();
        await b.Coordinateur.GesteAsync(maintenu: false);   // coupe
        await b.Coordinateur.GesteAsync(maintenu: false);   // rouvre : son dernier choix n'est plus « coupe »
        Assert.False(b.Coordinateur.MicroCoupe);

        b.Site.Session = room => FauxSiteVocal.Voix(room, 2, "ready", "auto", micro: false);
        b.Heure = b.Heure.AddSeconds(3);
        await b.TourAsync();
        Assert.False(MicroAttache(b, 2));
        Assert.False(b.Coordinateur.MicroCoupe);
    }

    [Fact]
    public async Task Le_site_recoupe_le_micro_au_salon_suivant_meme_rouvert_par_le_joueur()
    {
        await using var b = new Banc();
        b.Rooms.Add(Salle("playing"));
        await b.TourAsync();
        Assert.True(MicroAttache(b, 1));
        await b.Coordinateur.ReglerAsync(microCoupe: false, casqueCoupe: null);   // le joueur ouvre pendant la generation 1
        Assert.False(b.Coordinateur.MicroCoupe);

        b.Site.Session = room => FauxSiteVocal.Voix(room, 2, "ready", "auto");   // le site dit coupe
        b.Heure = b.Heure.AddSeconds(3);
        await b.TourAsync();
        Assert.True(MicroAttache(b, 2));
        Assert.True(Messages.Bool(MuetAttache(b, 2), "muted"));
        Assert.True(b.Coordinateur.MicroCoupe);
    }

    [Fact]
    public async Task Le_mute_du_joueur_s_arrete_avec_sa_session()
    {
        const string autre = "fedcba9876543210fedcba9876543210";
        await using var b = new Banc();
        b.Site.Session = room => FauxSiteVocal.Voix(room, 1, "ready", "auto", micro: false);
        b.Rooms.Add(Salle("playing"));
        await b.TourAsync();
        await b.Coordinateur.GesteAsync(maintenu: false);   // coupe pendant cette session
        Assert.True(b.Coordinateur.MicroCoupe);

        b.Rooms.Clear();   // la room est partie : la session aussi
        await b.TourAsync();
        Assert.Null(b.Coordinateur.Session);

        b.Rooms.Add(Salle("playing", room: autre));   // nouvelle session, le site ouvre : le micro s'ouvre
        await b.TourAsync();
        var attache = b.Exe.Commandes.Last(c => c.Commande == "session.attach").Charge;
        Assert.Equal(autre, Messages.Str(attache, "room_id"));
        Assert.False(Messages.Bool(attache, "mic_muted"));
        Assert.False(Messages.Bool(b.Exe.Commandes.Last(c => c.Commande == "voice.mute").Charge, "muted"));
        Assert.False(b.Coordinateur.MicroCoupe);
    }

    [Fact]
    public async Task Apres_un_plantage_le_micro_repart_coupe_meme_si_le_site_l_ouvrait()
    {
        await using var b = new Banc();
        b.Site.Session = room => FauxSiteVocal.Voix(room, 1, "ready", "auto", micro: false);
        b.Rooms.Add(Salle("playing"));
        await b.TourAsync();
        Assert.False(b.Coordinateur.MicroCoupe);
        b.Exe.Dernier!.Planter();
        await Attendre.QueAsync(() => b.Exe.Commandes.Count(c => c.Commande == "voice.mute") == 2);
        Assert.True(Messages.Bool(b.Exe.Commandes.Last(c => c.Commande == "session.attach").Charge, "mic_muted"));
        Assert.True(Messages.Bool(b.Exe.Commandes.Last(c => c.Commande == "voice.mute").Charge, "muted"));
        Assert.True(b.Coordinateur.MicroCoupe);
    }

    // ── lot D : jeton des pending, identite verifiee ───────────────────────────────────────────────

    [Fact]
    public async Task Lobby_en_creation_le_compte_se_connecte_deja_puis_l_appel_part_a_ready()
    {
        await using var b = new Banc();
        b.Site.Session = room => FauxSiteVocal.Voix(room, 1, "pending", "auto");
        b.Rooms.Add(Salle("launching"));
        await b.TourAsync();
        Assert.Equal(1, b.Exe.Lancements);
        Assert.Equal(1, b.Site.Nombre("token"));
        Assert.Contains("auth.apply_token", b.CommandesRecues);
        Assert.DoesNotContain("session.attach", b.CommandesRecues);

        // Toujours pending : pas de second jeton.
        b.Heure = b.Heure.AddSeconds(3);
        await b.TourAsync();
        Assert.Equal(1, b.Site.Nombre("token"));

        b.Site.Session = room => FauxSiteVocal.Voix(room, 1, "ready", "auto");
        b.Heure = b.Heure.AddSeconds(3);
        await b.TourAsync();
        Assert.Contains("session.attach", b.CommandesRecues);
        Assert.Equal(1, b.Site.Nombre("token"));
        Assert.Equal(1, b.Exe.Lancements);
    }

    [Fact]
    public async Task Vocal_propose_en_creation_rien_ne_se_connecte_sans_le_joueur()
    {
        await using var b = new Banc();
        b.Site.Session = room => FauxSiteVocal.Voix(room, 1, "pending", "offer");
        b.Rooms.Add(Salle("playing"));
        await b.TourAsync();
        Assert.Equal(0, b.Exe.Lancements);
        Assert.Equal(0, b.Site.Nombre("token"));
    }

    [Fact]
    public async Task L_identite_verifiee_par_le_site_part_avec_le_jeton()
    {
        await using var b = new Banc();
        b.Rooms.Add(Salle("playing"));
        await b.TourAsync();
        var jeton = b.Exe.Commandes.First(c => c.Commande == "auth.apply_token").Charge;
        Assert.Equal("123456789012345678", Messages.Str(jeton, "expected_user_id"));
        Assert.Contains("session.attach", b.CommandesRecues);
    }

    [Fact]
    public async Task Un_autre_compte_Discord_le_vocal_est_refuse_et_le_site_le_sait()
    {
        await using var b = new Banc();
        b.Exe.UtilisateurDiscord = "999999999999999999";
        b.Rooms.Add(Salle("playing"));
        await b.TourAsync();
        await Attendre.QueAsync(() => b.Processus.Etat.DerniereErreur == "user_mismatch");
        b.Heure = b.Heure.AddSeconds(3);
        await b.TourAsync();
        Assert.Equal(EtatDeLaVoix.Indisponible, b.Coordinateur.Affichage!.Etat);
        Assert.Equal("user_mismatch", b.Coordinateur.Affichage.Raison);
        Assert.False(b.Processus.Tourne);
        Assert.Equal(1, b.Site.Nombre("token"));   // pas de second essai
        Assert.Contains(b.Site.Appels, a => a.Route == "leave");
    }

    [Fact]
    public async Task Sans_identite_du_site_le_jeton_part_sans_attente()
    {
        await using var b = new Banc();
        b.Site.Jeton.Remove("discord_user_id");
        b.Rooms.Add(Salle("playing"));
        await b.TourAsync();
        var jeton = b.Exe.Commandes.First(c => c.Commande == "auth.apply_token").Charge;
        Assert.Null(Messages.Str(jeton, "expected_user_id"));
    }

    [Fact]
    public void L_indicateur_suit_l_exe()
    {
        var exe = VocalRoomEtat.Arrete with { Processus = VocalRoomEtat.EnMarche };
        Assert.Equal(EtatDeLaVoix.Connexion, VocalRoomCoordinator.Afficher(VocalRoomCoordinator.Phase.Attachee, null, exe with { Voix = "CONNECTING" }, true, false)!.Etat);
        Assert.Equal(EtatDeLaVoix.Pret, VocalRoomCoordinator.Afficher(VocalRoomCoordinator.Phase.Attachee, null, exe with { Voix = "READY" }, true, false)!.Etat);
        Assert.Equal(EtatDeLaVoix.Connecte, VocalRoomCoordinator.Afficher(VocalRoomCoordinator.Phase.Attachee, null, exe with { Voix = "IN_CALL", Participants = 3 }, true, false)!.Etat);
        Assert.Equal(EtatDeLaVoix.Indisponible, VocalRoomCoordinator.Afficher(VocalRoomCoordinator.Phase.Attachee, null, exe with { Voix = "ERROR" }, true, false)!.Etat);
        Assert.Null(VocalRoomCoordinator.Afficher(VocalRoomCoordinator.Phase.Terminee, null, exe, true, false));
    }

    // ── la Rich Presence (squelette desactive) ────────────────────────────────────────────────────

    [Fact]
    public void La_presence_reste_a_RetroArch_tant_que_le_lot_H_n_est_pas_allume()
    {
        Assert.False(DiscordPresenceCoordinator.Active);
        Assert.Equal(ProprietaireDeLaPresence.RetroArchNatif,
            DiscordPresenceCoordinator.Decider(DiscordPresenceCoordinator.Active, true, true, true, true, true));
    }

    [Theory]
    [InlineData(true, true, true, true, true, ProprietaireDeLaPresence.NelfePlay)]
    [InlineData(true, true, true, false, true, ProprietaireDeLaPresence.RetroArchNatif)]   // RetroArch publie et on ne peut pas le couper
    [InlineData(true, false, true, true, true, ProprietaireDeLaPresence.RetroArchNatif)]   // hors MULTIPLAYER
    [InlineData(false, true, true, true, true, ProprietaireDeLaPresence.Personne)]         // le joueur ne partage pas
    [InlineData(true, true, false, true, false, ProprietaireDeLaPresence.Personne)]        // exe pas pret, RetroArch muet
    public void Une_fois_allumee_un_seul_editeur(bool partage, bool multiplayer, bool exe, bool coupable, bool? discordAllow,
        ProprietaireDeLaPresence attendu)
        => Assert.Equal(attendu, DiscordPresenceCoordinator.Decider(true, partage, multiplayer, exe, coupable, discordAllow));

    [Fact]
    public void L_activite_ne_porte_que_des_metadonnees_publiques()
    {
        var a = DiscordPresenceCoordinator.Composer("Street Fighter II", "Arcade", "Multiplayer", 2, 2, DateTimeOffset.FromUnixTimeSeconds(1794700000));
        Assert.Equal(["details", "state", "party_size", "party_max", "start"], a.Select(p => p.Key).ToArray());
        Assert.Equal("Multiplayer · Arcade", Messages.Str(a, "state"));
    }

    [Fact]
    public void Discord_allow_se_lit_sans_s_ecrire()
    {
        var cfg = Path.Combine(Path.GetTempPath(), "ra-" + Guid.NewGuid().ToString("N") + ".cfg");
        try
        {
            File.WriteAllText(cfg, "video_fullscreen = \"true\"\ndiscord_allow = \"true\"\n");
            var avant = File.ReadAllText(cfg);
            Assert.True(DiscordPresenceCoordinator.LireDiscordAllow(cfg));
            Assert.Equal(avant, File.ReadAllText(cfg));
            Assert.Null(DiscordPresenceCoordinator.LireDiscordAllow(cfg + ".absent"));
        }
        finally
        {
            File.Delete(cfg);
        }
    }

    // ── le vrai exe du lot A/B, s'il est construit sur cette machine ──────────────────────────────

    /// <summary>
    /// Le VRAI NelfePlay-VocalRoom.exe (Release publiee du depot VocalRoom), pilote par le vrai service : lancement sans
    /// fenetre, appairage, health, session.attach, auth.apply_token sans jeton refuse, shutdown. Sans compte Discord ni
    /// reseau. Passe en silence si l'exe n'est pas construit ici (NELFEPLAY_VOCALROOM_PUBLISH pour un autre dossier).
    /// </summary>
    [Fact]
    public async Task Le_vrai_exe_se_pilote_de_bout_en_bout()
    {
        var publication = Environment.GetEnvironmentVariable("NELFEPLAY_VOCALROOM_PUBLISH")
            ?? @"E:\claude_travail\worktrees\vocalroom-auth\src\NelfePlay.VocalRoom\bin\Release\net8.0-windows\win-x64\publish";
        if (!VocalRoomFichiers.LotComplet(publication))
        {
            return;
        }

        using var racine = new RacineDuVocal(avecLeLot: false);
        foreach (var f in Directory.GetFiles(publication))
        {
            File.Copy(f, Path.Combine(racine.Chemin, Path.GetFileName(f)));
        }

        await using var service = new VocalRoomProcessService(new VocalRoomLanceur(), racine.Chemin);
        try
        {
            Assert.True(await service.DemarrerAsync(CancellationToken.None), service.Etat.Raison);
            Assert.True(VocalRoomProcessService.VersionDe(service.Etat.VersionExe) >= VocalRoomProtocolSource.VersionMinimale, service.Etat.VersionExe);
            Assert.Equal("1.10.19337", service.Etat.VersionSdk);
            Assert.Equal("AUTH_REQUIRED", service.Etat.Voix);

            var attache = await service.CommanderAsync(Commands.SessionAttach, new JsonObject
            {
                ["room_id"] = Room, ["generation"] = 1, ["lobby_id"] = Lobby, ["auto_join"] = true, ["mic_muted"] = true,
            });
            Assert.True(Messages.Bool(attache, "ok"));
            // Le micro redit au salon attache, comme le coordinateur le fait (room et generation de la session courante).
            var muet = await service.CommanderAsync(Commands.VoiceMute, new JsonObject { ["muted"] = false, ["room_id"] = Room, ["generation"] = 1 });
            Assert.True(Messages.Bool(muet, "ok"));
            Assert.False(Messages.Bool(Messages.Obj(muet, "result"), "mic_muted"));
            var sansJeton = await service.CommanderAsync(Commands.AuthApplyToken, new JsonObject());
            Assert.Equal(ErrorCodes.InvalidPayload, Messages.Str(Messages.Obj(sansJeton, "error"), "code"));
            var creer = await service.CommanderAsync("lobby.create", new JsonObject());
            Assert.Equal(ErrorCodes.Forbidden, Messages.Str(Messages.Obj(creer, "error"), "code"));
        }
        finally
        {
            await service.ArreterAsync("test");
        }

        Assert.Equal(VocalRoomEtat.Arret, service.Etat.Processus);
        Assert.Empty(System.Diagnostics.Process.GetProcessesByName(VocalRoomFichiers.Processus)
            .Where(p => { try { return p.MainModule?.FileName?.StartsWith(racine.Chemin, StringComparison.OrdinalIgnoreCase) == true; } catch { return false; } }));
    }
}
