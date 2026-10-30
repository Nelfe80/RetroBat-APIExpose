using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Multiplayer;
using RetroBat.Api.Speedrun;
using RetroBat.Domain.Events;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LA SESSION DE SPEEDRUN SUR LA BORNE (prototype Sonic, lot S3) : tentatives successives, etats publies au HUD,
/// relance, abandon, commandes idempotentes, puis le service branche sur un bus et un RetroArch d'essai. Les
/// frames sont celles de Green Hill 1 mesurees le 20/10 (timer a 909, main au joueur a 934, panneau a 3754, fin
/// d'acte a 3878).
/// </summary>
public class SpeedrunSessionTests
{
    private static readonly Reglement GreenHill1 = CatalogueDesDefis.Trouver("sonic-the-hedgehog.green-hill-1")
        ?? throw new InvalidOperationException("reglement de Green Hill 1 non embarque");

    private static Signal S(long frame, string action, string adresse, int valeur, long? horloge = null)
        => Signal.Depuis(frame, action, adresse, valeur, horloge ?? 1_000_000 + (long)(frame * 1000 / 59.922743));

    private static void Image(SessionSpeedrun s, params Signal[] signaux) => s.RecevoirImage(signaux);

    /// <summary>Ecran titre, conditions de depart relevees, carton du titre, timer, main au joueur.</summary>
    private static void Partir(SessionSpeedrun s, long decalage = 0)
    {
        Image(s, S(decalage + 500, "TITLE_SCREEN", "0x00F601", 0x04));
        foreach (var a in s.AdressesInconnues) s.Relever(a, 0);
        Image(s, S(decalage + 905, "GAME_PLAYING", "0x00F601", 0x8C));
        Image(s, S(decalage + 909, "GENERAL_TIMER", "0x00FE1F", 1));
        Image(s, S(decalage + 934, "GAME_PLAYING", "0x00F601", 0x0C));
    }

    private static void Finir(SessionSpeedrun s, long decalage = 0)
    {
        for (var sec = 1; sec <= 47; sec++) Image(s, S(decalage + 909 + 60 * sec, "LEVEL_TIMER", "0x00FE25", sec));
        Image(s, S(decalage + 3754, "GENERAL_TIMER", "0x00FE1F", 0));
        foreach (var a in s.AdressesInconnues) s.Relever(a, 0);
        Image(s, S(decalage + 3878, "LEVEL_CLEAR", "0x00F7CD", 1));
    }

    private static int _ids;

    private static SessionSpeedrun Nouvelle() => new(GreenHill1.ChallengeId, GreenHill1,
        () => "id" + Interlocked.Increment(ref _ids).ToString(CultureInfo.InvariantCulture));

    [Fact]
    public void Le_reglement_embarque_de_Green_Hill_1_ne_cite_que_des_lignes_du_MEM_certifie()
    {
        Assert.Equal(1, GreenHill1.Version);
        Assert.Equal(["genesis_plus_gx"], GreenHill1.Coeurs);
        if (MemCertifie() is not { } mem) return;
        Assert.Empty(GreenHill1.VerifierContre(File.ReadAllBytes(mem)));
    }

    [Fact]
    public void Une_tentative_passe_par_IDLE_ARMED_RUNNING_puis_FINISHED_et_publie_ses_evenements()
    {
        var s = Nouvelle();
        Assert.Equal(SessionSpeedrun.Idle, s.Etat);
        Image(s, S(500, "TITLE_SCREEN", "0x00F601", 0x04));
        foreach (var a in s.AdressesInconnues) s.Relever(a, 0);
        Assert.Equal(SessionSpeedrun.Armed, s.Etat);
        Image(s, S(905, "GAME_PLAYING", "0x00F601", 0x8C));
        var timer = S(909, "GENERAL_TIMER", "0x00FE1F", 1);
        Image(s, timer);
        Assert.Equal(SessionSpeedrun.Running, s.Etat);
        var depart = s.Instantane().Depart;
        Assert.Equal(909, depart?.Frame);
        Assert.Equal(timer.HorlogeMs, depart?.HorlogeMs);
        Assert.Equal(59.922743, depart?.Cadence);

        Image(s, S(934, "GAME_PLAYING", "0x00F601", 0x0C));
        Finir(s);
        Assert.Equal(SessionSpeedrun.Finished, s.Etat);
        var snap = s.Instantane();
        Assert.Equal(2845, snap.Officiel?.Frames);
        Assert.False(snap.Officiel?.Provisoire);
        Assert.Equal("0:47.48", snap.Officiel?.Affichage);
        Assert.Equal(2944, snap.Demonstration?.Independant?.Frames);
        Assert.Equal("ok", snap.Demonstration?.Controle);
        Assert.Equal(2845, s.MeilleurTemps);

        var types = s.PrendreLesEvenements().Select(e => e.Type).ToList();
        Assert.Equal(["speedrun.state", "speedrun.started", "speedrun.finish.provisional", "speedrun.attempt.ended"], types);
        Assert.Single(s.PrendreLesCloses());
    }

    [Fact]
    public void La_sequence_croit_a_chaque_evenement()
    {
        var s = Nouvelle();
        Partir(s);
        Finir(s);
        var sequences = s.PrendreLesEvenements().Select(e => e.Sequence).ToList();
        Assert.Equal(Enumerable.Range(1, sequences.Count).Select(i => (long)i), sequences);
        Assert.Equal(sequences[^1], s.Instantane().Sequence);
    }

    [Fact]
    public void A_l_arrivee_provisoire_le_HUD_recoit_un_temps_provisoire()
    {
        var s = Nouvelle();
        Partir(s);
        for (var sec = 1; sec <= 47; sec++) Image(s, S(909 + 60 * sec, "LEVEL_TIMER", "0x00FE25", sec));
        Image(s, S(3754, "GENERAL_TIMER", "0x00FE1F", 0));
        var snap = s.Instantane();
        Assert.Equal(SessionSpeedrun.Running, snap.Etat);
        Assert.Equal(2845, snap.Officiel?.Frames);
        Assert.True(snap.Officiel?.Provisoire);
    }

    [Fact]
    public void Une_tentative_close_reste_affichee_jusqu_au_depart_suivant()
    {
        var s = Nouvelle();
        Partir(s);
        var premiere = s.AttemptId;
        Image(s, S(2000, "LOSE_LIFE", "0x00FE13", 2));
        Assert.Equal(SessionSpeedrun.Invalid, s.Etat);
        Assert.Equal("LOSE_LIFE", s.Instantane().Cause);

        // La reprise de l'acte apres la mort : zone et acte deja connus, la tentative suivante attend en coulisse.
        Image(s, S(2300, "GAME_PLAYING", "0x00F601", 0x8C));
        Assert.Equal(SessionSpeedrun.Invalid, s.Etat);
        Assert.Equal(premiere, s.AttemptId);
        Assert.Contains(0xFE10L, s.AdressesInconnues);
        Assert.DoesNotContain(0xF601L, s.AdressesInconnues);
        foreach (var a in s.AdressesInconnues) s.Relever(a, 0);
        Image(s, S(2304, "GENERAL_TIMER", "0x00FE1F", 1));
        Assert.Equal(SessionSpeedrun.Running, s.Etat);
        Assert.NotEqual(premiere, s.AttemptId);
        Assert.Equal(2, s.Numero);
        Assert.Equal(2304, s.Instantane().Depart?.Frame);
    }

    [Fact]
    public void Relancer_pendant_un_run_demande_confirmation_puis_ouvre_une_tentative_neuve()
    {
        var s = Nouvelle();
        Partir(s);
        var avant = s.AttemptId;

        var sans = s.Commander(new CommandeSpeedrun("r1", s.SessionId, SessionSpeedrun.ActionRelancer), out var effet);
        Assert.Equal(("rejected", "confirm_required"), (sans.Statut, sans.Raison));
        Assert.Equal(EffetDemande.Aucun, effet);

        var avec = s.Commander(new CommandeSpeedrun("r2", s.SessionId, SessionSpeedrun.ActionRelancer, Confirme: true), out effet);
        Assert.Equal("pending", avec.Statut);
        Assert.Equal(SessionSpeedrun.Restarting, avec.Etat);
        Assert.Equal(EffetDemande.Relancer, effet);

        // La meme demande rend la meme reponse, sans rien refaire ; une autre trouve la relance en cours.
        Assert.Same(avec, s.Commander(new CommandeSpeedrun("r2", s.SessionId, SessionSpeedrun.ActionRelancer, Confirme: true), out effet));
        Assert.Equal(EffetDemande.Aucun, effet);
        var autre = s.Commander(new CommandeSpeedrun("r3", s.SessionId, SessionSpeedrun.ActionRelancer, Confirme: true), out effet);
        Assert.Equal(("rejected", "restart_in_progress"), (autre.Statut, autre.Raison));

        // Pendant la relance, le flux du jeu ne compte pour personne.
        Image(s, S(3000, "GENERAL_TIMER", "0x00FE1F", 1));
        Assert.Equal(SessionSpeedrun.Restarting, s.Etat);

        s.RelanceFaite();
        Assert.Equal(SessionSpeedrun.Idle, s.Etat);
        Assert.NotEqual(avant, s.AttemptId);
        var close = Assert.Single(s.PrendreLesCloses());
        Assert.Equal((avant, EtatTentative.Abandonnee, "restart"), (close.AttemptId, close.Resultat.Etat, close.Resultat.Cause));
        Assert.Contains(s.PrendreLesEvenements(), e => e.Type == "speedrun.attempt.opened");

        Partir(s, decalage: 10_000);
        Assert.Equal(SessionSpeedrun.Running, s.Etat);
    }

    [Fact]
    public void Relancer_avant_le_depart_est_immediat_et_n_ecrit_rien_au_journal()
    {
        var s = Nouvelle();
        Image(s, S(500, "TITLE_SCREEN", "0x00F601", 0x04));
        foreach (var a in s.AdressesInconnues) s.Relever(a, 0);
        var r = s.Commander(new CommandeSpeedrun("r1", s.SessionId, SessionSpeedrun.ActionRelancer), out var effet);
        Assert.Equal("pending", r.Statut);
        Assert.Equal(EffetDemande.Relancer, effet);
        s.RelanceFaite();
        Assert.Empty(s.PrendreLesCloses());
        Assert.DoesNotContain(s.PrendreLesEvenements(), e => e.Type == "speedrun.attempt.ended");
    }

    [Fact]
    public void Une_relance_que_RetroArch_ne_recoit_pas_est_dite()
    {
        var s = Nouvelle();
        s.Commander(new CommandeSpeedrun("r1", s.SessionId, SessionSpeedrun.ActionRelancer), out _);
        s.RelanceEchouee("retroarch_unreachable");
        Assert.Equal(SessionSpeedrun.Idle, s.Etat);
        Assert.Contains(s.PrendreLesEvenements(), e => e.Type == "speedrun.restart.failed");
    }

    [Fact]
    public void Abandonner_ne_vaut_que_pendant_un_run_et_avec_confirmation()
    {
        var s = Nouvelle();
        var avant = s.Commander(new CommandeSpeedrun("a1", s.SessionId, SessionSpeedrun.ActionAbandonner, Confirme: true), out _);
        Assert.Equal(("rejected", "no_attempt_running"), (avant.Statut, avant.Raison));

        Partir(s);
        var sans = s.Commander(new CommandeSpeedrun("a2", s.SessionId, SessionSpeedrun.ActionAbandonner), out _);
        Assert.Equal("confirm_required", sans.Raison);
        var ok = s.Commander(new CommandeSpeedrun("a3", s.SessionId, SessionSpeedrun.ActionAbandonner, Confirme: true), out var effet);
        Assert.Equal(("accepted", SessionSpeedrun.Aborted), (ok.Statut, ok.Etat));
        Assert.Equal(EffetDemande.Aucun, effet);
        Assert.Equal("abandoned", Assert.Single(s.PrendreLesCloses()).Resultat.Cause);
    }

    [Fact]
    public void Une_commande_d_une_session_perimee_ou_d_entrainement_est_refusee()
    {
        var s = Nouvelle();
        Assert.Equal("stale_session", s.Commander(new CommandeSpeedrun("c1", "ancienne", SessionSpeedrun.ActionRelancer), out _).Raison);
        Assert.Equal("ranked_challenge", s.Commander(new CommandeSpeedrun("c2", s.SessionId, "run.split"), out _).Raison);
        Assert.Equal("ranked_challenge", s.Commander(new CommandeSpeedrun("c3", s.SessionId, "run.pause"), out _).Raison);
        Assert.Equal("unknown_action", s.Commander(new CommandeSpeedrun("c4", s.SessionId, "run.teleport"), out _).Raison);
        Assert.All(s.ActionsPermises().Where(a => SessionSpeedrun.ActionsDEntrainement.Contains(a.Action)), a => Assert.False(a.Permise));
    }

    [Fact]
    public void Seul_le_meilleur_temps_du_joueur_se_compare_avant_S4()
    {
        var s = Nouvelle();
        Assert.Equal("accepted", s.Commander(new CommandeSpeedrun("k1", s.SessionId, SessionSpeedrun.ActionComparaison, Argument: "pb"), out _).Statut);
        Assert.Equal("not_available_yet", s.Commander(new CommandeSpeedrun("k2", s.SessionId, SessionSpeedrun.ActionComparaison, Argument: "record"), out _).Raison);
        Assert.Equal("unknown_comparison", s.Commander(new CommandeSpeedrun("k3", s.SessionId, SessionSpeedrun.ActionComparaison, Argument: "x"), out _).Raison);
    }

    [Fact]
    public void Le_snapshot_parle_le_contrat_du_HUD()
    {
        var s = Nouvelle();
        Partir(s);
        var json = JsonSerializer.Serialize(s.Instantane(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        foreach (var champ in new[] { "sessionId", "attemptId", "state", "sequence", "start", "official", "splits", "comparisons", "allowedActions" })
            Assert.True(r.TryGetProperty(champ, out _), champ);
        Assert.Equal("RUNNING", r.GetProperty("state").GetString());
        var relancer = r.GetProperty("allowedActions").EnumerateArray().First(a => a.GetProperty("action").GetString() == "run.restart");
        Assert.True(relancer.GetProperty("allowed").GetBoolean());
        Assert.True(relancer.GetProperty("confirm").GetBoolean());
    }

    [Theory]
    [InlineData("[21:15:30.250] [ADDR:0x00FE1F] [VAL:0x01] [UDP_OUT] ...", "2026-10-29T21:16:00+01:00", "2026-10-29T21:15:30.250+01:00")]
    [InlineData("[23:59:59.900] [ADDR:0x00FE1F] ...", "2026-10-30T00:00:00.100+01:00", "2026-10-29T23:59:59.900+01:00")]
    [InlineData(null, "2026-10-29T12:00:00+01:00", "2026-10-29T12:00:00+01:00")]
    public void L_horloge_d_un_evenement_est_celle_de_son_emission_par_le_wrapper(string? ligne, string maintenant, string attendu)
    {
        Assert.Equal(DateTimeOffset.Parse(attendu, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(),
            SpeedrunSessionService.HorlogeDeLaLigne(ligne, DateTimeOffset.Parse(maintenant, CultureInfo.InvariantCulture)));
    }

    /// <summary>Un RetroArch d'essai : READ_CORE_RAM rend la valeur donnee, 0 sinon ; RESET est note.</summary>
    private sealed class RetroArchDEssai(IReadOnlyDictionary<long, int>? valeurs = null) : ICommandesRetroArch
    {
        public List<string> Envoyees { get; } = new();

        public Task<string?> DemanderAsync(string commande, CancellationToken ct, int delaiMs = 500)
        {
            var m = commande.Split(' ');
            if (m[0] != "READ_CORE_RAM") return Task.FromResult<string?>(null);
            var valeur = valeurs?.GetValueOrDefault(long.Parse(m[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture)) ?? 0;
            return Task.FromResult<string?>($"READ_CORE_RAM {m[1]} {valeur:X2}");
        }

        public Task EnvoyerAsync(string commande, CancellationToken ct)
        {
            lock (Envoyees) Envoyees.Add(commande);
            return Task.CompletedTask;
        }
    }

    private sealed record Service(SpeedrunSessionService S, SimpleEventBus Bus, RetroArchDEssai RetroArch, List<EventEnvelope> Publies, string Journal);

    private static async Task<Service> Demarrer(IReadOnlyDictionary<long, int>? valeurs = null)
    {
        var bus = new SimpleEventBus();
        var publies = new List<EventEnvelope>();
        bus.Subscribe<EventEnvelope>(e => { if (e.Type.StartsWith("speedrun.", StringComparison.Ordinal)) lock (publies) publies.Add(e); });
        var contexte = new ApiContext();
        var rom = Path.Combine(Path.GetTempPath(), "Sonic The Hedgehog (USA, Europe).zip");
        contexte.Ui.Running = new GameReference { SystemId = "megadrive", GamePath = rom };
        var retroArch = new RetroArchDEssai(valeurs);
        var journal = Path.Combine(Path.GetTempPath(), "speedrun-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var service = new SpeedrunSessionService(bus, contexte, NullLogger<SpeedrunSessionService>.Instance, null, retroArch, journal);
        await service.StartAsync(default);
        service.AttendreLeLancement(GreenHill1, rom);
        await bus.PublishAsync(new EventEnvelope { Type = "ui.game.started", Payload = new { GamePath = rom } });
        return new Service(service, bus, retroArch, publies, journal);
    }

    private static Task Emettre(SimpleEventBus bus, long frame, string action, string adresse, int valeur, string heure)
        => bus.PublishAsync(new EventEnvelope
        {
            Type = "retroarch.memory.changed",
            Payload = new
            {
                SystemId = "megadrive",
                Rom = "sonic-the-hedgehog",
                signal = new RetroArchRuntimeSignal
                {
                    Name = action, Address = adresse, Value = valeur, Frame = frame,
                    RawLine = $"[{heure}] [ADDR:{adresse}] [VAL:0x{valeur:X2}] [UDP_OUT] ACTION:{action}",
                },
            },
        });

    private static async Task Attendre(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(30);
        Assert.True(condition());
    }

    private static string Heure(long frame) => TimeSpan.FromHours(10).Add(TimeSpan.FromMilliseconds(frame * 1000 / 59.922743)).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Le_service_ouvre_la_session_au_lancement_type_et_arme_par_READ_CORE_RAM()
    {
        var (service, bus, _, publies, journal) = await Demarrer();
        try
        {
            Assert.Equal(SessionSpeedrun.Idle, service.Instantane()?.Etat);
            await Attendre(() => service.Instantane()?.Etat == SessionSpeedrun.Armed);
            await Emettre(bus, 905, "GAME_PLAYING", "0x00F601", 0x8C, Heure(905));
            await Emettre(bus, 909, "GENERAL_TIMER", "0x00FE1F", 1, Heure(909));
            await Emettre(bus, 934, "GAME_PLAYING", "0x00F601", 0x0C, Heure(934));
            await Attendre(() => service.Instantane()?.Etat == SessionSpeedrun.Running);
            Assert.Equal(909, service.Instantane()?.Depart?.Frame);
            lock (publies) Assert.Contains(publies, e => e.Type == "speedrun.session");

            await bus.PublishAsync(new EventEnvelope { Type = "ui.game.ended", Payload = new { } });
            Assert.Null(service.Instantane());
            lock (publies) Assert.Contains(publies, e => e.Type == "speedrun.session.closed");
            var ligne = Assert.Single(File.ReadAllLines(journal));
            Assert.Contains("\"cause\":\"game_closed\"", ligne);
        }
        finally
        {
            service.Dispose();
            File.Delete(journal);
        }
    }

    [Fact]
    public async Task Les_evenements_d_une_meme_frame_sont_juges_ensemble()
    {
        // Les minutes valent 1 avant la reprise : le depart ne voit 0 que si l'image est jugee d'un bloc.
        var (service, bus, _, _, journal) = await Demarrer(new Dictionary<long, int> { [0xFE22] = 1 });
        try
        {
            await Attendre(() => service.Instantane()?.Etat == SessionSpeedrun.Armed);
            await Task.Delay(150);
            await Emettre(bus, 905, "GAME_PLAYING", "0x00F601", 0x8C, Heure(905));
            // Sonic remet ses minutes a 0 dans l'image ou son timer repart ; le wrapper emet le timer en premier.
            await Emettre(bus, 909, "GENERAL_TIMER", "0x00FE1F", 1, Heure(909));
            await Emettre(bus, 909, "LEVEL_TIMER", "0x00FE22", 0, Heure(909));
            await Emettre(bus, 934, "GAME_PLAYING", "0x00F601", 0x0C, Heure(934));
            await Attendre(() => service.Instantane()?.Etat == SessionSpeedrun.Running);
        }
        finally
        {
            service.Dispose();
            File.Delete(journal);
        }
    }

    [Fact]
    public async Task L_avance_rapide_annule_la_tentative()
    {
        var (service, bus, _, _, journal) = await Demarrer();
        try
        {
            await Attendre(() => service.Instantane()?.Etat == SessionSpeedrun.Armed);
            await Emettre(bus, 905, "GAME_PLAYING", "0x00F601", 0x8C, Heure(905));
            await Emettre(bus, 909, "GENERAL_TIMER", "0x00FE1F", 1, Heure(909));
            await Emettre(bus, 934, "GAME_PLAYING", "0x00F601", 0x0C, Heure(934));
            // Quatre fois plus vite que l'horloge : une seconde du jeu toutes les 250 ms.
            for (var sec = 1; sec <= 12; sec++)
            {
                var frame = 909 + 60L * sec;
                var heure = TimeSpan.FromHours(10).Add(TimeSpan.FromMilliseconds(934 * 1000 / 59.922743 + 250 * sec)).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
                await Emettre(bus, frame, "LEVEL_TIMER", "0x00FE25", sec, heure);
            }

            await Attendre(() => service.Instantane()?.Etat == SessionSpeedrun.Invalid);
            Assert.Equal("fast_forward", service.Instantane()?.Cause);
        }
        finally
        {
            service.Dispose();
            File.Delete(journal);
        }
    }

    [Fact]
    public async Task Une_relance_envoie_RESET_et_ouvre_une_tentative_neuve()
    {
        var (service, bus, retroArch, _, journal) = await Demarrer();
        try
        {
            var snap = service.Instantane()!;
            var r = await service.CommanderAsync(new CommandeSpeedrun("x1", snap.SessionId, SessionSpeedrun.ActionRelancer));
            Assert.Equal("pending", r.Statut);
            lock (retroArch.Envoyees) Assert.Equal(["RESET"], retroArch.Envoyees);
            Assert.Equal(SessionSpeedrun.Idle, service.Instantane()?.Etat);
            Assert.NotEqual(snap.AttemptId, service.Instantane()?.AttemptId);
        }
        finally
        {
            service.Dispose();
            File.Delete(journal);
        }
    }

    [Fact]
    public async Task Hotkey_A_avant_le_depart_relance_aussitot()
    {
        var (service, bus, retroArch, _, journal) = await Demarrer();
        try
        {
            await bus.PublishAsync(new EventEnvelope { Type = "panel.input.pressed", Payload = new { Player = 1, Identity = "select" } });
            await bus.PublishAsync(new EventEnvelope { Type = "panel.input.pressed", Payload = new { Player = 1, Identity = "a" } });
            await Attendre(() => { lock (retroArch.Envoyees) return retroArch.Envoyees.Contains("RESET"); });
        }
        finally
        {
            service.Dispose();
            File.Delete(journal);
        }
    }

    [Fact]
    public void Le_meilleur_temps_vient_du_journal_local()
    {
        var journal = Path.Combine(Path.GetTempPath(), "speedrun-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            File.WriteAllLines(journal,
            [
                """{"kind":"attempt","challengeId":"sonic-the-hedgehog.green-hill-1","state":"FINISHED","officialFrames":2900}""",
                """{"kind":"attempt","challengeId":"sonic-the-hedgehog.green-hill-1","state":"INVALID","officialFrames":null}""",
                """{"kind":"wrapper_summary","sessionId":"s"}""",
                """{"kind":"attempt","challengeId":"sonic-the-hedgehog.green-hill-1","state":"FINISHED","officialFrames":2845}""",
                """{"kind":"attempt","challengeId":"sonic-the-hedgehog.green-hill-2","state":"FINISHED","officialFrames":100}""",
            ]);
            var service = new SpeedrunSessionService(new SimpleEventBus(), new ApiContext(), NullLogger<SpeedrunSessionService>.Instance, null,
                new RetroArchDEssai(), journal);
            Assert.Equal(2845, service.MeilleurTempsDuJournal("sonic-the-hedgehog.green-hill-1"));
            Assert.Null(service.MeilleurTempsDuJournal("sonic-the-hedgehog.marble-1"));
        }
        finally
        {
            File.Delete(journal);
        }
    }

    private static string? MemCertifie()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var c = Path.Combine(d.FullName, "RetroBat-DataPack", "ram", "megadrive", "sonic-the-hedgehog.MEM");
            if (File.Exists(c)) return c;
        }

        return null;
    }
}
