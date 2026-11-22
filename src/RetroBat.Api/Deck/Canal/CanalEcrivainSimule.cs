using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;

namespace RetroBat.Api.Deck.Canal;

/// <summary>
/// L'ECRIVAIN SIMULE DU CANAL (outil d'essai, jamais le wrapper) : ecrit un bloc dans la meme disposition que <c>canal.h</c> du
/// wrapper 0.348 (en-tete de 64 octets, descripteur, donnees ; protocole de sequence impaire pendant l'ecriture), pour faire tourner
/// l'API et le Deck sans jeu. Sert aux tests de l'API (tampon en memoire) et au simulateur <c>tools\CanalSimulateur</c> (memoire
/// partagee <c>Local\NelfePlay.Canal.&lt;pid&gt;</c>). Ce fichier est aussi compile tel quel par le simulateur : il ne depend que de la
/// bibliotheque de base et de <see cref="CanalFormat"/>.
/// </summary>
public sealed class CanalEcrivainSimule : IDisposable
{
    public sealed record Table(string Nom, int Nombre, int Garder, string[] Colonnes, bool Actif = true);

    private readonly string[] _champs;
    private readonly Table[] _tables;
    private readonly byte[] _tampon;
    private readonly int _offDonnees;
    private MemoryMappedFile? _fichier;
    private MemoryMappedViewAccessor? _vue;
    private uint _sequence, _ecritures;

    public uint Epoch { get; private set; } = 1;
    public uint Pid { get; }

    /// <summary>Le bloc tel qu'il serait en memoire (les tests le lisent directement).</summary>
    public byte[] Tampon => _tampon;

    public CanalEcrivainSimule(string nomVue, string[] champs, Table[] tables, uint pid)
    {
        _champs = champs;
        _tables = tables;
        Pid = pid;
        var desc = 96 + 32 * champs.Length;
        foreach (var t in tables) desc += 32 + 32 * t.Colonnes.Length;
        var donnees = 4 * champs.Length;
        foreach (var t in tables) donnees += 8 + 4 * t.Garder * (1 + t.Colonnes.Length);
        _offDonnees = CanalFormat.TailleEntete + desc;
        _tampon = new byte[_offDonnees + donnees];
        Ecrire32(0, CanalFormat.Signature);
        Ecrire16(4, 1);
        Ecrire16(6, CanalFormat.TailleEntete);
        Ecrire32(8, (uint)_tampon.Length);   // taille UTILE (la vue du wrapper est arrondie a 4 Kio)
        Ecrire16(36, 1);
        Ecrire16(38, (ushort)champs.Length);
        Ecrire16(40, (ushort)tables.Length);
        Ecrire16(42, 1);                   // preparations (resolutions des adresses)
        Ecrire32(48, CanalFormat.TailleEntete);
        Ecrire32(52, (uint)_offDonnees);
        Ecrire32(32, pid);
        var p = CanalFormat.TailleEntete;
        SHA256.HashData(Encoding.ASCII.GetBytes(nomVue)).CopyTo(_tampon, p);
        Texte(p + 32, 64, nomVue);
        p += 96;
        foreach (var c in champs) { Description(p, c); p += 32; }
        foreach (var t in tables)
        {
            Texte(p, 24, t.Nom);
            Ecrire16(p + 24, (ushort)t.Nombre);
            Ecrire16(p + 26, (ushort)t.Garder);
            Ecrire16(p + 28, (ushort)t.Colonnes.Length);
            Ecrire16(p + 30, (ushort)(t.Actif ? 1 : 0));
            p += 32;
            foreach (var c in t.Colonnes) { Description(p, c); p += 32; }
        }
    }

    /// <summary>La memoire partagee nommee du pid de l'ecrivain (ou d'un autre pid pour les essais) : creee et gardee tant que l'objet vit.</summary>
    public void Creer(int? pid = null)
    {
        var nom = @"Local\NelfePlay.Canal." + (pid ?? (int)Pid);
        _fichier = MemoryMappedFile.CreateOrOpen(nom, _tampon.Length);
        _vue = _fichier.CreateViewAccessor(0, _tampon.Length);
        _vue.WriteArray(0, _tampon, 0, _tampon.Length);
    }

    /// <summary>Ecrit une image : un entier par champ, et pour chaque table ses entrees <c>[emplacement, colonnes...]</c>.</summary>
    public void Ecrire(uint image, int[] champs, int[][][] tables, uint etat = 0)
    {
        _sequence++;                       // impaire : ecriture en cours
        Publier();
        Ecrire32(16, image);
        Ecrire32(20, Epoch);
        Ecrire32(24, ++_ecritures);
        Ecrire32(28, etat);
        var p = _offDonnees;
        for (var i = 0; i < _champs.Length; i++, p += 4) Ecrire32(p, (uint)(i < champs.Length ? champs[i] : 0));
        for (var t = 0; t < _tables.Length; t++)
        {
            var d = _tables[t];
            var entrees = t < tables.Length ? tables[t] : [];
            var n = Math.Min(entrees.Length, d.Garder);
            Ecrire32(p, (uint)n);
            Ecrire32(p + 4, (uint)entrees.Length);
            p += 8;
            var largeur = 1 + d.Colonnes.Length;
            for (var e = 0; e < n; e++)
                for (var k = 0; k < largeur; k++)
                    Ecrire32(p + 4 * (e * largeur + k), (uint)(k < entrees[e].Length ? entrees[e][k] : 0));
            p += d.Garder * largeur * 4;
        }

        PublierLeCorps();
        _sequence++;                       // paire : stable
        Publier();
    }

    /// <summary>Un chargement d'etat : l'epoch change, le Deck vide ses tables.</summary>
    public void NouvelleEpoque() => Epoch++;

    /// <summary>Coupe (1 : netplay) ou ferme (2) le canal.</summary>
    public void Couper(uint etat)
    {
        _sequence++;
        Publier();
        Ecrire32(28, etat);
        PublierLeCorps();
        _sequence++;
        Publier();
    }

    // Le protocole du wrapper : la sequence seule (impaire) avant le corps, le corps, puis la sequence seule (paire) : un lecteur
    // qui voit la meme sequence paire avant et apres sa copie a un bloc entier.
    private void Publier()
    {
        Ecrire32(12, _sequence);
        _vue?.Write(12, _sequence);
    }

    private void PublierLeCorps()
    {
        if (_vue is not null) _vue.WriteArray(16, _tampon, 16, _tampon.Length - 16);
    }

    private void Description(int o, string nom)
    {
        Texte(o, 24, nom);
        _tampon[o + 24] = 9;               // u32le
        _tampon[o + 25] = 1;               // resolu
        Ecrire32(o + 28, 0xFFFFFFFFu);
    }

    private void Texte(int o, int n, string s)
    {
        var b = Encoding.ASCII.GetBytes(s);
        Array.Copy(b, 0, _tampon, o, Math.Min(b.Length, n - 1));
    }

    private void Ecrire32(int o, uint v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(_tampon.AsSpan(o, 4), v);
    private void Ecrire16(int o, ushort v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(_tampon.AsSpan(o, 2), v);

    public void Dispose()
    {
        _vue?.Dispose();
        _fichier?.Dispose();
    }
}
