using System.Buffers.Binary;

namespace RetroBat.Api.Replay.Playback;

/// <summary>
/// LES POINTS DE CONTROLE D'UN REPLAY, lus dans le fichier avant la lecture (2026-10-10).
///
/// RetroArch ne saute dans un replay que d'un point de controle a l'autre. Les replays venus d'autres
/// bornes n'en avaient aucun (replay_checkpoint_interval vaut 0 par defaut), et RetroArch 1.22.2 le vit
/// mal : le saut au point suivant parcourt le fichier jusqu'au bout et arrete le film, le jeu repartant
/// alors en direct, manette en main ; le saut vers une frame sans point avant elle recharge le debut
/// (sa recherche rend -1, qu'il prend pour un succes). On lit donc les points avant de lancer, et on ne
/// demande a RetroArch que des sauts qui aboutissent.
///
/// Format BSV de RetroArch 1.22.2 (input/bsv/bsvmovie.c) : un en-tete (10 u32 en v2, 6 en v1), l'etat
/// initial, puis les frames : renvoi u32 (v2), touches (u8 puis 12 o chacune), evenements (u16 puis 8 o
/// chacun), jeton u8 : 'f' frame simple, 'c' point v1 (taille u64 puis l'etat), 'C' point v2
/// (compression u8, encodage u8, trois tailles u32, l'etat compresse).
/// </summary>
public static class PointsDeControle
{
    /// <summary>Ce qu'on demande a SEEK_REPLAY, et la frame ou la lecture se retrouvera.</summary>
    public readonly record struct Saut(long Demande, long Arrivee);

    private const uint Signature = 0x42535632;

    /// <summary>En deca, un point « suivant » ou « precedent » est celui qu'on vient de passer.</summary>
    private const long Marge = 30;

    /// <summary>Le retour au point precedent recule d'au moins deux secondes, comme celui de RetroArch hors pause.</summary>
    private const long ReculMinimal = 60;

    /// <summary>
    /// Les frames des points de controle, dans l'ordre. Vide : aucun point, aucun saut possible.
    /// null : fichier illisible ou format inconnu, et RetroArch fait comme avant.
    /// </summary>
    public static IReadOnlyList<long>? Lire(string chemin)
    {
        try
        {
            using var flux = new FileStream(chemin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            return Lire(flux);
        }
        catch (Exception) { return null; }
    }

    public static IReadOnlyList<long>? Lire(Stream flux)
    {
        var longueur = flux.Length;
        var tampon = new byte[40];
        if (!Exact(flux, tampon.AsSpan(0, 8)) || BinaryPrimitives.ReadUInt32LittleEndian(tampon) != Signature) return null;
        var version = BinaryPrimitives.ReadUInt32LittleEndian(tampon.AsSpan(4));
        var points = new List<long>();
        switch (version)
        {
            case 0:
                return points;   // RetroArch ne saute dans aucun de ces films-la
            case 1:
                // En-tete de 6 u32, la taille de l'etat initial en quatrieme, puis l'etat.
                if (!Exact(flux, tampon.AsSpan(8, 16))) return null;
                if (!Sauter(flux, BinaryPrimitives.ReadUInt32LittleEndian(tampon.AsSpan(12)), longueur)) return null;
                break;
            case 2:
                // En-tete de 10 u32, puis compression, encodage et l'etat initial (trois tailles u32, l'etat compresse).
                if (!Exact(flux, tampon.AsSpan(8, 32)) || !Exact(flux, tampon.AsSpan(0, 14))) return null;
                if (!Sauter(flux, BinaryPrimitives.ReadUInt32LittleEndian(tampon.AsSpan(10)), longueur)) return null;
                break;
            default:
                return null;
        }

        // Un fichier coupe ou un jeton inconnu arretent la lecture, comme ils arretent RetroArch : les
        // points trouves avant restent atteignables.
        for (long frame = 0; ; frame++)
        {
            if (version > 1 && !Sauter(flux, 4, longueur)) break;
            var touches = flux.ReadByte();
            if (touches < 0 || !Sauter(flux, touches * 12L, longueur) || !Exact(flux, tampon.AsSpan(0, 2))) break;
            if (!Sauter(flux, BinaryPrimitives.ReadUInt16LittleEndian(tampon) * 8L, longueur)) break;
            var jeton = flux.ReadByte();
            if (jeton == 'C')
            {
                if (!Exact(flux, tampon.AsSpan(0, 14)) || !Sauter(flux, BinaryPrimitives.ReadUInt32LittleEndian(tampon.AsSpan(10)), longueur)) break;
                points.Add(frame);
            }
            else if (jeton == 'c')
            {
                if (!Exact(flux, tampon.AsSpan(0, 8))) break;
                var taille = BinaryPrimitives.ReadUInt64LittleEndian(tampon);
                if (taille > (ulong)longueur || !Sauter(flux, (long)taille, longueur)) break;
                points.Add(frame);
            }
            else if (jeton != 'f')
            {
                break;
            }
        }
        return points;
    }

    /// <summary>
    /// Un saut de <paramref name="delta"/> frames depuis <paramref name="courante"/> : le point le plus proche
    /// de la cible dans le bon sens, le debut comptant comme un point en arriere. <paramref name="fin"/> borne
    /// les sauts en avant (0 : pas de borne). null : aucun saut utile.
    /// </summary>
    public static Saut? Relatif(IReadOnlyList<long> points, long courante, long delta, long fin = 0)
    {
        if (points.Count == 0 || delta == 0) return null;
        var voulue = courante + delta;
        long? choisi = delta < 0 && courante > Marge ? 0 : null;
        foreach (var p in points)
        {
            if (fin > 0 && p >= fin) break;
            var utile = delta > 0 ? p > courante + Marge : p < courante - Marge;
            if (utile && (choisi is null || Math.Abs(p - voulue) <= Math.Abs(choisi.Value - voulue))) choisi = p;
        }
        return choisi is { } c ? Vers(c) : null;
    }

    /// <summary>Le premier point devant la lecture, ou null s'il n'y en a plus avant <paramref name="fin"/>.</summary>
    public static Saut? Suivant(IReadOnlyList<long> points, long courante, long fin = 0)
    {
        foreach (var p in points)
        {
            if (fin > 0 && p >= fin) break;
            if (p > courante + Marge) return Vers(p);
        }
        return null;
    }

    /// <summary>Le dernier point au moins deux secondes derriere la lecture, ou le debut.</summary>
    public static Saut? Precedent(IReadOnlyList<long> points, long courante)
    {
        if (points.Count == 0) return null;
        long? choisi = null;
        foreach (var p in points)
        {
            if (p >= courante - ReculMinimal) break;
            choisi = p;
        }
        return choisi is { } c ? Vers(c) : Vers(0);
    }

    /// <summary>
    /// RetroArch atterrit sur le dernier point AVANT la frame demandee : pour le point c, on demande c + 1.
    /// Pour le debut, 0 : faute de point avant, il recharge le debut du film.
    /// </summary>
    private static Saut Vers(long point) => point <= 0 ? new Saut(0, 0) : new Saut(point + 1, point);

    private static bool Exact(Stream flux, Span<byte> destination)
    {
        try
        {
            flux.ReadExactly(destination);
            return true;
        }
        catch (EndOfStreamException) { return false; }
    }

    private static bool Sauter(Stream flux, long octets, long longueur)
    {
        if (octets < 0 || flux.Position + octets > longueur) return false;
        flux.Seek(octets, SeekOrigin.Current);
        return true;
    }
}
