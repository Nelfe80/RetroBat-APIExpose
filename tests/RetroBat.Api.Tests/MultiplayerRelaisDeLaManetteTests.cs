using RetroBat.Api.Multiplayer;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LA MANETTE PASSEE AU JOUEUR COURANT (CDC Classements v1.0 §8.3, Worms) : ce que le journal de RetroArch dit de la borne,
/// en anglais et en francais, et quand la borne bascule entre joueur et spectateur (NETPLAY_GAME_WATCH, banc du 30/10).
/// </summary>
public class MultiplayerRelaisDeLaManetteTests
{
    private static readonly MultiplayerTurnRelay Worms = new()
    {
        CoreRam = "0x1A2B", Values = new Dictionary<string, int> { ["0"] = 1, ["1"] = 2 }, Port = 1, SettleMs = 300,
    };

    [Theory]
    [InlineData("[INFO] [Netplay] You have joined as player 1", MultiplayerReponseDuNetplay.Rejoint)]
    [InlineData("[INFO] [Netplay] You have joined as player 1 (ping: 16 ms)", MultiplayerReponseDuNetplay.Rejoint)]
    [InlineData("[INFO] [Netplay] Vous avez rejoint le jeu en tant que joueur 1 (ping: 16 ms)", MultiplayerReponseDuNetplay.Rejoint)]
    [InlineData("[INFO] [Netplay] You have left the game", MultiplayerReponseDuNetplay.Quitte)]
    [InlineData("[INFO] [Netplay] Vous avez quitté le jeu", MultiplayerReponseDuNetplay.Quitte)]
    [InlineData("[INFO] [Netplay] The input devices requested are not available", MultiplayerReponseDuNetplay.Refuse)]
    [InlineData("[INFO] [Netplay] Les dispositifs d'entrée demandés ne sont pas disponibles", MultiplayerReponseDuNetplay.Refuse)]
    [InlineData("[INFO] [Netplay] invite has joined as player 1 (ping: 12 ms)", null)]
    [InlineData("[INFO] [Netplay] Player hote has left the game", null)]
    [InlineData("[INFO] [Core] You have left the game", null)]
    public void Le_journal_dit_ce_que_fait_cette_borne(string ligne, MultiplayerReponseDuNetplay? attendue)
        => Assert.Equal(attendue, MultiplayerRelaisDeLaManette.Lire(ligne));

    [Fact]
    public void L_hote_rend_la_manette_quand_le_tour_passe_a_l_invite()
    {
        var hote = new MultiplayerRelaisEnCours(Worms, 1);
        // Rien avant la premiere reponse du journal : l'etat de depart de la borne n'est pas encore connu.
        Assert.False(hote.Observer(1, 0));
        hote.Noter(MultiplayerReponseDuNetplay.Rejoint, 100);
        Assert.False(hote.Observer(0, 200));
        Assert.False(hote.Observer(0, 600));
        // Le tour passe a la place 2 : il faut qu'il tienne 300 ms.
        Assert.False(hote.Observer(1, 1000));
        Assert.False(hote.Observer(1, 1200));
        Assert.True(hote.Observer(1, 1300));
        // Une seule bascule a la fois, jusqu'a la reponse.
        Assert.False(hote.Observer(1, 1400));
        hote.Noter(MultiplayerReponseDuNetplay.Quitte, 1500);
        Assert.False(hote.Joue);
        Assert.False(hote.Observer(1, 1600));
        // Le tour revient : il la redemande.
        Assert.False(hote.Observer(0, 2000));
        Assert.True(hote.Observer(0, 2300));
    }

    [Fact]
    public void L_invite_redemande_tant_que_la_manette_est_tenue()
    {
        var invite = new MultiplayerRelaisEnCours(Worms, 2);
        // Au lancement, l'hote tient la manette : la demande de l'invite est refusee, il regarde.
        invite.Noter(MultiplayerReponseDuNetplay.Refuse, 0);
        Assert.False(invite.Observer(0, 100));
        Assert.False(invite.Observer(1, 1000));
        Assert.True(invite.Observer(1, 1300));
        // Le sortant ne l'a pas encore rendue : refus, nouvelle demande 500 ms plus tard.
        invite.Noter(MultiplayerReponseDuNetplay.Refuse, 1400);
        Assert.False(invite.Observer(1, 1600));
        Assert.True(invite.Observer(1, 1900));
        invite.Noter(MultiplayerReponseDuNetplay.Rejoint, 2000);
        Assert.True(invite.Joue);
        Assert.False(invite.Observer(1, 2500));
    }

    [Fact]
    public void Une_valeur_hors_de_la_table_ne_change_personne_de_main()
    {
        var hote = new MultiplayerRelaisEnCours(Worms, 1);
        hote.Noter(MultiplayerReponseDuNetplay.Rejoint, 0);
        Assert.False(hote.Observer(7, 100));
        Assert.False(hote.Observer(7, 5000));
        Assert.True(hote.Joue);
    }

    [Fact]
    public void Une_bascule_sans_reponse_arrete_le_relais()
    {
        // La commande bascule : sans reponse, la redemander pourrait rendre la manette a contretemps.
        var hote = new MultiplayerRelaisEnCours(Worms, 1);
        hote.Noter(MultiplayerReponseDuNetplay.Rejoint, 0);
        Assert.False(hote.Observer(1, 100));
        Assert.True(hote.Observer(1, 400));
        Assert.False(hote.Observer(1, 400 + MultiplayerRelaisEnCours.SansReponseMs));
        Assert.True(hote.HorsService);
        Assert.False(hote.Observer(0, 20000));
    }

    /// <summary>
    /// AU BANC (banc_relais.py api, plugins-70) : deux RetroArch en netplay, l'hote en 56510 et l'invite en 56511, leurs
    /// journaux dans NELFEPLAY_BANC_RELAIS. Les deux vraies boucles du relais tournent ici ; le test ecrit lui-meme l'octet du
    /// tour dans les deux jeux (une adresse que Worms laisse en paix), et le journal de chaque RetroArch dit qui tient la
    /// manette. Sans la variable, il passe sans rien faire.
    /// </summary>
    [Fact]
    public async Task Au_banc_la_manette_suit_l_octet_du_tour()
    {
        if (Environment.GetEnvironmentVariable("NELFEPLAY_BANC_RELAIS") is not { Length: > 0 } dossier)
        {
            return;
        }

        var relais = Worms with { CoreRam = "0x1FFFE" };
        var ecrivains = new[] { new CommandesRetroArchUdp(56510, 56533), new CommandesRetroArchUdp(56511, 56534) };
        using var fin = new CancellationTokenSource();
        var tour = 0;
        var ecriture = Task.Run(async () =>
        {
            // L'octet est reecrit sans cesse dans les deux jeux : un retour en arriere du netplay le remettrait sinon.
            while (!fin.IsCancellationRequested)
            {
                foreach (var ecrivain in ecrivains)
                {
                    await ecrivain.EnvoyerAsync($"WRITE_CORE_RAM 1fffe {tour:x2}", CancellationToken.None);
                }

                await Task.Delay(50);
            }
        });
        var trace = new JournalDuBanc(Path.Combine(dossier, "relais-api.log"));
        var hote = new MultiplayerRelaisDeLaManette(new CommandesRetroArchUdp(56510, 56531), trace).RelayerAsync(relais, 1, Path.Combine(dossier, "hote.log"), fin.Token);
        var invite = new MultiplayerRelaisDeLaManette(new CommandesRetroArchUdp(56511, 56532), trace).RelayerAsync(relais, 2, Path.Combine(dossier, "invite.log"), fin.Token);
        foreach (var suivant in new[] { 1, 0, 1, 0 })
        {
            await Task.Delay(5000);
            tour = suivant;
        }

        await Task.Delay(5000);
        fin.Cancel();
        await ecriture;
        var bascules = (await hote, await invite);

        // Quatre changements de main : l'hote rend et reprend deux fois, l'invite prend et rend deux fois.
        // RetroArch tient son journal ouvert en ecriture : il se lit en partage.
        string[] Lignes(string role)
        {
            using var flux = new FileStream(Path.Combine(dossier, role + ".log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var lecteur = new StreamReader(flux);
            return lecteur.ReadToEnd().Split('\n');
        }

        string[] Siennes(string role) => Lignes(role)
            .Where(ligne => MultiplayerRelaisDeLaManette.Lire(ligne) is MultiplayerReponseDuNetplay.Rejoint or MultiplayerReponseDuNetplay.Quitte)
            .Select(ligne => MultiplayerRelaisDeLaManette.Lire(ligne)!.Value.ToString()).ToArray();
        Assert.Equal(["Rejoint", "Quitte", "Rejoint", "Quitte", "Rejoint"], Siennes("hote"));
        Assert.Equal(["Rejoint", "Quitte", "Rejoint", "Quitte"], Siennes("invite"));
        // Une prise refusee (la manette pas encore rendue) se redemande : au moins quatre bascules de chaque cote.
        Assert.True(bascules.Item1 >= 4 && bascules.Item2 >= 4, bascules.ToString());
    }

    /// <summary>
    /// AU BANC SUR LE VRAI JEU (banc_relais.py worms) : le depart a deux du couloir 1, l'octet du tour de Worms (0x1F6A), et
    /// personne aux manettes : chaque tour finit au temps, la main passe d'elle-meme. NELFEPLAY_BANC_RELAIS_WORMS donne la duree.
    /// </summary>
    [Fact]
    public async Task Au_banc_la_manette_suit_le_vrai_tour_de_Worms()
    {
        if (Environment.GetEnvironmentVariable("NELFEPLAY_BANC_RELAIS") is not { Length: > 0 } dossier
            || !int.TryParse(Environment.GetEnvironmentVariable("NELFEPLAY_BANC_RELAIS_WORMS"), out var secondes))
        {
            return;
        }

        var relais = Worms with { CoreRam = "0x1F6A" };
        using var fin = new CancellationTokenSource(TimeSpan.FromSeconds(secondes));
        var trace = new JournalDuBanc(Path.Combine(dossier, "relais-worms.log"));
        var hote = new MultiplayerRelaisDeLaManette(new CommandesRetroArchUdp(56510, 56531), trace).RelayerAsync(relais, 1, Path.Combine(dossier, "hote.log"), fin.Token);
        var invite = new MultiplayerRelaisDeLaManette(new CommandesRetroArchUdp(56511, 56532), trace).RelayerAsync(relais, 2, Path.Combine(dossier, "invite.log"), fin.Token);
        var bascules = (await hote, await invite);
        // Au moins un tour passe : l'hote rend la manette, l'invite la prend.
        Assert.True(bascules.Item1 >= 1 && bascules.Item2 >= 1, bascules.ToString());
    }

    /// <summary>Le journal des deux boucles du banc, ligne a ligne, a l'instant de chaque ligne.</summary>
    private sealed class JournalDuBanc(string chemin) : Microsoft.Extensions.Logging.ILogger
    {
        private readonly object _verrou = new();
        private readonly System.Diagnostics.Stopwatch _horloge = System.Diagnostics.Stopwatch.StartNew();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_verrou)
            {
                File.AppendAllText(chemin, $"{_horloge.ElapsedMilliseconds,7} {formatter(state, exception)}{Environment.NewLine}");
            }
        }
    }

    [Fact]
    public void Le_jeu_a_tour_demande_la_seule_manette_et_se_valide()
    {
        var jeu = new MultiplayerGame { Id = "snes/worms", System = "snes", Players = 2, TurnRelay = Worms };
        Assert.Equal([1], jeu.PortsDeLaPlace(1));
        Assert.Equal([1], jeu.PortsDeLaPlace(2));
        Assert.Null(MultiplayerCatalogueStore.ValiderLeRelais(jeu));
        Assert.Equal(2, Worms.PlaceDuTour(1));
        Assert.Null(Worms.PlaceDuTour(5));

        const string Refus = "relais de la manette incoherent (adresse, valeurs vers places, port ; sans ordre, ports ni depart par place)";
        Assert.Equal(Refus, MultiplayerCatalogueStore.ValiderLeRelais(jeu with { TurnRelay = Worms with { Values = new Dictionary<string, int> { ["0"] = 3 } } }));
        Assert.Equal(Refus, MultiplayerCatalogueStore.ValiderLeRelais(jeu with { TurnRelay = Worms with { Values = new Dictionary<string, int> { ["x"] = 1 } } }));
        Assert.Equal(Refus, MultiplayerCatalogueStore.ValiderLeRelais(jeu with { TurnRelay = Worms with { Port = 0 } }));
        Assert.Equal(Refus, MultiplayerCatalogueStore.ValiderLeRelais(jeu with { SeatOrder = [2, 1] }));
    }
}
