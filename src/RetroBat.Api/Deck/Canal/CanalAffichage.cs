namespace RetroBat.Api.Deck.Canal;

/// <summary>
/// LE MESSAGE <c>deck.live</c> (API 1.9.211, CDC Canal v0.1) : ce que le canal du wrapper dit de l'image en cours, converti en
/// pixels d'ECRAN du jeu (le repere du releve de niveau : x vers la droite, y vers le BAS, 0,0 en haut a gauche). Un seul message
/// compact, publie a 30 Hz au plus et seulement sur changement (plus un battement par seconde). AFFICHAGE SEULEMENT.
///
/// Sans <see cref="Active"/> : le canal est perdu, coupe (netplay) ou ferme ; le Deck revient a son comportement d'avant (releve et
/// extrapolation). Les ennemis sont a plat : <c>[emplacement, x, y, classe, vie]</c> par ennemi, <see cref="Enemies"/> en entiers
/// (classe 0 leger, 1 lourd, 2 special : mi-boss, boss, 3 generique ou inconnu).
/// </summary>
public sealed record DeckLive
{
    /// <summary>Le canal parle (vrai) ou vient de se taire (faux : le Deck reprend le releve et l'extrapolation).</summary>
    public bool Active { get; init; }

    /// <summary>Pourquoi un canal inactif : closed, netplay, lost.</summary>
    public string? Reason { get; init; }

    public string Rom { get; init; } = "";

    /// <summary>Change au chargement d'un etat ou a un retour arriere : le Deck vide ses tables.</summary>
    public uint Epoch { get; init; }

    /// <summary>Numero d'image du coeur (comme FRAME:n du tube).</summary>
    public uint Image { get; init; }

    /// <summary>Compte les messages (le Deck ignore un ancien message arrive apres un plus recent).</summary>
    public long Seq { get; init; }

    /// <summary>La taille de l'ecran du jeu dans le repere de <see cref="Enemies"/> et <see cref="Ship"/> (celle du releve de niveau).</summary>
    public int ScreenW { get; init; }
    public int ScreenH { get; init; }

    /// <summary>La distance du niveau, EXACTE (valeur brute du jeu, a chaque image : le Deck applique la regle de son releve). -1 : pas de distance.</summary>
    public int Dist { get; init; } = -1;

    /// <summary>La camera (decalage horizontal du decor en pixels, signe), 0 sans.</summary>
    public double Camera { get; init; }

    /// <summary>Le niveau (numero du jeu), -1 sans.</summary>
    public int Stage { get; init; } = -1;

    /// <summary>Les vies restantes (255 hors partie), -1 sans.</summary>
    public int Lives { get; init; } = -1;

    /// <summary>Le compteur de temps du jeu (+1 par image en jeu), -1 sans.</summary>
    public int Tick { get; init; } = -1;

    /// <summary>Le vaisseau en pixels d'ecran : <c>[x, y]</c>, ou null s'il n'y est pas.</summary>
    public int[]? Ship { get; init; }

    /// <summary>Les ennemis vivants a l'ecran : <c>[emplacement, x, y, classe, vie]</c> a plat, au plus 64.</summary>
    public int[] Enemies { get; init; } = [];

    /// <summary>La vie du boss : <c>[vie, vie la plus haute vue depuis son apparition]</c>, ou null.</summary>
    public int[]? Boss { get; init; }

    /// <summary>La minuterie du combo : <c>[valeur, maximum]</c> si le .VIEW la porte (champs combo et combo_max), sinon null.</summary>
    public int[]? Combo { get; init; }
}

/// <summary>
/// Le sens du bloc d'un jeu : comment les valeurs brutes du canal deviennent des pixels d'ecran. Un profil par jeu, ecrit avec le
/// releve du labo (les formules viennent du profil d'atlas de ScoreLab, 19-20/11). Calcul pur.
/// </summary>
public sealed record CanalProfilAffichage(
    string Id,
    int EcranL,
    int EcranH,
    double EchelleX, double DecalageX, bool XDepuisY,
    double EchelleY, double DecalageY,
    double CameraEchelle,
    IReadOnlyDictionary<int, int> Classes,
    IReadOnlyList<int> Caches,
    IReadOnlyList<string> Roms)
{
    /// <summary>
    /// DoDonPachi DaiOuJou Black Label (ddpdojblka, FBNeo) : ecran portrait 224 x 448 ; les mots de la table d'affichage sont en 8.8 et
    /// tournes : x_ecran = 4 x mot(+4) / 256 + 3, y_ecran = 446 - 4 x mot(+2) / 256. Le vaisseau suit la meme rotation (0x103E6 pour x,
    /// 0x103E4 pour y). La camera : s16 / 64 pixels. Classes proposees par le labo (l leger, h lourd, s special, g generique).
    /// </summary>
    public static readonly CanalProfilAffichage DaiOuJou = new(
        "daioujou", 224, 448,
        EchelleX: 4.0 / 256.0, DecalageX: 3, XDepuisY: true,
        EchelleY: -4.0 / 256.0, DecalageY: 446,
        CameraEchelle: 1.0 / 64.0,
        Classes: ClassesDaiOuJou(),
        Caches: [0xA200, 0xA201],
        Roms: ["ddpdojblka", "ddpdojblk", "ddpdoj", "ddpdojh"]);

    private static Dictionary<int, int> ClassesDaiOuJou()
    {
        var d = new Dictionary<int, int>();
        foreach (var t in new[] { 0x1101, 0x1001, 0x9500, 0x2500, 0x2B00, 0x2900, 0x0501 }) d[t] = 0;
        foreach (var t in new[] { 0x8200, 0x8B01, 0x8A01, 0x8000, 0x8500, 0x8801, 0x8D01, 0x8F01, 0x9401, 0x9701, 0x9201, 0x9601, 0x9301, 0x8401, 0x9001, 0x9101, 0x8600 }) d[t] = 1;
        foreach (var t in new[] { 0x8901, 0x0D81, 0x8C80 }) d[t] = 2;
        foreach (var t in new[] { 0x0500, 0x0700, 0x0800, 0x0801, 0x0900, 0x0B00, 0x0B01, 0x2700, 0x4D00 }) d[t] = 3;
        return d;
    }

    private static readonly CanalProfilAffichage[] Connus = [DaiOuJou];

    /// <summary>Le profil du jeu lance (nom de ROM), ou null : un jeu sans profil n'a pas de canal publie.</summary>
    public static CanalProfilAffichage? Pour(string? rom)
    {
        if (string.IsNullOrWhiteSpace(rom)) return null;
        foreach (var p in Connus)
            foreach (var r in p.Roms)
                if (string.Equals(r, rom.Trim(), StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    /// <summary>Le profil reconnu par le NOM du .VIEW que le wrapper a charge (repli quand la ROM n'est pas connue).</summary>
    public static CanalProfilAffichage? PourLaVue(string? nomVue)
    {
        if (string.IsNullOrWhiteSpace(nomVue)) return null;
        var n = nomVue.ToLowerInvariant();
        foreach (var p in Connus)
        {
            if (n.Contains(p.Id, StringComparison.Ordinal)) return p;
            foreach (var r in p.Roms) if (n.Contains(r, StringComparison.Ordinal)) return p;
        }

        return null;
    }

    public int ClasseDe(int type) => Classes.TryGetValue(type & 0xFFFF, out var c) ? c : 3;
}

/// <summary>
/// LE CALCUL du message <c>deck.live</c> a partir d'un bloc du canal : pur, sans memoire partagee. Garde le peu d'etat qu'il faut
/// (la vie la plus haute du boss depuis son apparition, le compteur de messages) et le vide a chaque changement d'epoch.
/// </summary>
public sealed class CanalAffichageCalcul
{
    public const int MaxEnnemis = 64;

    private readonly CanalProfilAffichage _profil;
    private readonly CanalDescripteur _d;
    private readonly string _rom;
    private readonly int _iDist, _iCamera, _iShipX, _iShipY, _iStage, _iLives, _iTick, _iCombo, _iComboMax;
    private readonly int _tAff, _tObj, _cDrapeau, _cX, _cY, _cType, _cVie;
    private uint _epoch = uint.MaxValue;
    private long _seq;
    private int _bossMax;
    private int _bossSlot = -1;

    public CanalAffichageCalcul(CanalProfilAffichage profil, CanalDescripteur d, string rom)
    {
        _profil = profil;
        _d = d;
        _rom = rom;
        _iDist = d.Champ("distance");
        _iCamera = d.Champ("camera");
        _iShipX = d.Champ("vaisseau_x");
        _iShipY = d.Champ("vaisseau_y");
        _iStage = d.Champ("niveau");
        _iLives = d.Champ("vies");
        _iTick = d.Champ("temps");
        _iCombo = d.Champ("combo");
        _iComboMax = d.Champ("combo_max");
        _tAff = d.Table("affichage");
        _tObj = d.Table("objets");
        _cDrapeau = _tAff < 0 ? -1 : d.Tables[_tAff].Colonne("drapeau");
        _cX = _tAff < 0 ? -1 : d.Tables[_tAff].Colonne("x");
        _cY = _tAff < 0 ? -1 : d.Tables[_tAff].Colonne("y");
        _cType = _tObj < 0 ? -1 : d.Tables[_tObj].Colonne("type");
        _cVie = _tObj < 0 ? -1 : d.Tables[_tObj].Colonne("vie");
    }

    /// <summary>Ce descripteur porte-t-il de quoi publier quelque chose (au moins la distance ou la table d'affichage) ?</summary>
    public bool Utile => _iDist >= 0 || (_tAff >= 0 && _cX >= 0 && _cY >= 0);

    /// <summary>Le message d'un canal qui se tait.</summary>
    public DeckLive Inactif(string raison) => new() { Active = false, Reason = raison, Rom = _rom, Seq = ++_seq, ScreenW = _profil.EcranL, ScreenH = _profil.EcranH };

    public DeckLive Convertir(CanalBloc b)
    {
        if (b.Epoch != _epoch)
        {
            _epoch = b.Epoch;
            _bossMax = 0;
            _bossSlot = -1;
        }

        var p = _profil;
        int[]? ship = null;
        if (_iShipX >= 0 && _iShipY >= 0 && (_iLives < 0 || b.Champs[_iLives] != 255))
        {
            // x vient du second mot (rotation du jeu), y du premier : voir CanalProfilAffichage.DaiOuJou
            var sx = (int)Math.Round(b.Champs[_iShipX] * p.EchelleX + p.DecalageX);
            var sy = (int)Math.Round(b.Champs[_iShipY] * p.EchelleY + p.DecalageY);
            ship = [Math.Clamp(sx, 0, p.EcranL), Math.Clamp(sy, 0, p.EcranH)];
        }

        var ennemis = Ennemis(b, out var boss);
        return new DeckLive
        {
            Active = true,
            Rom = _rom,
            Epoch = b.Epoch,
            Image = b.Image,
            Seq = ++_seq,
            ScreenW = p.EcranL,
            ScreenH = p.EcranH,
            Dist = _iDist >= 0 ? b.Champs[_iDist] : -1,
            Camera = _iCamera >= 0 ? Math.Round(b.Champs[_iCamera] * p.CameraEchelle, 2) : 0,
            Stage = _iStage >= 0 ? b.Champs[_iStage] : -1,
            Lives = _iLives >= 0 ? b.Champs[_iLives] : -1,
            Tick = _iTick >= 0 ? b.Champs[_iTick] : -1,
            Ship = ship,
            Enemies = ennemis,
            Boss = boss,
            Combo = _iCombo >= 0 ? [b.Champs[_iCombo], _iComboMax >= 0 ? b.Champs[_iComboMax] : 0] : null,
        };
    }

    /// <summary>
    /// Les ennemis vivants a l'ecran : une case de la table d'affichage dessinee (drapeau non cache) ET occupee dans la table des
    /// objets (type non nul : une case libre n'est pas un ennemi). Les deux tables ont le meme rang, les deux listes sont triees par
    /// emplacement : un seul passage.
    /// </summary>
    private int[] Ennemis(CanalBloc b, out int[]? boss)
    {
        boss = null;
        if (_tAff < 0 || _cX < 0 || _cY < 0) return [];
        var aff = b.Tables[_tAff];
        var obj = _tObj >= 0 ? b.Tables[_tObj] : null;
        var p = _profil;
        var n = Math.Min(aff.Ecrites, MaxEnnemis);
        var sortie = new int[n * 5];
        var k = 0;
        var o = 0;
        var bossVie = -1;
        var bossSlot = -1;
        for (var e = 0; e < aff.Ecrites && k < MaxEnnemis; e++)
        {
            var slot = aff.Emplacement(e);
            if (_cDrapeau >= 0 && IndexOf(p.Caches, aff.Colonne(e, _cDrapeau) & 0xFFFF)) continue;
            var type = -1;
            var vie = 0;
            if (obj is not null)
            {
                while (o < obj.Ecrites && obj.Emplacement(o) < slot) o++;
                if (o >= obj.Ecrites || obj.Emplacement(o) != slot) continue;   // case libre : pas d'ennemi
                if (_cType >= 0) type = obj.Colonne(o, _cType) & 0xFFFF;
                if (_cVie >= 0) vie = obj.Colonne(o, _cVie) & 0xFFFF;
                if (type == 0) continue;
            }

            // x_ecran vient du mot « y » de la table, y_ecran du mot « x » (le jeu est tourne) : voir le profil
            var sx = (int)Math.Round(aff.Colonne(e, _cY) * p.EchelleX + p.DecalageX);
            var sy = (int)Math.Round(aff.Colonne(e, _cX) * p.EchelleY + p.DecalageY);
            var cls = type < 0 ? 3 : p.ClasseDe(type);
            var i = k * 5;
            sortie[i] = slot; sortie[i + 1] = sx; sortie[i + 2] = sy; sortie[i + 3] = cls; sortie[i + 4] = vie;
            k++;
            if (cls == 2 && vie > bossVie) { bossVie = vie; bossSlot = slot; }
        }

        if (bossSlot >= 0)
        {
            if (bossSlot != _bossSlot) { _bossSlot = bossSlot; _bossMax = 0; }
            if (bossVie > _bossMax) _bossMax = bossVie;
            boss = [bossVie, _bossMax];
        }
        else { _bossSlot = -1; _bossMax = 0; }

        if (k != n) Array.Resize(ref sortie, k * 5);
        return sortie;
    }

    private static bool IndexOf(IReadOnlyList<int> l, int v)
    {
        for (var i = 0; i < l.Count; i++) if (l[i] == v) return true;
        return false;
    }

    /// <summary>Le message a-t-il change depuis le precedent publie (hors numero et image) ? Pur, sert a ne publier que sur changement.</summary>
    public static bool Different(DeckLive a, DeckLive b)
    {
        if (a.Active != b.Active || a.Epoch != b.Epoch || a.Dist != b.Dist || a.Stage != b.Stage || a.Lives != b.Lives || a.Camera != b.Camera) return true;
        if (!Egal(a.Ship, b.Ship) || !Egal(a.Boss, b.Boss) || !Egal(a.Combo, b.Combo)) return true;
        return !a.Enemies.AsSpan().SequenceEqual(b.Enemies);
    }

    private static bool Egal(int[]? a, int[]? b) => a is null || b is null ? a is null && b is null : a.AsSpan().SequenceEqual(b);
}
