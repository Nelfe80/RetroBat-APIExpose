using System.Text;

namespace RetroBat.Api.Deck.Canal;

/// <summary>
/// LE CANAL D'AFFICHAGE DU WRAPPER (0.348, CDC-NelfePlay-Canal-Memoire-Deck-v0.1, decisions du user du 22/11) : la memoire
/// partagee <c>Local\NelfePlay.Canal.&lt;pid&gt;</c> que le wrapper CREE dans le processus du jeu et que l'API lit en lecture seule.
/// AFFICHAGE SEULEMENT : rien de ce bloc n'entre dans un score, une regle ou un passeport. Ce fichier decrit la disposition
/// (celle de <c>canal.h</c> du wrapper, la source de verite) et sa lecture PURE sur un tampon d'octets : testable sans memoire
/// partagee, sans processus.
///
/// Disposition (petit-boutiste) : en-tete de 64 octets (signature NCNL, version, taille, sequence, image, epoch, ecritures, etat,
/// pid, cadence, nombres, decalages), puis le descripteur (sha256 du .VIEW, nom, une description de 32 octets par champ, par table
/// et par colonne), puis les donnees (un int32 par champ ; par table : ecrites, actives, puis « garder » entrees de
/// [emplacement, colonnes...] en int32).
/// </summary>
public static class CanalFormat
{
    public const uint Signature = 0x4C4E434E;   // « NCNL »
    public const int TailleEntete = 64;
    public const int TailleDescription = 32;
    public const int MaxTaille = 1 << 20;       // un canal fait quelques Ko : au-dela, ce n'est pas lui

    /// <summary>L'etat du canal dit par le wrapper.</summary>
    public const uint EtatActif = 0, EtatCoupeNetplay = 1, EtatFerme = 2;

    private const int OffTaille = 8, OffSequence = 12, OffImage = 16, OffEpoch = 20, OffEcritures = 24, OffEtat = 28, OffPid = 32,
        OffNbChamps = 38, OffNbTables = 40, OffRestaurations = 44, OffDescripteur = 48, OffDonnees = 52, OffCout = 56;

    /// <summary>La taille declaree par l'en-tete, ou -1 si ce n'est pas un canal (signature, version, taille).</summary>
    public static int TailleDeclaree(ReadOnlySpan<byte> entete)
    {
        if (entete.Length < TailleEntete || U32(entete, 0) != Signature) return -1;
        if (U16(entete, 4) != 1) return -1;
        var taille = (int)U32(entete, OffTaille);
        return taille >= TailleEntete && taille <= MaxTaille ? taille : -1;
    }

    /// <summary>La sequence de l'en-tete : impaire = le wrapper ecrit, pair = bloc stable.</summary>
    public static uint Sequence(ReadOnlySpan<byte> tampon) => U32(tampon, OffSequence);

    /// <summary>Lit le descripteur du bloc (une seule fois : il ne change pas tant que le .VIEW est le meme). Null si le bloc est incoherent.</summary>
    public static CanalDescripteur? LireDescripteur(ReadOnlySpan<byte> b)
    {
        var taille = TailleDeclaree(b);
        if (taille < 0 || b.Length < taille) return null;
        var od = (int)U32(b, OffDescripteur);
        var odon = (int)U32(b, OffDonnees);
        int nc = U16(b, OffNbChamps), nt = U16(b, OffNbTables);
        if (od < TailleEntete || od + 96 > taille || odon < od || odon > taille) return null;
        var p = od + 96;
        var champs = new List<CanalDescription>(nc);
        for (var i = 0; i < nc; i++, p += TailleDescription)
        {
            if (p + TailleDescription > taille) return null;
            champs.Add(Description(b, p));
        }

        var tables = new List<CanalTableDescription>(nt);
        for (var i = 0; i < nt; i++)
        {
            if (p + TailleDescription > taille) return null;
            var nom = Texte(b, p, 24);
            int nombre = U16(b, p + 24), garder = U16(b, p + 26), ncol = U16(b, p + 28), drapeaux = U16(b, p + 30);
            p += TailleDescription;
            var colonnes = new List<CanalDescription>(ncol);
            for (var k = 0; k < ncol; k++, p += TailleDescription)
            {
                if (p + TailleDescription > taille) return null;
                colonnes.Add(Description(b, p));
            }

            tables.Add(new CanalTableDescription(nom, nombre, garder, drapeaux, colonnes));
        }

        var d = new CanalDescripteur(Convert.ToHexString(b.Slice(od, 32)).ToLowerInvariant(), Texte(b, od + 32, 64), champs, tables, odon, taille);
        return d.TailleDonnees + odon <= taille ? d : null;
    }

    /// <summary>
    /// Lit un bloc COHERENT (sequence paire, la meme avant et apres la copie : c'est l'affaire de l'appelant) dans un etat reutilise.
    /// Rend faux si le bloc ne colle pas au descripteur (un autre .VIEW a ete charge : l'appelant relit le descripteur).
    /// </summary>
    public static bool Lire(ReadOnlySpan<byte> b, CanalDescripteur d, CanalBloc sortie)
    {
        if (TailleDeclaree(b) != d.Taille || b.Length < d.Taille) return false;
        if (!Convert.ToHexString(b.Slice((int)U32(b, OffDescripteur), 32)).Equals(d.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        sortie.Sequence = U32(b, OffSequence);
        sortie.Image = U32(b, OffImage);
        sortie.Epoch = U32(b, OffEpoch);
        sortie.Ecritures = U32(b, OffEcritures);
        sortie.Etat = U32(b, OffEtat);
        sortie.Pid = U32(b, OffPid);
        sortie.Restaurations = U32(b, OffRestaurations);
        sortie.CoutNs = U32(b, OffCout);
        var p = d.OffDonnees;
        for (var i = 0; i < d.Champs.Count; i++, p += 4) sortie.Champs[i] = I32(b, p);
        for (var t = 0; t < d.Tables.Count; t++)
        {
            var td = d.Tables[t];
            var ecrites = I32(b, p);
            var actives = I32(b, p + 4);
            p += 8;
            var ts = sortie.Tables[t];
            ts.Ecrites = Math.Clamp(ecrites, 0, td.Garder);
            ts.Actives = Math.Max(0, actives);
            var largeur = 1 + td.Colonnes.Count;
            for (var e = 0; e < ts.Ecrites; e++)
                for (var k = 0; k < largeur; k++)
                    ts.Valeurs[e * largeur + k] = I32(b, p + 4 * (e * largeur + k));
            p += td.Garder * largeur * 4;
        }

        return true;
    }

    internal static uint U32(ReadOnlySpan<byte> b, int o) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(o, 4));
    internal static int I32(ReadOnlySpan<byte> b, int o) => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(b.Slice(o, 4));
    internal static ushort U16(ReadOnlySpan<byte> b, int o) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(o, 2));

    private static string Texte(ReadOnlySpan<byte> b, int o, int n)
    {
        var s = b.Slice(o, n);
        var fin = s.IndexOf((byte)0);
        return Encoding.ASCII.GetString(fin < 0 ? s : s[..fin]);
    }

    private static CanalDescription Description(ReadOnlySpan<byte> b, int o) => new(Texte(b, o, 24), b[o + 24], b[o + 25] != 0, U32(b, o + 28));
}

/// <summary>Un champ ou une colonne : nom, type (1 a 12 de canal.h), « resolu » (le wrapper a trouve l'adresse), masque.</summary>
public sealed record CanalDescription(string Nom, byte Type, bool Resolu, uint Masque);

/// <summary>Une table : nombre de cases du jeu, entrees gardees au plus, colonnes (sans l'emplacement, qui ouvre chaque entree), drapeaux (1 actif, 2 proche).</summary>
public sealed record CanalTableDescription(string Nom, int Nombre, int Garder, int Drapeaux, IReadOnlyList<CanalDescription> Colonnes)
{
    public int Largeur => 1 + Colonnes.Count;

    /// <summary>L'indice de la colonne de ce nom (hors emplacement), ou -1.</summary>
    public int Colonne(string nom)
    {
        for (var i = 0; i < Colonnes.Count; i++) if (string.Equals(Colonnes[i].Nom, nom, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}

public sealed record CanalDescripteur(string Sha256, string Nom, IReadOnlyList<CanalDescription> Champs, IReadOnlyList<CanalTableDescription> Tables, int OffDonnees, int Taille)
{
    public int TailleDonnees
    {
        get
        {
            var n = Champs.Count * 4;
            foreach (var t in Tables) n += 8 + t.Garder * t.Largeur * 4;
            return n;
        }
    }

    public int Champ(string nom)
    {
        for (var i = 0; i < Champs.Count; i++) if (string.Equals(Champs[i].Nom, nom, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    public int Table(string nom)
    {
        for (var i = 0; i < Tables.Count; i++) if (string.Equals(Tables[i].Nom, nom, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}

/// <summary>Un bloc lu, reutilise d'une lecture a l'autre (aucune allocation dans la boucle de lecture).</summary>
public sealed class CanalBloc
{
    public uint Sequence, Image, Epoch, Ecritures, Etat, Pid, Restaurations, CoutNs;
    public readonly int[] Champs;
    public readonly CanalTableLue[] Tables;

    public CanalBloc(CanalDescripteur d)
    {
        Champs = new int[d.Champs.Count];
        Tables = new CanalTableLue[d.Tables.Count];
        for (var i = 0; i < Tables.Length; i++) Tables[i] = new CanalTableLue(d.Tables[i]);
    }
}

/// <summary>Les entrees ecrites d'une table : <c>Valeurs[e * Largeur + 0]</c> = l'emplacement dans la table du jeu, puis les colonnes.</summary>
public sealed class CanalTableLue
{
    public int Ecrites, Actives;
    public readonly int Largeur;
    public readonly int[] Valeurs;

    public CanalTableLue(CanalTableDescription d)
    {
        Largeur = d.Largeur;
        Valeurs = new int[Math.Max(1, d.Garder) * Largeur];
    }

    public int Emplacement(int e) => Valeurs[e * Largeur];
    public int Colonne(int e, int c) => Valeurs[e * Largeur + 1 + c];
}
