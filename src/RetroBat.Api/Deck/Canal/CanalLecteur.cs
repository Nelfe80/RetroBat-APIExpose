using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;

namespace RetroBat.Api.Deck.Canal;

/// <summary>
/// LE LECTEUR DU CANAL : ouvre <c>Local\NelfePlay.Canal.&lt;pid&gt;</c> en LECTURE SEULE (le wrapper le cree), lit un bloc COHERENT
/// (sequence paire, la meme avant et apres la copie ; impaire = le wrapper ecrit : on recommence) dans un tampon reutilise. Rien
/// n'est alloue dans la boucle de lecture. Aucun verrou, aucune attente longue : au pire quelques tours de boucle.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CanalLecteur : IDisposable
{
    public static string Nom(int pid) => @"Local\NelfePlay.Canal." + pid;

    private readonly MemoryMappedFile _fichier;
    private readonly MemoryMappedViewAccessor _vue;
    private byte[] _tampon;
    private CanalBloc? _bloc;

    public int Pid { get; }
    public CanalDescripteur Descripteur { get; private set; }

    /// <summary>Les lectures qui ont du recommencer (sequence impaire ou changee) : un indice du coup de la lecture.</summary>
    public long Relectures { get; private set; }

    private CanalLecteur(int pid, MemoryMappedFile f, MemoryMappedViewAccessor v, byte[] tampon, CanalDescripteur d)
    {
        Pid = pid;
        _fichier = f;
        _vue = v;
        _tampon = tampon;
        Descripteur = d;
    }

    /// <summary>Ouvre le canal de ce processus ; null s'il n'existe pas (vieux wrapper, canal non demande, jeu sans .VIEW) ou s'il est incoherent.</summary>
    public static CanalLecteur? Ouvrir(int pid)
    {
        MemoryMappedFile? f = null;
        MemoryMappedViewAccessor? v = null;
        try
        {
            f = MemoryMappedFile.OpenExisting(Nom(pid), MemoryMappedFileRights.Read);
            v = f.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            var entete = new byte[CanalFormat.TailleEntete];
            v.ReadArray(0, entete, 0, entete.Length);
            var taille = CanalFormat.TailleDeclaree(entete);
            if (taille < 0 || taille > v.Capacity) { v.Dispose(); f.Dispose(); return null; }
            var tampon = new byte[taille];
            if (!LireCoherent(v, tampon, out _)) { v.Dispose(); f.Dispose(); return null; }
            var d = CanalFormat.LireDescripteur(tampon);
            if (d is null) { v.Dispose(); f.Dispose(); return null; }
            return new CanalLecteur(pid, f, v, tampon, d);
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            v?.Dispose();
            f?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Un bloc coherent dans <paramref name="bloc"/> (reutilise). Faux : le canal ne repond pas (ecriture sans fin, ou un autre .VIEW est
    /// charge : <see cref="Recharger"/>).
    /// </summary>
    public bool Lire(out CanalBloc bloc)
    {
        _bloc ??= new CanalBloc(Descripteur);
        bloc = _bloc;
        if (!LireCoherent(_vue, _tampon, out var essais)) { Relectures += essais; return false; }
        Relectures += essais - 1;
        return CanalFormat.Lire(_tampon, Descripteur, _bloc);
    }

    /// <summary>Le wrapper a charge un autre .VIEW : relit le descripteur (rend faux si le canal n'en a plus).</summary>
    public bool Recharger()
    {
        var entete = new byte[CanalFormat.TailleEntete];
        _vue.ReadArray(0, entete, 0, entete.Length);
        var taille = CanalFormat.TailleDeclaree(entete);
        if (taille < 0 || taille > _vue.Capacity) return false;
        var tampon = new byte[taille];
        if (!LireCoherent(_vue, tampon, out _)) return false;
        var d = CanalFormat.LireDescripteur(tampon);
        if (d is null) return false;
        _tampon = tampon;
        Descripteur = d;
        _bloc = null;
        return true;
    }

    private static bool LireCoherent(MemoryMappedViewAccessor v, byte[] tampon, out int essais)
    {
        for (essais = 1; essais <= 200; essais++)
        {
            var s1 = v.ReadUInt32(12);
            if ((s1 & 1) != 0) { Thread.SpinWait(50); continue; }
            Thread.MemoryBarrier();
            v.ReadArray(0, tampon, 0, tampon.Length);
            Thread.MemoryBarrier();
            var s2 = v.ReadUInt32(12);
            if (s1 == s2 && CanalFormat.Sequence(tampon) == s1) return true;
        }

        return false;
    }

    public void Dispose()
    {
        _vue.Dispose();
        _fichier.Dispose();
    }
}
