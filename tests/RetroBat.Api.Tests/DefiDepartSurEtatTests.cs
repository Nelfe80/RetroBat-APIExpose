using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Multiplayer;
using RetroBat.Api.Speedrun;
using RetroBat.Domain.Events;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE DEPART SUR ETAT (demande du user du 01/11) : Green Hill 1 part pendant le panneau du niveau, Metal Slug 3 au parachute.
/// L'etat porte l'empreinte du reglement, se pose et se charge comme le depart de MULTIPLAYER, et son chargement, nomme au
/// passeport, n'est pas une sauvegarde.
/// </summary>
public class DefiDepartSurEtatTests
{
    private static readonly string Modele = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Reglements", "metal-slug-3.mission-1-knife.json"));

    private static readonly byte[] Octets = [1, 2, 3, 4, 5];

    private static string Sha(byte[] octets) => Convert.ToHexString(SHA256.HashData(octets)).ToLowerInvariant();

    private static Reglement SansEtat()
    {
        var doc = JsonNode.Parse(Modele)!.AsObject();
        doc["start_policy"] = "normal_boot";
        doc.Remove("state");
        return Reglement.Lire(doc.ToJsonString());
    }

    private static Reglement AvecEtat(string fichier, string? sha = null)
    {
        var doc = JsonNode.Parse(Modele)!.AsObject();
        doc["start_policy"] = "state";
        doc["state"] = new JsonObject { ["name"] = "mission-1-fbneo", ["sha256"] = sha ?? Sha(Octets), ["file"] = fichier };
        return Reglement.Lire(doc.ToJsonString());
    }

    [Fact]
    public void Le_reglement_porte_son_etat_de_depart()
    {
        var r = AvecEtat("arcade/metal-slug-3/mission-1-fbneo.state");

        Assert.Equal("state", r.StartPolicy);
        Assert.Equal(new EtatDeDepart("mission-1-fbneo", Sha(Octets), "arcade/metal-slug-3/mission-1-fbneo.state"), r.Etat);
        Assert.Null(SansEtat().Etat);
    }

    [Theory]
    [InlineData("metal-slug-3.mission-1-knife.json", "arcade/metal-slug-3/mission-1-depart.state", "1cd550ac12226e043eefbc61b6c23ee37a6c6abdd2e0601440ec8f74186ed981")]
    [InlineData("sonic-the-hedgehog.green-hill-1.v2.json", "megadrive/sonic-the-hedgehog/green-hill-1-panneau.state", "d35a49cb3f95afd5bc8878399fb8592657422588290e89dd2641e83dc675e87a")]
    public void Les_deux_premiers_defis_partent_sur_l_etat_fabrique_le_1er_novembre(string fichier, string etat, string sha)
    {
        var r = Reglement.Lire(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Reglements", fichier)));

        Assert.Equal("canonical_state", r.StartPolicy);
        Assert.Equal(etat, r.Etat!.Fichier);
        Assert.Equal(sha, r.Etat.Sha256);
        Assert.NotNull(EtatsDesDefis.Chemin(r.Etat, @"E:acine"));
    }

    [Theory]
    [InlineData("../appsettings.json")]
    [InlineData("arcade/../../secret.state")]
    [InlineData(@"C:\Windows\x.state")]
    [InlineData("")]
    public void Un_chemin_qui_sort_de_la_racine_des_etats_est_refuse(string fichier)
        => Assert.Null(EtatsDesDefis.Chemin(new EtatDeDepart("x", Sha(Octets), fichier), @"E:\racine"));

    [Fact]
    public void L_etat_se_lit_seulement_s_il_porte_l_empreinte_du_reglement()
    {
        var racine = Directory.CreateTempSubdirectory("etats-defis").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(racine, "arcade"));
            File.WriteAllBytes(Path.Combine(racine, "arcade", "m1.state"), Octets);

            Assert.Equal(Octets, EtatsDesDefis.Lire(new EtatDeDepart("m1", Sha(Octets), "arcade/m1.state"), racine).Octets);
            Assert.Equal("state_mismatch", EtatsDesDefis.Lire(new EtatDeDepart("m1", Sha([9]), "arcade/m1.state"), racine).Refus);
            Assert.Equal("state_missing", EtatsDesDefis.Lire(new EtatDeDepart("m1", Sha(Octets), "arcade/absent.state"), racine).Refus);
        }
        finally
        {
            Directory.Delete(racine, true);
        }
    }

    private sealed class Commandes : ICommandesRetroArch
    {
        public readonly List<string> Envoyees = [];

        public volatile string Statut = "GET_STATUS PLAYING fbneo,mslug3,crc32=0";

        public Task<string?> DemanderAsync(string commande, CancellationToken ct, int delaiMs = 500)
            => Task.FromResult<string?>(commande switch
            {
                "GET_STATUS" => Statut,
                "GET_CONFIG_PARAM savestate_directory" => "GET_CONFIG_PARAM savestate_directory " + Dossier,
                _ when commande.StartsWith("READ_CORE_RAM ", StringComparison.Ordinal) => commande + " 00",
                _ => null,
            });

        public Task EnvoyerAsync(string commande, CancellationToken ct)
        {
            lock (Envoyees) Envoyees.Add(commande);
            if (commande == "PAUSE_TOGGLE") Statut = "GET_STATUS PLAYING fbneo,mslug3,crc32=0";
            return Task.CompletedTask;
        }

        public string[] Lues()
        {
            lock (Envoyees) return Envoyees.ToArray();
        }

        public string Dossier = "";
    }

    [Fact]
    public async Task L_etat_se_pose_au_nom_du_contenu_et_ne_se_charge_qu_a_la_demande()
    {
        var dossier = Directory.CreateTempSubdirectory("etats-retroarch").FullName;
        try
        {
            var commandes = new Commandes { Dossier = dossier };

            Assert.Null(await EtatsDesDefis.PoserAsync(commandes, Octets, "mslug3", TimeSpan.FromSeconds(5), CancellationToken.None));

            Assert.Equal(Octets, File.ReadAllBytes(Path.Combine(dossier, "mslug3.state96")));
            Assert.Empty(commandes.Envoyees);

            await EtatsDesDefis.ChargerAsync(commandes, CancellationToken.None);
            Assert.Equal(["LOAD_STATE_SLOT 96"], commandes.Envoyees);
        }
        finally
        {
            Directory.Delete(dossier, true);
        }
    }

    private sealed record Borne(SpeedrunSessionService S, Commandes RetroArch, List<string> Publies, string Dossier) : IDisposable
    {
        public void Dispose() => Directory.Delete(Dossier, true);
    }

    private static async Task<Borne> SessionSurEtat(Reglement reglement)
    {
        var bus = new SimpleEventBus();
        var publies = new List<string>();
        bus.Subscribe<EventEnvelope>(e => { if (e.Type.StartsWith("speedrun.etat", StringComparison.Ordinal)) lock (publies) publies.Add(e.Type); });
        var contexte = new ApiContext();
        var rom = Path.Combine(Path.GetTempPath(), "mslug3.zip");
        contexte.Ui.Running = new GameReference { SystemId = "arcade", GamePath = rom };
        var dossier = Directory.CreateTempSubdirectory("etats-retroarch").FullName;
        var retroArch = new Commandes { Dossier = dossier };
        var journal = Path.Combine(Path.GetTempPath(), "speedrun-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var service = new SpeedrunSessionService(bus, contexte, NullLogger<SpeedrunSessionService>.Instance, null, retroArch, journal);
        await service.StartAsync(default);
        service.AttendreLeLancement(reglement, rom);
        await bus.PublishAsync(new EventEnvelope { Type = "ui.game.started", Payload = new { GamePath = rom } });
        return new Borne(service, retroArch, publies, dossier);
    }

    private static async Task Attendre(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(30);
        Assert.True(condition());
    }

    [Fact]
    public async Task L_etat_pose_attend_la_page_de_preparation_pour_se_charger()
    {
        var reglement = AvecEtat("arcade/m1.state");
        using var borne = await SessionSurEtat(reglement);

        borne.S.PartirSurLEtat(reglement, Octets, "mslug3");
        await Attendre(() => borne.S.EtatPoseUtc is not null);

        Assert.Equal(Octets, File.ReadAllBytes(Path.Combine(borne.Dossier, "mslug3.state96")));
        await Task.Delay(300);
        Assert.Empty(borne.RetroArch.Lues());
        Assert.Null(borne.S.EtatChargeUtc);

        borne.S.ChargerLEtatDeDepart();
        await Attendre(() => borne.S.EtatChargeUtc is not null);

        Assert.Equal(["LOAD_STATE_SLOT 96"], borne.RetroArch.Lues());
        lock (borne.Publies) Assert.Equal(["speedrun.etat.attendu", "speedrun.etat.charge"], borne.Publies);
    }

    [Fact]
    public async Task La_pause_n_est_pas_levee_pendant_que_l_etat_de_depart_se_charge()
    {
        var reglement = AvecEtat("arcade/m1.state");
        using var borne = await SessionSurEtat(reglement);
        borne.S.PartirSurLEtat(reglement, Octets, "mslug3");
        await Attendre(() => borne.S.EtatPoseUtc is not null);

        borne.RetroArch.Statut = "GET_STATUS PAUSED fbneo,mslug3,crc32=0";
        await Task.Delay(600);
        Assert.Empty(borne.RetroArch.Lues());

        // L'etat charge, la session leve la pause comme pendant tout le defi.
        borne.S.ChargerLEtatDeDepart();
        await Attendre(() => borne.RetroArch.Lues().Contains("PAUSE_TOGGLE"));
        Assert.Equal("LOAD_STATE_SLOT 96", borne.RetroArch.Lues()[0]);
    }

    [Fact]
    public async Task Sans_demande_l_etat_pose_se_charge_seul()
    {
        var reglement = AvecEtat("arcade/m1.state");
        using var borne = await SessionSurEtat(reglement);
        borne.S.ChargementDeSecours = TimeSpan.FromMilliseconds(200);

        borne.S.PartirSurLEtat(reglement, Octets, "mslug3");
        await Attendre(() => borne.S.EtatChargeUtc is not null);

        Assert.Equal(["LOAD_STATE_SLOT 96"], borne.RetroArch.Lues());
    }

    [Fact]
    public async Task Une_relance_recharge_l_etat_au_lieu_d_un_RESET()
    {
        var reglement = AvecEtat("arcade/m1.state");
        using var borne = await SessionSurEtat(reglement);
        borne.S.PartirSurLEtat(reglement, Octets, "mslug3");
        await Attendre(() => borne.S.EtatPoseUtc is not null);
        borne.S.ChargerLEtatDeDepart();
        await Attendre(() => borne.S.EtatChargeUtc is not null);
        var avant = borne.S.Instantane()!;

        var reponse = await borne.S.CommanderAsync(new CommandeSpeedrun("x1", avant.SessionId, SessionSpeedrun.ActionRelancer));

        Assert.Equal("pending", reponse.Statut);
        // Le replay de la tentative s'arrete avant le chargement (essai du 01/11).
        Assert.Equal(["LOAD_STATE_SLOT 96", "HALT_REPLAY", "LOAD_STATE_SLOT 96"], borne.RetroArch.Lues());
        Assert.NotEqual(avant.AttemptId, borne.S.Instantane()?.AttemptId);
        lock (borne.Publies) Assert.Equal(["speedrun.etat.attendu", "speedrun.etat.charge", "speedrun.etat.attendu", "speedrun.etat.charge"], borne.Publies);
    }

    [Fact]
    public void Le_chargement_de_l_etat_de_depart_n_est_pas_une_sauvegarde_mais_un_second_l_est()
    {
        const string un = """{"save_state_loads":1,"cheats":0}""";
        const string deux = """{"save_state_loads":2,"cheats":0}""";

        Assert.Equal("save_state_loads", PasseportDeDefi.Salissure(un));
        Assert.Null(PasseportDeDefi.Salissure(un, chargementsPermis: 1));
        Assert.Equal("save_state_loads", PasseportDeDefi.Salissure(deux, chargementsPermis: 1));
    }

    [Fact]
    public void Le_passeport_nomme_l_etat_de_depart_comme_celui_de_MULTIPLAYER()
    {
        var depart = PasseportDeDefi.Depart(AvecEtat("arcade/m1.state"))!;

        Assert.Equal(Sha(Octets), (string?)depart["etat"]);
        Assert.Equal(1, (int?)depart["chargements_permis"]);
        Assert.Empty(depart["choix"]!.AsArray());
        Assert.Null(PasseportDeDefi.Depart(SansEtat()));
    }
}
