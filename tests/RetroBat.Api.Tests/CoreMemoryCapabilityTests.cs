using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Les procès-verbaux ci-dessous sont RÉELS, relevés sur borne le 24 septembre 2026. Ils sont la
/// garantie que la borne juge un cœur exactement comme le wrapper se juge lui-même.
/// </summary>
public class CoreMemoryCapabilityTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "nelfe-coeurs-" + Guid.NewGuid().ToString("N"));

    private CoreMemoryCapability Neuf() => new(_dossier);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dossier)) Directory.Delete(_dossier, true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    [Theory]
    // FBNeo, cinq jeux mesurés : la capacité ne change pas, seule la taille varie.
    [InlineData("Core=fbneo_libretro arcade=YES system_ram=OK system_ram_size=94208 memory_map_blocks=0", true)]
    [InlineData("Core=fbneo_libretro arcade=YES system_ram=OK system_ram_size=6165 memory_map_blocks=0", true)]
    // Un cœur qui n'expose rien et n'annonce aucune carte : c'est le cas de figure qui a fait
    // perdre quatre parties.
    [InlineData("Core=mame2003_plus_libretro arcade=YES system_ram=NULL system_ram_size=0 memory_map_blocks=0", false)]
    // Pas de RAM système, mais une carte mémoire annoncée : le wrapper sait lire, nous aussi.
    [InlineData("Core=un_coeur arcade=YES system_ram=NULL system_ram_size=0 memory_map_blocks=7", true)]
    // Une carte annoncée hors arcade ne suffit pas : c'est la condition du wrapper, telle quelle.
    [InlineData("Core=un_coeur arcade=NO system_ram=NULL system_ram_size=0 memory_map_blocks=7", false)]
    // Une taille nulle malgré un pointeur : rien à lire.
    [InlineData("Core=un_coeur arcade=NO system_ram=OK system_ram_size=0 memory_map_blocks=0", false)]
    public void Le_verdict_suit_la_condition_du_wrapper(string proces, bool attendu)
    {
        var verdict = Neuf().Observer("[16:57:14.234] [DEBUG WRAPPER] " + proces);
        Assert.NotNull(verdict);
        Assert.Equal(attendu, verdict!.Measures);
    }

    [Fact]
    public void Une_ligne_qui_n_est_pas_un_proces_verbal_est_ignoree()
    {
        var liste = Neuf();
        Assert.Null(liste.Observer("[DEBUG WRAPPER] WATCH_RESOLVE Credits -> SYSTEM_RAM resolved=0x3094"));
        Assert.Null(liste.Observer("[ADDR:0x003094] [VAL:0x00] TYPE:STATE :DEMO"));
        Assert.Empty(liste.Tout());
    }

    [Fact]
    public void Un_coeur_qui_lit_ne_se_dejuge_pas_sur_un_jeu_sans_carte_memoire()
    {
        // La carte mémoire est annoncée PAR JEU : un titre qui n'en déclare pas ne prouve rien
        // contre un cœur qu'on a déjà vu lire. Sans cette garde, un seul jeu particulier suffirait
        // à faire déclarer muet un cœur parfaitement lisible.
        var liste = Neuf();
        liste.Observer("Core=c arcade=YES system_ram=NULL system_ram_size=0 memory_map_blocks=4");
        liste.Observer("Core=c arcade=YES system_ram=NULL system_ram_size=0 memory_map_blocks=0");
        Assert.True(liste.Connu("c")!.Measures);
    }

    [Fact]
    public void Un_coeur_cru_muet_qui_lit_enfin_remplace_l_ancien_verdict()
    {
        var liste = Neuf();
        liste.Observer("Core=c arcade=NO system_ram=NULL system_ram_size=0 memory_map_blocks=0");
        Assert.False(liste.Connu("c")!.Measures);
        liste.Observer("Core=c arcade=NO system_ram=OK system_ram_size=65536 memory_map_blocks=0");
        Assert.True(liste.Connu("c")!.Measures);
    }

    [Fact]
    public void Le_verdict_survit_au_redemarrage_de_la_borne()
    {
        Neuf().Observer("Core=mame2003_plus_libretro arcade=YES system_ram=NULL system_ram_size=0 memory_map_blocks=0");

        var apres = Neuf();
        var connu = apres.Connu("mame2003_plus_libretro");
        Assert.NotNull(connu);
        Assert.False(connu!.Measures);
        // La preuve se relit : un verdict sans sa ligne d'origine ne serait qu'une affirmation.
        Assert.Contains("system_ram=NULL", connu.Evidence);
    }

    [Fact]
    public void Un_coeur_jamais_vu_n_a_pas_de_verdict()
    {
        Assert.Null(Neuf().Connu("jamais_lance"));
    }

    [Fact]
    public void Le_nom_d_affichage_rejoint_le_fichier_par_la_fiche_de_RetroArch()
    {
        // Les deux bouts ne parlaient pas la meme langue : le wrapper nomme le FICHIER
        // (mame2003_plus_libretro), l'attestation nomme le coeur (MAME 2003-Plus). RetroArch pose
        // lui-meme la table de jointure a cote de chaque .dll, et son champ `corename` vaut mot
        // pour mot ce que l'attestation annonce -- verifie sur les trois coeurs vus en production.
        var info = Path.Combine(RetroBat.Domain.Paths.RetroBatPaths.RetroBatRoot,
            "emulators", "retroarch", "info", "mame2003_plus_libretro.info");
        if (!File.Exists(info))
        {
            return;   // pas de RetroArch installe ici : rien a verifier
        }

        var liste = Neuf();
        liste.Observer("Core=mame2003_plus_libretro arcade=YES system_ram=NULL system_ram_size=0 memory_map_blocks=0");

        var connu = liste.ConnuParNomAffiche("MAME 2003-Plus");
        Assert.NotNull(connu);
        Assert.False(connu!.Measures);
    }

    [Fact]
    public void Un_nom_d_affichage_inconnu_ne_conclut_rien()
    {
        Assert.Null(Neuf().ConnuParNomAffiche("Un Coeur Qui N Existe Pas"));
        Assert.Null(Neuf().ConnuParNomAffiche(""));
    }

    [Fact]
    public void Un_coeur_installe_mais_jamais_lance_ne_conclut_rien()
    {
        // La fiche existe, mais on ne l'a jamais vu tourner : on ne suppose pas.
        var info = Path.Combine(RetroBat.Domain.Paths.RetroBatPaths.RetroBatRoot,
            "emulators", "retroarch", "info", "fbneo_libretro.info");
        if (!File.Exists(info))
        {
            return;
        }

        Assert.Null(Neuf().ConnuParNomAffiche("FinalBurn Neo"));
    }
    [Fact]
    public void Le_pont_Lua_rehabilite_un_coeur_que_le_wrapper_croyait_muet()
    {
        // Mesure du 24 septembre 2026, 23:18-23:19. Le wrapper enveloppe aussi le coeur libretro
        // MAME et publie « system_ram=NULL » -- c'est vrai, il n'en lit rien. Mais c'est le plugin
        // Lua, charge par ce coeur, qui mesure. Pris pour argent comptant, le verdict du wrapper a
        // fait annoncer « rien ne sera mesure » sur le SEUL coeur MAME qui fonctionne.
        //
        // Une garde au moment du proces-verbal ne suffit pas : quatorze secondes separent les deux
        // annonces. C'est la prise en main du pont, certaine, qui corrige le verdict.
        var liste = Neuf();
        liste.Observer("Core=mame_libretro arcade=YES system_ram=NULL system_ram_size=2048 memory_map_blocks=0");
        Assert.False(liste.Connu("mame_libretro")!.Measures);

        liste.Observer("Core=mame_libretro arcade=YES system_ram=OK system_ram_size=1 memory_map_blocks=0");
        Assert.True(liste.Connu("mame_libretro")!.Measures);
    }
}
