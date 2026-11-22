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

    /// <summary>La minuterie du combo : <c>[valeur, maximum]</c> si le .VIEW porte le champ combo (maximum : champ combo_max, sinon celui
    /// du profil, 56 pour DaiOuJou), sinon null.</summary>
    public int[]? Combo { get; init; }
}

/// <summary>Un axe d'ecran : pixel = brut x echelle + decalage, arrondi. Calcul pur.</summary>
public readonly record struct CanalAxe(double Echelle, double Decalage)
{
    public int Pixel(int brut) => (int)Math.Round(brut * Echelle + Decalage);
}

/// <summary>
/// Le sens du bloc d'un jeu : comment les valeurs brutes du canal deviennent des pixels d'ecran. Un profil par jeu, ecrit avec le
/// labo du jeu qui a prouve son .VIEW (noms des champs et des colonnes, formules, filtre des ennemis). Calcul pur.
/// </summary>
/// <param name="ColonneX">La colonne de la table d'affichage qui donne le x d'ECRAN (le jeu peut etre tourne : nommee par le .VIEW).</param>
/// <param name="ColonneY">La colonne de la table d'affichage qui donne le y d'ECRAN.</param>
/// <param name="MasqueDessine">Le bit du drapeau d'affichage qui dit « dessine » (0 : drapeau non controle).</param>
/// <param name="VieRequise">Un ennemi doit avoir une vie non nulle (vie 0 : decor ou objet, jamais publie).</param>
/// <param name="CoordonneesSignees">Les mots de position de la table d'affichage sont des s16 (un objet juste hors de l'ecran, a
/// gauche ou en bas, porte un mot negatif lu 65 xxx en u16) ; un ennemi hors de l'ecran n'est jamais publie.</param>
/// <param name="ComboMax">Le maximum de la jauge du combo quand le .VIEW n'a pas de champ <c>combo_max</c> (0 : inconnu).</param>
public sealed record CanalProfilAffichage(
    string Id,
    int EcranL,
    int EcranH,
    CanalAxe VaisseauX, CanalAxe VaisseauY,
    string ColonneX, string ColonneY,
    CanalAxe EnnemiX, CanalAxe EnnemiY,
    int MasqueDessine,
    bool VieRequise,
    bool CoordonneesSignees,
    int ComboMax,
    double CameraEchelle,
    IReadOnlyDictionary<int, int> Classes,
    IReadOnlyList<string> Roms)
{
    /// <summary>
    /// DoDonPachi DaiOuJou Black Label (ddpdojblka, FBNeo 800f25e9) : ecran portrait 224 x 448, y vers le bas. Formules du labo du
    /// .VIEW (22/11, <c>dodonpachi-daioujou.VIEW</c>, preuves dans RAPPORT_LABO_VIEW) : vaisseau x = vaisseau_x / 64, y = 456 -
    /// vaisseau_y / 64 (gabarit sur 247 captures d'une partie reelle) ; ennemi x = ax / 64, y = 447 - ay / 64 (ax = mot +4, ay = mot
    /// +2 de la table d'affichage, surimpressions des niveaux 1 et 2). Le « +3 » de l'ancien profil d'atlas n'est pas repris : aucune
    /// mesure ne le prouve. Ennemi = case DESSINEE (drapeau &amp; 0x2000) ET occupee (type non nul) ET de vie non nulle (vie 0 :
    /// ventilateurs, pieces du decor). La jauge du combo (champ combo, 0 a 56) a un maximum constant de 56. Camera : s16 / 64 pixels.
    /// Classes proposees par le labo (l leger, h lourd, s special, g generique), a valider par le user.
    /// </summary>
    public static readonly CanalProfilAffichage DaiOuJou = new(
        "daioujou", 224, 448,
        VaisseauX: new(1.0 / 64.0, 0), VaisseauY: new(-1.0 / 64.0, 456),
        ColonneX: "ax", ColonneY: "ay",
        EnnemiX: new(1.0 / 64.0, 0), EnnemiY: new(-1.0 / 64.0, 447),
        MasqueDessine: 0x2000,
        VieRequise: true,
        CoordonneesSignees: true,
        ComboMax: 56,
        CameraEchelle: 1.0 / 64.0,
        Classes: ClassesDaiOuJou(),
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
    private readonly int _iDist, _iCamera, _iShipX, _iShipY, _iStage, _iLives, _iTick, _iCombo, _iComboMax, _iMort;
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
        _iMort = d.Champ("mort_anim");
        _iComboMax = d.Champ("combo_max");
        _tAff = d.Table("affichage");
        _tObj = d.Table("objets");
        _cDrapeau = _tAff < 0 ? -1 : d.Tables[_tAff].Colonne("drapeau");
        _cX = _tAff < 0 ? -1 : d.Tables[_tAff].Colonne(profil.ColonneX);
        _cY = _tAff < 0 ? -1 : d.Tables[_tAff].Colonne(profil.ColonneY);
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
        // pas de vaisseau hors partie (vies 255), pendant l'animation de mort (mort_anim > 0, 31 images par mort au labo) ni a 0,0
        if (_iShipX >= 0 && _iShipY >= 0 && (_iLives < 0 || b.Champs[_iLives] != 255)
            && (_iMort < 0 || b.Champs[_iMort] == 0) && (b.Champs[_iShipX] != 0 || b.Champs[_iShipY] != 0))
        {
            // formules du vaisseau du profil (DaiOuJou : x = vaisseau_x / 64, y = 456 - vaisseau_y / 64)
            var sx = p.VaisseauX.Pixel(b.Champs[_iShipX]);
            var sy = p.VaisseauY.Pixel(b.Champs[_iShipY]);
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
            Combo = _iCombo >= 0 ? [b.Champs[_iCombo], _iComboMax >= 0 ? b.Champs[_iComboMax] : p.ComboMax] : null,
        };
    }

    /// <summary>
    /// Les ennemis vivants a l'ecran : une case de la table d'affichage DESSINEE (drapeau portant le masque du profil) ET occupee
    /// dans la table des objets (type non nul : une case libre n'est pas un ennemi) ET, si le profil le veut, de vie non nulle (vie
    /// 0 : decor, pieces) ET a l'ecran. Les deux tables ont le meme rang, les deux listes sont triees par emplacement : un seul passage. Les
    /// autres tables du .VIEW (les tirs de DaiOuJou) ne sont pas lues.
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
            if (_cDrapeau >= 0 && p.MasqueDessine != 0 && (aff.Colonne(e, _cDrapeau) & p.MasqueDessine) == 0) continue;
            var type = -1;
            var vie = 0;
            if (obj is not null)
            {
                while (o < obj.Ecrites && obj.Emplacement(o) < slot) o++;
                if (o >= obj.Ecrites || obj.Emplacement(o) != slot) continue;   // case libre : pas d'ennemi
                if (_cType >= 0) type = obj.Colonne(o, _cType) & 0xFFFF;
                if (_cVie >= 0) vie = obj.Colonne(o, _cVie) & 0xFFFF;
                if (type == 0) continue;
                if (p.VieRequise && _cVie >= 0 && vie == 0) continue;   // decor ou objet : jamais un ennemi
            }

            // colonnes nommees par le .VIEW selon l'axe d'ECRAN (DaiOuJou : ax = mot +4 pour x, ay = mot +2 pour y, jeu tourne)
            var bx = aff.Colonne(e, _cX);
            var by = aff.Colonne(e, _cY);
            if (p.CoordonneesSignees) { bx = (short)bx; by = (short)by; }
            var sx = p.EnnemiX.Pixel(bx);
            var sy = p.EnnemiY.Pixel(by);
            if (sx < 0 || sx > p.EcranL || sy < 0 || sy > p.EcranH) continue;   // hors de l'ecran : rien a dessiner
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

    /// <summary>Le message a-t-il change depuis le precedent publie (hors numero et image) ? Pur, sert a ne publier que sur changement.</summary>
    public static bool Different(DeckLive a, DeckLive b)
    {
        if (a.Active != b.Active || a.Epoch != b.Epoch || a.Dist != b.Dist || a.Stage != b.Stage || a.Lives != b.Lives || a.Camera != b.Camera) return true;
        if (!Egal(a.Ship, b.Ship) || !Egal(a.Boss, b.Boss) || !Egal(a.Combo, b.Combo)) return true;
        return !a.Enemies.AsSpan().SequenceEqual(b.Enemies);
    }

    private static bool Egal(int[]? a, int[]? b) => a is null || b is null ? a is null && b is null : a.AsSpan().SequenceEqual(b);
}
