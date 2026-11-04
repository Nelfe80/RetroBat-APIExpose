using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using System.Text;

namespace RetroBat.Api.MassRun;

/// <summary>
/// LA MEMOIRE PARTAGEE ENTRE L'API ET LE COEUR HOTE (CDC MASS RUN §7, lot L2). Le coeur ne fait jamais de reseau : l'API pose
/// la salle (niveau, heure du GO!, couleur et pseudo du joueur, etat de depart) et les coureurs ; le coeur publie sa partie
/// (mode, image de course, arrivee, piste de l'image). Disposition v1, la meme que <c>NelfePlay-Ghosts/src/canal.h</c> :
/// <code>
///      0  'NMRS' (u32), version (u32 = 1), taille (u32), generation (u32)
///     16  actif (u32), crc32 du contenu (u32), GO! (FILETIME UTC, i64), limite en s (u32), couleur 0RGB (u32),
///         pseudo (16 octets UTF-8), niveau (16), taille de l'etat de depart (u32)
///    128  joueur, ecrit par le coeur : sequence (u32, impaire pendant l'ecriture), mode, image de course, arrivee (images,
///         0 = pas encore), image du mode, puis l'image de piste courante (40 octets)
///    256  coureurs, ecrits par l'API : sequence (u32), nombre (u32), image de la premiere fenetre (u32), reserve (u32),
///         puis 64 entrees de 192 octets : couleur 0RGB (u32), pseudo (16), drapeaux (u32, bit 0 = fantome), reserve (8),
///         4 images de piste consecutives (4 x 40) a partir de l'image de la fenetre
///  32768  etat de depart brut du coeur
/// </code>
/// Les blocs ecrits par l'un et lus par l'autre sont proteges par leur sequence : paire = stable.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MassRunCanal : IDisposable
{
    public const string Nom = @"Local\NelfePlay.MassRun";
    public const int Taille = 65536;
    public const uint Signature = 0x53524D4E; // "NMRS"
    public const int TailleImageDePiste = 40;
    public const int MaxCoureurs = 64;
    public const int ImagesParFenetre = 4;

    private const int OffActif = 16, OffCrc = 20, OffGo = 24, OffLimite = 32, OffCouleur = 36, OffPseudo = 40, OffNiveau = 56, OffTailleEtat = 72;
    private const int OffJoueur = 128, OffCoureurs = 256, OffEntrees = 272, TailleEntree = 192, OffEtat = 32768;

    private readonly MemoryMappedFile _fichier;
    private readonly MemoryMappedViewAccessor _vue;
    private uint _sequenceCoureurs;

    public MassRunCanal()
    {
        _fichier = MemoryMappedFile.CreateOrOpen(Nom, Taille, MemoryMappedFileAccess.ReadWrite);
        _vue = _fichier.CreateViewAccessor(0, Taille, MemoryMappedFileAccess.ReadWrite);
    }

    /// <summary>Pose une salle : le coeur la lit au chargement du jeu. L'etat de depart est l'etat brut du coeur.</summary>
    public void PoserSalle(uint crc, DateTime? goUtc, int limiteSecondes, uint couleur, string pseudo, string niveau, byte[] etat)
    {
        if (etat.Length > Taille - OffEtat)
        {
            throw new ArgumentException("etat de depart trop grand pour le canal", nameof(etat));
        }
        _vue.Write(OffActif, 0u);
        _vue.WriteArray(OffJoueur, new byte[OffEtat - OffJoueur], 0, OffEtat - OffJoueur);
        _vue.Write(0, Signature);
        _vue.Write(4, 1u);
        _vue.Write(8, (uint)Taille);
        _vue.Write(OffCrc, crc);
        _vue.Write(OffGo, goUtc?.ToFileTimeUtc() ?? 0L);
        _vue.Write(OffLimite, (uint)limiteSecondes);
        _vue.Write(OffCouleur, couleur);
        EcrireTexte(OffPseudo, pseudo, 16);
        EcrireTexte(OffNiveau, niveau, 16);
        _vue.Write(OffTailleEtat, (uint)etat.Length);
        _vue.WriteArray(OffEtat, etat, 0, etat.Length);
        _vue.Write(12, _vue.ReadUInt32(12) + 1);
        _sequenceCoureurs = 0;
        _vue.Write(OffActif, 1u);
    }

    /// <summary>L'heure du GO! : le coeur la relit a chaque image (0 = pas encore fixee, la salle d'attente continue).</summary>
    public void PoserGo(DateTime goUtc) => _vue.Write(OffGo, goUtc.ToFileTimeUtc());

    /// <summary>Plus de salle : une partie suivante sur le coeur hote redevient une partie normale.</summary>
    public void Fermer() => _vue.Write(OffActif, 0u);

    public readonly record struct EtatDuJoueur(uint Mode, uint ImageCourse, uint Arrivee, uint ImageMode, byte[] Piste);

    /// <summary>La partie du joueur, lue sous sa sequence ; null si le coeur ecrit encore apres quelques essais.</summary>
    public EtatDuJoueur? LireJoueur()
    {
        for (var essai = 0; essai < 8; essai++)
        {
            var avant = _vue.ReadUInt32(OffJoueur);
            if ((avant & 1) != 0)
            {
                continue;
            }
            var mode = _vue.ReadUInt32(OffJoueur + 4);
            var image = _vue.ReadUInt32(OffJoueur + 8);
            var arrivee = _vue.ReadUInt32(OffJoueur + 12);
            var imageMode = _vue.ReadUInt32(OffJoueur + 16);
            var piste = new byte[TailleImageDePiste];
            _vue.ReadArray(OffJoueur + 20, piste, 0, piste.Length);
            if (_vue.ReadUInt32(OffJoueur) == avant)
            {
                return new EtatDuJoueur(mode, image, arrivee, imageMode, piste);
            }
        }
        return null;
    }

    public sealed record Coureur(uint Couleur, string Pseudo, bool Fantome, IReadOnlyList<byte[]?> Fenetre);

    /// <summary>
    /// Les coureurs pour une fenetre de <see cref="ImagesParFenetre"/> images a partir de <paramref name="premiereImage"/> :
    /// une image absente (null) n'est pas dessinee. Le coeur prend celle de l'image qu'il dessine.
    /// </summary>
    public void EcrireCoureurs(uint premiereImage, IReadOnlyList<Coureur> coureurs)
    {
        var n = Math.Min(coureurs.Count, MaxCoureurs);
        _sequenceCoureurs += 1;
        if ((_sequenceCoureurs & 1) == 0) _sequenceCoureurs += 1;
        _vue.Write(OffCoureurs, _sequenceCoureurs);
        _vue.Write(OffCoureurs + 4, (uint)n);
        _vue.Write(OffCoureurs + 8, premiereImage);
        var vide = new byte[TailleImageDePiste];
        for (var i = 0; i < n; i++)
        {
            var c = coureurs[i];
            var o = OffEntrees + i * TailleEntree;
            _vue.Write(o, c.Couleur);
            EcrireTexte(o + 4, c.Pseudo, 16);
            _vue.Write(o + 20, c.Fantome ? 1u : 0u);
            for (var k = 0; k < ImagesParFenetre; k++)
            {
                var im = k < c.Fenetre.Count ? c.Fenetre[k] : null;
                _vue.WriteArray(o + 32 + k * TailleImageDePiste, im ?? vide, 0, TailleImageDePiste);
            }
        }
        _sequenceCoureurs += 1;
        _vue.Write(OffCoureurs, _sequenceCoureurs);
    }

    private void EcrireTexte(int offset, string texte, int taille)
    {
        var octets = new byte[taille];
        var source = Encoding.UTF8.GetBytes(texte ?? string.Empty);
        Array.Copy(source, octets, Math.Min(source.Length, taille - 1));
        _vue.WriteArray(offset, octets, 0, taille);
    }

    public void Dispose()
    {
        _vue.Dispose();
        _fichier.Dispose();
    }
}
