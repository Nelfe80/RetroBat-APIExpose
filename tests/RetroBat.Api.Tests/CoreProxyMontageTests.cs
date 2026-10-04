using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le montage core_proxy (2026-10-04), propose par l'equipe RetroBat : le vrai coeur reste dans
/// cores/, ou RetroBat le met a jour, et le lanceur passe a RetroArch notre wrapper depuis
/// core_proxy/. Avec un lanceur qui ne le sait pas, l'ancien montage (wrapper dans cores/, vrai
/// coeur dans cores_real/) reste en place. Ces tests gelent le passage de l'un a l'autre, dans
/// les deux sens, sans perdre un seul vrai coeur.
/// </summary>
public sealed class CoreProxyMontageTests : IDisposable
{
    private static readonly byte[] Signature = Encoding.ASCII.GetBytes("RETROBAT_ARCADE_WRAPPER_V1_DO_NOT_DELETE");

    private readonly string _racine = Path.Combine(Path.GetTempPath(), "core-proxy-tests-" + Guid.NewGuid().ToString("N"));
    private string Cores => Path.Combine(_racine, "cores");
    private string CoresReal => Path.Combine(_racine, "cores_real");
    private string CoreProxy => Path.Combine(_racine, "core_proxy");
    private string Wrapper => Path.Combine(_racine, "wrapper", "wrapper.dll");

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Le_lanceur_qui_gere_core_proxy_se_reconnait_a_sa_chaine_dotnet()
    {
        // Une chaine litterale .NET vit en UTF-16 dans l'exe : « core_proxy » en ASCII n'en est pas une.
        var avec = Encoding.ASCII.GetBytes("MZ...").Concat(Encoding.Unicode.GetBytes("retroarch\\core_proxy")).ToArray();
        Assert.True(RetroArchWrapperDeploymentService.PorteLaMarqueCoreProxy(avec));
        Assert.False(RetroArchWrapperDeploymentService.PorteLaMarqueCoreProxy(Encoding.ASCII.GetBytes("MZ core_proxy")));
        Assert.False(RetroArchWrapperDeploymentService.PorteLaMarqueCoreProxy(Encoding.Unicode.GetBytes("retroarch\\cores")));
        Assert.False(RetroArchWrapperDeploymentService.LanceurGereCoreProxy(Path.Combine(_racine, "absent.exe")));
    }

    [Theory]
    [InlineData("auto", true, "core_proxy")]
    [InlineData("auto", false, "cores_real")]
    [InlineData("", true, "core_proxy")]
    [InlineData(null, false, "cores_real")]
    [InlineData("cores_real", true, "cores_real")]
    [InlineData(" CORE_PROXY ", false, "core_proxy")]
    public void Le_montage_suit_le_lanceur_sauf_si_la_configuration_le_force(string? option, bool lanceurGere, string attendu)
    {
        Assert.Equal(attendu, RetroArchWrapperDeploymentService.ChoisirMontage(option, lanceurGere));
    }

    [Fact]
    public void Un_coeur_de_l_ancien_montage_revient_dans_cores_et_recoit_son_proxy()
    {
        var plan = RetroArchWrapperDeploymentService.ArbitrerCoreProxy(
            dansCores: true, estWrapper: true, dansCoresReal: true, cible: true, exclu: false, proxyExiste: false, proxyAJour: false);
        Assert.True(plan.RemettreLeVrai);
        Assert.False(plan.RetirerLAncien);
        Assert.True(plan.EcrireProxy);
    }

    [Fact]
    public void Un_coeur_mis_a_jour_par_RetroBat_garde_sa_version_et_l_ancienne_copie_part()
    {
        // L'ancien montage laissait RetroBat ecrire par-dessus le wrapper : cores/ a le coeur a jour,
        // cores_real/ l'ancien. C'est cores/ qui fait foi.
        var plan = RetroArchWrapperDeploymentService.ArbitrerCoreProxy(
            dansCores: true, estWrapper: false, dansCoresReal: true, cible: true, exclu: false, proxyExiste: false, proxyAJour: false);
        Assert.False(plan.RemettreLeVrai);
        Assert.True(plan.RetirerLAncien);
        Assert.True(plan.EcrireProxy);
    }

    [Fact]
    public void Un_wrapper_sans_vrai_coeur_ne_se_touche_pas_et_n_a_pas_de_proxy()
    {
        // Le proxy chargerait le wrapper de cores/ comme vrai coeur : on ne pose rien.
        var plan = RetroArchWrapperDeploymentService.ArbitrerCoreProxy(
            dansCores: true, estWrapper: true, dansCoresReal: false, cible: true, exclu: false, proxyExiste: true, proxyAJour: true);
        Assert.True(plan.Orphelin);
        Assert.False(plan.EcrireProxy);
        Assert.True(plan.RetirerProxy);
    }

    [Fact]
    public void Un_coeur_exclu_est_charge_tel_quel_depuis_cores()
    {
        var plan = RetroArchWrapperDeploymentService.ArbitrerCoreProxy(
            dansCores: true, estWrapper: false, dansCoresReal: false, cible: true, exclu: true, proxyExiste: true, proxyAJour: true);
        Assert.False(plan.VeutProxy);
        Assert.True(plan.RetirerProxy);
    }

    [Fact]
    public void Un_proxy_a_jour_ne_bouge_pas()
    {
        var plan = RetroArchWrapperDeploymentService.ArbitrerCoreProxy(
            dansCores: true, estWrapper: false, dansCoresReal: false, cible: true, exclu: false, proxyExiste: true, proxyAJour: true);
        Assert.Equal(new RetroArchWrapperDeploymentService.PlanCoreProxy(false, false, false, true, true, false, false), plan);
    }

    [Fact]
    public async Task Une_borne_passe_au_montage_core_proxy_puis_revient_sans_perdre_un_coeur()
    {
        var wrapper = Dll("wrapper 0.341", signe: true);
        var ancienWrapper = Dll("wrapper 0.340 plus ancien", signe: true);
        Ecrire(Wrapper, wrapper);

        // L'ancien montage tel qu'une borne le porte.
        Ecrire(Path.Combine(Cores, "fbneo_libretro.dll"), ancienWrapper);
        Ecrire(Path.Combine(CoresReal, "fbneo_libretro.dll"), Dll("FBNeo"));
        Ecrire(Path.Combine(Cores, "snes9x_libretro.dll"), Dll("snes9x mis a jour par RetroBat"));
        Ecrire(Path.Combine(CoresReal, "snes9x_libretro.dll"), Dll("snes9x ancien"));
        Ecrire(Path.Combine(CoresReal, "genesis_plus_gx_libretro.dll"), Dll("Genesis Plus GX"));
        Ecrire(Path.Combine(Cores, "mame_libretro.dll"), Dll("MAME"));
        Ecrire(Path.Combine(CoresReal, "mame_libretro.dll"), Dll("MAME"));
        Ecrire(Path.Combine(Cores, "orphelin_libretro.dll"), ancienWrapper);
        Ecrire(Path.Combine(CoreProxy, "fantome_libretro.dll"), wrapper);

        var resultat = await Service("core_proxy").DeployAsync(dryRun: false);

        Assert.Equal("core_proxy", resultat.Layout);
        Assert.Equal(Dll("FBNeo"), Lire(Cores, "fbneo_libretro.dll"));
        Assert.Equal(Dll("snes9x mis a jour par RetroBat"), Lire(Cores, "snes9x_libretro.dll"));
        Assert.Equal(Dll("Genesis Plus GX"), Lire(Cores, "genesis_plus_gx_libretro.dll"));
        Assert.Equal(Dll("MAME"), Lire(Cores, "mame_libretro.dll"));
        Assert.Equal(ancienWrapper, Lire(Cores, "orphelin_libretro.dll"));
        Assert.False(Directory.Exists(CoresReal));

        foreach (var nom in new[] { "fbneo_libretro.dll", "snes9x_libretro.dll", "genesis_plus_gx_libretro.dll" })
        {
            Assert.Equal(wrapper, Lire(CoreProxy, nom));
        }
        Assert.Equal(
            new[] { "fbneo_libretro.dll", "genesis_plus_gx_libretro.dll", "snes9x_libretro.dll" },
            Directory.GetFiles(CoreProxy).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(3, resultat.WrappedCores);
        Assert.Equal(1, resultat.MissingRealCores);
        Assert.Contains(resultat.Warnings, w => w.StartsWith("orphelin_libretro.dll", StringComparison.Ordinal));

        // Les copies perimees de cores_real/ sont en sauvegarde, pas effacees.
        var sauvegardes = Directory.GetFiles(Path.Combine(_racine, "backups"), "*.dll", SearchOption.AllDirectories)
            .Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "mame_libretro.dll", "snes9x_libretro.dll" }, sauvegardes);

        // Un seul wrapper sur le disque : les entrees sont des liens (NTFS).
        using (var f = File.Open(Path.Combine(CoreProxy, "fbneo_libretro.dll"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            f.Position = 0;
            f.WriteByte((byte) 'X');
        }
        Assert.Equal((byte) 'X', Lire(CoreProxy, "genesis_plus_gx_libretro.dll")[0]);
        Ecrire(Path.Combine(CoreProxy, "fbneo_libretro.dll"), wrapper);
        File.SetLastWriteTimeUtc(Path.Combine(CoreProxy, "fbneo_libretro.dll"), File.GetLastWriteTimeUtc(Wrapper));

        // Repasser ne change rien.
        var encore = await Service("core_proxy").DeployAsync(dryRun: false);
        Assert.Equal(0, encore.ProxyEntriesWritten);
        Assert.Equal(0, encore.RealCoresMovedBack);
        Assert.Equal(3, encore.WrappedCores);

        // Retour a l'ancien montage (lanceur sans core_proxy) : les vrais coeurs repartent dans
        // cores_real/, le wrapper revient dans cores/, core_proxy/ disparait.
        var retour = await Service("cores_real").DeployAsync(dryRun: false);
        Assert.Equal("cores_real", retour.Layout);
        Assert.Equal(wrapper, Lire(Cores, "fbneo_libretro.dll"));
        Assert.Equal(Dll("FBNeo"), Lire(CoresReal, "fbneo_libretro.dll"));
        Assert.Equal(Dll("snes9x mis a jour par RetroBat"), Lire(CoresReal, "snes9x_libretro.dll"));
        Assert.Equal(Dll("MAME"), Lire(Cores, "mame_libretro.dll"));
        Assert.False(Directory.Exists(CoreProxy));
    }

    private RetroArchWrapperDeploymentService Service(string montage) => new(
        Options.Create(new ApiExposeOptions
        {
            RetroArchWrapperDeployment = new ApiExposeOptions.RetroArchWrapperDeploymentOptions
            {
                Layout = montage,
                WrapperDllPath = Wrapper,
                CoresPath = Cores,
                RealCoresPath = CoresReal,
                CoreProxyPath = CoreProxy,
                EmulatorLauncherPath = Path.Combine(_racine, "emulatorLauncher.exe"),
                BackupPath = Path.Combine(_racine, "backups"),
                LogFilePath = Path.Combine(_racine, "deploiement.jsonl"),
                CachePath = Path.Combine(_racine, "cache.json"),
                ExcludedCores = ["mame_libretro"],
                SkipIfRetroArchRunning = false
            }
        }),
        NullLogger<RetroArchWrapperDeploymentService>.Instance);

    private static byte[] Dll(string contenu, bool signe = false) =>
        Encoding.ASCII.GetBytes("MZ " + contenu + " ").Concat(signe ? Signature : Array.Empty<byte>()).ToArray();

    private static void Ecrire(string chemin, byte[] contenu)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
        File.WriteAllBytes(chemin, contenu);
    }

    private static byte[] Lire(string dossier, string nom) => File.ReadAllBytes(Path.Combine(dossier, nom));
}
