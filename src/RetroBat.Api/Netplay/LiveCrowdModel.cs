namespace RetroBat.Api.Netplay;

/// <summary>
/// La FOULE des spectateurs d'un direct : qui est la, qui est EN SCENE, et comment ils bougent.
/// Aucun dessin ici : le HUD lit un instantane.
///
/// UNE RANGEE d'avatars de 64 pixels au bas de l'ecran. Tous les presents ne tiennent pas en scene :
/// le HUD fixe combien il peut en dessiner d'apres ce que la machine encaisse, et la scene choisit
/// qui y est. Quelqu'un qui reagit y entre, a la place de celui qui s'est tu le plus longtemps.
/// Entrees et sorties se font par les BORDS, en marchant. Un leger decalage vertical donne la
/// profondeur, et c'est lui qui fait passer un avatar devant ou derriere un autre.
///
/// Les deplacements sont RARES par construction. La fenetre du HUD ne recompose a vingt-cinq images
/// par seconde que lorsque quelque chose bouge, et c'est ce compositing plein ecran qui a deja hache
/// le son d'une borne modeste. Une foule qui flane en permanence le maintiendrait sans arret : on
/// fait donc flaner UN spectateur a la fois, de loin en loin, et la scene est immobile le reste du
/// temps.
///
/// Tout le hasard est DETERMINISTE, tire d'une graine et d'un hachage de l'acteur : un test peut
/// rejouer une scene a la milliseconde pres.
/// </summary>
public sealed class LiveCrowdModel
{
    /// <summary>Le cote d'un avatar, en pixels de planche. Le HUD l'agrandit d'un facteur ENTIER.</summary>
    public const int TailleSprite = 64;

    /// <summary>Les presents suivis, comme la plateforme : au-dela, c'est le nombre qui parle.</summary>
    public const int Capacite = 300;

    /// <summary>Combien en scene : le HUD ajuste entre ces bornes d'apres son temps de dessin.</summary>
    public const int CapaciteMin = 8;
    public const int CapaciteMax = 30;
    public const int CapaciteDepart = 24;

    /// <summary>La profondeur : un decalage vertical de 0 a 6 pixels. Plus bas sur l'ecran = plus pres = devant.</summary>
    public const int Profondeur = 6;

    public const int DureeVol = 1400;
    public const int DureeSaut = 420;

    /// <summary>Entre deux flaneries, de huit a vingt secondes.</summary>
    public const int FlanerieMin = 8000;
    public const int FlanerieMax = 20000;

    private const int PlafondEtiquettes = 8;
    private const int DureeHaute = 1800;
    private const int DureePlancher = 400;
    private const int PlafondVols = 60;

    /// <summary>Qui a reagi il y a moins de huit secondes ne cede pas sa place.</summary>
    private const int ReactionProtegee = 8000;

    /// <summary>Un avatar inconnu se redemande au plus toutes les trente secondes.</summary>
    private const int RedemandeAvatar = 30000;

    /// <summary>
    /// Le temps qu'on laisse a une planche d'arriver avant de faire entrer son spectateur en
    /// silhouette.
    ///
    /// Il attend HORS SCENE, donc invisible : on ne dessine personne sur place pour le
    /// transformer ensuite, on le voit ARRIVER avec son avatar. Passe ce delai, il entre quand
    /// meme : un spectateur qui n'a jamais genere d'avatar ne doit pas rester invisible.
    /// </summary>
    private const int AttenteAvatar = 12000;

    /// <summary>Une entree a la fois, espacee : vingt-quatre arrivees d'un coup seraient une
    /// bousculade, et on ne verrait justement plus personne arriver.</summary>
    private const int EcartEntrees = 400;

    /// <summary>Pas plus de huit pas rattrapes d'un coup : apres une pause du HUD, personne ne se teleporte.</summary>
    private const int RattrapageMax = 8;

    public enum Vue { Face = 0, Dos = 1, Droite = 2, Gauche = 3 }

    /// <summary>
    /// Comment une famille se deplace : pixels et millisecondes par pas, rebond pendant la marche,
    /// si elle avance de face (pas chasses), et jusqu'ou elle flane.
    /// </summary>
    public sealed record Allure(int PixelsParPas, int MsParPas, int Rebond, bool DeFace, int Amplitude);

    /// <summary>Les treize familles de l'Atelier 64, chacune avec sa facon de bouger.</summary>
    public static readonly IReadOnlyDictionary<string, Allure> Allures = new Dictionary<string, Allure>(StringComparer.Ordinal)
    {
        ["run-and-gun"] = new(3, 70, 0, false, 40),   // des rafales : court vite, s'arrete net
        ["shmup"] = new(2, 50, 0, false, 36),         // glisse sans rebond, comme un vaisseau
        ["beatemup"] = new(2, 110, 1, false, 30),     // roule des epaules
        ["fighting"] = new(2, 90, 0, true, 16),       // pas chasses, en garde, face a l'adversaire
        ["racing"] = new(4, 60, 0, false, 48),        // le plus rapide, et qui va le plus loin
        ["platformer"] = new(2, 80, 2, false, 32),    // sautille
        ["puzzle"] = new(1, 120, 0, false, 14),       // bouge a peine : il reflechit
        ["maze"] = new(2, 70, 1, false, 40),          // de longues lignes droites
        ["sports"] = new(3, 80, 1, true, 36),         // trottine en regardant le jeu
        ["pinball"] = new(2, 60, 3, false, 28),       // rebondit comme une bille
        ["horror"] = new(1, 170, 0, false, 20),       // se traine
        ["rpg"] = new(1, 110, 0, false, 24),          // marche pose, en voyageur
        ["shooter"] = new(2, 90, 0, true, 24),        // pas de cote, a couvert
    };

    private static readonly Allure AllureParDefaut = new(1, 110, 0, false, 24);

    public static Allure AllureDe(string? famille)
        => famille is not null && Allures.TryGetValue(famille, out var a) ? a : AllureParDefaut;

    /// <summary>L'avatar d'un spectateur tel que la plateforme le donne. `Planche` est l'empreinte de
    /// la planche a dessiner, absente tant que personne ne l'a declaree.</summary>
    public sealed record Avatar(string Pseudo, string Famille, int Variation, string? Planche, string? Generateur);

    /// <summary>
    /// Un avatar a dessiner. X est le bord gauche en pixels de scene, Y le decalage de profondeur,
    /// `Image` 0 au repos et 1 a 4 en marche, `Hauteur` ce qui le souleve (saut, rebond).
    /// </summary>
    public sealed record Silhouette(string Acteur, int X, int Y, Vue Vue, int Image, int Hauteur, bool Sortant, Avatar? Avatar);

    /// <summary>Un emoji en vol, parti du centre de son lanceur. `Depuis` vaut -1 tant que le HUD ne l'a
    /// pas encore vu : il part au premier releve.</summary>
    public sealed record Vol(string Famille, int Niveau, int X, long Depuis);

    /// <summary>Un nom affiche au-dessus de son avatar, qu'il suit s'il bouge.</summary>
    public sealed record Etiquette(string Texte, string Acteur, long Jusqua);

    /// <summary>`Fin` vaut 0 tant que le HUD ne l'a pas encore vu : le saut part au premier releve.</summary>
    public sealed record Saut(long Fin, int Hauteur);

    /// <summary>Ce qu'il faut pour dessiner une image, pris sous verrou une seule fois. La scene est
    /// deja dans l'ordre de dessin : du fond vers le devant.</summary>
    public sealed record Instantane(
        IReadOnlyList<Silhouette> Scene,
        int Total,
        IReadOnlyList<Vol> Vols,
        IReadOnlyList<Etiquette> Etiquettes,
        bool Bouge);

    private sealed class Figurant
    {
        public string Acteur = "";
        public uint Graine;
        public int X, Cible, Y;
        public bool Place, Nouveau, AReplacer, Sortant, Supprime, FinDeFlanerie;
        public int? CentreVise;
        public long ProchainPas, DerniereActivite, RegardJusqua, Arrivee;
        public int Pas;
        public bool Marche => Place && X != Cible;
    }

    private readonly object _gate = new();
    private string[] _presents = Array.Empty<string>();
    private HashSet<string> _presentsSet = new(StringComparer.Ordinal);
    private int _total;
    private readonly List<Figurant> _scene = new();
    private readonly Dictionary<string, Avatar> _avatars = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _demandesAvatar = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _reactions = new(StringComparer.Ordinal);
    private readonly List<Vol> _vols = new();
    private readonly List<Etiquette> _etiquettes = new();
    private readonly Dictionary<string, Saut> _sauts = new(StringComparer.Ordinal);

    /// <summary>
    /// Les reactions DEJA montrees, par identifiant.
    ///
    /// Sa propre reaction s'anime tout de suite, des la reponse de la plateforme ; le flux la ramene
    /// ensuite au battement suivant. Sans cette memoire, on la verrait DEUX fois.
    /// </summary>
    private readonly HashSet<long> _vus = new();

    private int _capacite = CapaciteDepart;
    private int _largeur = 1920;
    private bool _aRecomposer;
    // Null avant la premiere entree. Pas long.MinValue : `maintenant - long.MinValue` deborde en
    // negatif, l'espacement paraissait toujours trop court, et plus personne n'entrait jamais.
    private long? _derniereEntree;
    private long _prochaineFlanerie = -1;
    private uint _alea;

    public LiveCrowdModel() : this(0x2545F491u)
    {
    }

    public LiveCrowdModel(uint graine)
    {
        _alea = graine == 0 ? 1u : graine;
    }

    // ── Les regles, pures ──────────────────────────────────────────────────

    /// <summary>
    /// La duree d'affichage d'une etiquette, d'apres le nombre deja a l'ecran.
    ///
    /// Elle DECROIT : plus ca reagit, plus les noms s'effacent vite, donc plus de monde est nomme et
    /// personne ne monopolise une place. Sous le plancher on rend zero : l'oeil n'attrape pas un mot
    /// en moins de quatre dixiemes de seconde.
    /// </summary>
    public static int DureeEtiquette(int dejaAffichees)
    {
        if (dejaAffichees >= PlafondEtiquettes)
        {
            return 0;
        }
        var duree = DureeHaute - (DureeHaute - DureePlancher) * (dejaAffichees / (double) PlafondEtiquettes);
        return duree < DureePlancher ? 0 : (int) Math.Round(duree);
    }

    /// <summary>
    /// La hauteur du saut, d'apres l'INTENSITE du geste (trois crans) : 8, 16 ou 24 pixels pour un
    /// avatar de 64. C'est la jauge de charge qui se retrouve dans le saut, pas le budget depense.
    /// </summary>
    public static int HauteurSaut(int niveau) => 8 * Math.Clamp(niveau, 1, 3);

    /// <summary>La teinte de la silhouette de remplacement, 0 a 359, tant que la planche n'est pas la.</summary>
    public static int Teinte(string acteur)
    {
        var h = 0;
        foreach (var c in acteur ?? "")
        {
            h = (h * 31 + c) % 360;
        }
        return h;
    }

    /// <summary>FNV-1a : stable d'une execution a l'autre, contrairement a string.GetHashCode.</summary>
    public static uint Hachage(string s)
    {
        var h = 2166136261u;
        foreach (var c in s ?? "")
        {
            h ^= c;
            h *= 16777619u;
        }
        return h;
    }

    /// <summary>
    /// Le dessin de REMPLACEMENT d'un avatar dont la planche n'est pas encore arrivee : huit lignes de
    /// huit pixels, qui se divisent proprement dans soixante-quatre (blocs de huit).
    /// </summary>
    public static readonly byte[] Gabarit =
    {
        0b00100100,   // antennes
        0b01111110,
        0b11111111,
        0b11011011,   // les yeux
        0b11111111,
        0b01111110,
        0b01100110,   // les pattes
        0b01100110,
    };

    /// <summary>
    /// L'HORLOGE de la foule, et la seule : celle du HUD (`Environment.TickCount64`). Deux bases
    /// differentes se sont deja rencontrees, et le resultat etait silencieux : les emoji ne
    /// montaient pas, personne ne sautait, et les pseudos ne disparaissaient jamais.
    /// </summary>
    public static long Maintenant() => Environment.TickCount64;

    // ── La presence ────────────────────────────────────────────────────────

    public void Poser(IReadOnlyList<string> acteurs, int total) => Poser(acteurs, total, Maintenant());

    /// <summary>Pose la liste des presents, et recompose la scene tout de suite.</summary>
    public void Poser(IReadOnlyList<string> acteurs, int total, long maintenant)
    {
        lock (_gate)
        {
            var tries = acteurs.Where(a => !string.IsNullOrEmpty(a)).Distinct(StringComparer.Ordinal).ToArray();
            Array.Sort(tries, StringComparer.Ordinal);
            if (tries.Length > Capacite)
            {
                tries = tries[..Capacite];
            }
            _presents = tries;
            _presentsSet = new HashSet<string>(tries, StringComparer.Ordinal);
            _total = total;
            Recomposer(maintenant);
        }
    }

    public void PoserAvatars(IReadOnlyDictionary<string, Avatar> avatars)
    {
        lock (_gate)
        {
            foreach (var (acteur, avatar) in avatars)
            {
                _avatars[acteur] = avatar;
            }
        }
    }

    /// <summary>
    /// Les acteurs EN SCENE dont on ne connait pas encore l'avatar. Seuls ceux qu'on dessine sont
    /// demandes : trois cents avatars pour en montrer vingt-quatre serait du trafic pour rien. Un
    /// acteur deja demande ne revient qu'apres trente secondes.
    /// </summary>
    public IReadOnlyList<string> AvatarsManquants(int max, long maintenant)
    {
        lock (_gate)
        {
            var sortie = new List<string>();
            foreach (var f in _scene)
            {
                if (sortie.Count >= max)
                {
                    break;
                }
                if (f.Sortant || _avatars.ContainsKey(f.Acteur))
                {
                    continue;
                }
                if (_demandesAvatar.TryGetValue(f.Acteur, out var t) && maintenant - t < RedemandeAvatar)
                {
                    continue;
                }
                _demandesAvatar[f.Acteur] = maintenant;
                sortie.Add(f.Acteur);
            }
            return sortie;
        }
    }

    /// <summary>Les planches des avatars en scene, pour les faire venir si la borne ne les a pas.</summary>
    public IReadOnlyList<Avatar> AvatarsEnScene()
    {
        lock (_gate)
        {
            return _scene.Where(f => !f.Sortant)
                .Select(f => _avatars.GetValueOrDefault(f.Acteur))
                .OfType<Avatar>()
                .ToList();
        }
    }

    /// <summary>Combien le HUD peut en dessiner. Borne, et appliquee au prochain releve.</summary>
    public void FixerCapacite(int combien)
    {
        lock (_gate)
        {
            var c = Math.Clamp(combien, CapaciteMin, CapaciteMax);
            if (c != _capacite)
            {
                _capacite = c;
                _aRecomposer = true;
            }
        }
    }

    public int CapaciteCourante
    {
        get { lock (_gate) { return _capacite; } }
    }

    // ── Les reactions ──────────────────────────────────────────────────────

    /// <summary>Une reaction, horodatee par l'horloge de la foule.</summary>
    public void Reagir(string acteur, string famille, int niveau, string nom)
        => Reagir(acteur, famille, niveau, nom, Maintenant());

    /// <summary>
    /// Une reaction a un instant donne. Hors de scene, son auteur y ENTRE, a la place du plus
    /// silencieux s'il n'y a plus de place : c'est qui s'exprime qu'on doit voir.
    /// </summary>
    public void Reagir(string acteur, string famille, int niveau, string nom, long maintenant)
    {
        if (string.IsNullOrEmpty(acteur) || string.IsNullOrEmpty(famille))
        {
            return;
        }
        lock (_gate)
        {
            if (!_presentsSet.Contains(acteur))
            {
                // Pas dans le dernier releve de presence : on ne dessine rien plutot que n'importe ou.
                return;
            }
            _reactions[acteur] = maintenant;
            if (_aRecomposer)
            {
                Recomposer(maintenant);
            }

            int? place = null;
            var f = _scene.FirstOrDefault(x => x.Acteur == acteur && !x.Sortant);
            if (f is null)
            {
                var enScene = _scene.Where(x => !x.Sortant).ToList();
                if (enScene.Count >= _capacite && enScene.Count > 0)
                {
                    var cede = enScene
                                   .Where(x => maintenant - x.DerniereActivite >= ReactionProtegee)
                                   .OrderBy(x => x.DerniereActivite).ThenBy(x => x.Acteur, StringComparer.Ordinal)
                                   .FirstOrDefault()
                               ?? enScene.OrderBy(x => x.DerniereActivite).ThenBy(x => x.Acteur, StringComparer.Ordinal).First();
                    place = cede.Place ? cede.X + TailleSprite / 2 : null;
                    FaireSortir(cede, maintenant);
                }
                f = Ajouter(acteur, maintenant, place);
            }
            f.DerniereActivite = maintenant;

            // NON AMORCES : le saut et le vol partent au premier releve du HUD, pas maintenant. Au
            // repos, le HUD ne se reveille que toutes les 500 ms, et un saut dure 420 ms : amorce ici,
            // il etait deja FINI quand le HUD le dessinait pour la premiere fois. Constate a l'ecran,
            // les avatars ne sautaient jamais alors que l'emoji et le nom, plus longs, paraissaient.
            _sauts[acteur] = new Saut(0, HauteurSaut(niveau));

            if (_vols.Count < PlafondVols)
            {
                var centre = f.Place ? f.X + TailleSprite / 2 : place ?? _largeur / 2;
                _vols.Add(new Vol(famille, niveau, Math.Clamp(centre, 16, Math.Max(16, _largeur - 16)), -1));
            }

            var duree = DureeEtiquette(_etiquettes.Count);
            if (duree > 0 && !string.IsNullOrWhiteSpace(nom))
            {
                // Le filet : plafond atteint malgre tout, on retire la plus ANCIENNE plutot que de
                // refuser la nouvelle. Sinon la regle punirait qui arrive tard.
                if (_etiquettes.Count >= PlafondEtiquettes)
                {
                    _etiquettes.RemoveAt(0);
                }
                var texte = nom.Length > 12 ? nom[..12] : nom;
                _etiquettes.Add(new Etiquette(texte, acteur, maintenant + duree));
            }
        }
    }

    /// <summary>Note une reaction comme montree. Rend vrai si elle est NOUVELLE.</summary>
    public bool NoterVue(long id)
    {
        if (id <= 0)
        {
            return true;
        }
        lock (_gate)
        {
            if (_vus.Count > 1000)
            {
                _vus.Clear();
            }
            return _vus.Add(id);
        }
    }

    // ── Le releve ──────────────────────────────────────────────────────────

    /// <summary>Fait avancer la scene jusqu'a `maintenant`, retire ce qui est fini, et rend de quoi dessiner.</summary>
    public Instantane Relever(long maintenant, int largeur)
    {
        lock (_gate)
        {
            Avancer(maintenant, largeur);
            Amorcer(maintenant);

            _vols.RemoveAll(v => maintenant - v.Depuis >= DureeVol);
            _etiquettes.RemoveAll(e => e.Jusqua <= maintenant || !_scene.Any(f => f.Place && f.Acteur == e.Acteur));
            foreach (var fini in _sauts.Where(s => s.Value.Fin <= maintenant).Select(s => s.Key).ToArray())
            {
                _sauts.Remove(fini);
            }

            var scene = _scene
                .Where(f => f.Place)
                .OrderBy(f => f.Y).ThenBy(f => f.X).ThenBy(f => f.Acteur, StringComparer.Ordinal)
                .Select(f => new Silhouette(
                    f.Acteur, f.X, f.Y, VueDe(f, maintenant), f.Marche ? 1 + f.Pas % 4 : 0,
                    HauteurDe(f, maintenant), f.Sortant, _avatars.GetValueOrDefault(f.Acteur)))
                .ToArray();

            return new Instantane(scene, _total, _vols.ToArray(), _etiquettes.ToArray(), BougeSansVerrou());
        }
    }

    /// <summary>Y a-t-il quelque chose qui bouge ? Sert au HUD a ne pas repeindre pour rien.</summary>
    public bool Anime
    {
        get { lock (_gate) { return BougeSansVerrou(); } }
    }

    public void Vider()
    {
        lock (_gate)
        {
            _presents = Array.Empty<string>();
            _presentsSet = new HashSet<string>(StringComparer.Ordinal);
            _total = 0;
            _scene.Clear();
            _avatars.Clear();
            _demandesAvatar.Clear();
            _reactions.Clear();
            _vols.Clear();
            _etiquettes.Clear();
            _sauts.Clear();
            _vus.Clear();
            _aRecomposer = false;
            _derniereEntree = null;
            _prochaineFlanerie = -1;
        }
    }

    // ── La mecanique, sous verrou ──────────────────────────────────────────

    /// <summary>Ce qui attend d'etre vu part maintenant : c'est le premier releve qui donne l'heure.</summary>
    private void Amorcer(long maintenant)
    {
        foreach (var (acteur, saut) in _sauts.Where(s => s.Value.Fin == 0).ToArray())
        {
            _sauts[acteur] = saut with { Fin = maintenant + DureeSaut };
        }
        for (var i = 0; i < _vols.Count; i++)
        {
            if (_vols[i].Depuis < 0)
            {
                _vols[i] = _vols[i] with { Depuis = maintenant };
            }
        }
    }

    private bool BougeSansVerrou()
        => _scene.Any(f => f.Marche) || _vols.Count > 0 || _etiquettes.Count > 0 || _sauts.Count > 0;

    private string? FamilleDe(string acteur) => _avatars.TryGetValue(acteur, out var a) ? a.Famille : null;

    /// <summary>
    /// Qui est en scene. Les partis sortent, les surnumeraires les plus silencieux aussi, et les places
    /// libres vont d'abord a ceux qui ont reagi, puis dans l'ordre trie des acteurs.
    /// </summary>
    private void Recomposer(long maintenant)
    {
        foreach (var f in _scene.Where(f => !f.Sortant && !_presentsSet.Contains(f.Acteur)).ToList())
        {
            FaireSortir(f, maintenant);
        }

        var enScene = _scene.Where(f => !f.Sortant).ToList();
        foreach (var f in enScene
                     .OrderBy(f => f.DerniereActivite).ThenBy(f => f.Acteur, StringComparer.Ordinal)
                     .Take(Math.Max(0, enScene.Count - _capacite))
                     .ToList())
        {
            FaireSortir(f, maintenant);
        }

        var libres = _capacite - _scene.Count(f => !f.Sortant);
        if (libres > 0)
        {
            var dejaLa = _scene.Where(f => !f.Sortant).Select(f => f.Acteur).ToHashSet(StringComparer.Ordinal);
            foreach (var a in _presents
                         .Where(a => !dejaLa.Contains(a))
                         .OrderByDescending(a => _reactions.TryGetValue(a, out var t) ? t : long.MinValue)
                         .ThenBy(a => a, StringComparer.Ordinal)
                         .Take(libres)
                         .ToList())
            {
                Ajouter(a, maintenant, null);
            }
        }

        _scene.RemoveAll(f => f.Supprime);
        _aRecomposer = false;
    }

    private Figurant Ajouter(string acteur, long maintenant, int? centre)
    {
        // Un acteur en train de sortir qui revient fait demi-tour, au lieu d'exister en deux exemplaires.
        var revenant = _scene.FirstOrDefault(f => f.Acteur == acteur);
        if (revenant is not null)
        {
            revenant.Sortant = false;
            revenant.Supprime = false;
            revenant.DerniereActivite = maintenant;
            revenant.CentreVise = centre;
            revenant.AReplacer = true;
            return revenant;
        }

        var graine = Hachage(acteur);
        var f = new Figurant
        {
            Acteur = acteur,
            Graine = graine,
            Y = (int) (graine % (uint) (Profondeur + 1)),
            DerniereActivite = maintenant,
            Arrivee = maintenant,
            CentreVise = centre,
            AReplacer = true,
            // TOUT LE MONDE entre par un bord, y compris au debut d'un direct : on ne dessine
            // personne sur la scene.
            Nouveau = true,
        };
        _scene.Add(f);
        return f;
    }

    private void FaireSortir(Figurant f, long maintenant)
    {
        f.AReplacer = false;
        f.CentreVise = null;
        if (!f.Place)
        {
            // Jamais dessine : il disparait sans traverser l'ecran.
            f.Sortant = true;
            f.Supprime = true;
            return;
        }
        f.Sortant = true;
        f.RegardJusqua = 0;
        // Par le bord le plus proche.
        f.Cible = f.X + TailleSprite / 2 < _largeur / 2 ? -TailleSprite - 2 : _largeur + 2;
        f.ProchainPas = Math.Max(f.ProchainPas, maintenant);
    }

    private void Avancer(long maintenant, int largeur)
    {
        largeur = Math.Max(TailleSprite * 2, largeur);
        if (largeur != _largeur)
        {
            foreach (var f in _scene.Where(f => f.Place))
            {
                f.X = (int) ((long) f.X * largeur / _largeur);
                f.Cible = (int) ((long) f.Cible * largeur / _largeur);
            }
            _largeur = largeur;
        }

        if (_aRecomposer)
        {
            Recomposer(maintenant);
        }
        Placer(maintenant);
        Flaner(maintenant);

        foreach (var f in _scene)
        {
            if (!f.Marche)
            {
                continue;
            }
            var allure = AllureDe(FamilleDe(f.Acteur));
            // On entre et on sort en courant : c'est un passage, pas une flanerie.
            var pixels = allure.PixelsParPas * (f.Sortant || f.Nouveau ? 2 : 1);
            if (maintenant - f.ProchainPas > allure.MsParPas * RattrapageMax)
            {
                f.ProchainPas = maintenant - allure.MsParPas * RattrapageMax;
            }

            var pas = 0;
            while (f.Marche && f.ProchainPas <= maintenant && pas < RattrapageMax)
            {
                var d = f.Cible - f.X;
                f.X += Math.Sign(d) * Math.Min(Math.Abs(d), pixels);
                f.Pas++;
                f.ProchainPas += allure.MsParPas;
                pas++;
            }

            if (!f.Marche && !f.Sortant)
            {
                f.Nouveau = false;
                if (f.FinDeFlanerie)
                {
                    f.FinDeFlanerie = false;
                    // Une fois sur trois, il se retourne un moment vers la partie.
                    if (Suivant() % 3 == 0)
                    {
                        f.RegardJusqua = maintenant + Tirer(2000, 4000);
                    }
                }
            }
        }

        _scene.RemoveAll(f => f.Supprime || (f.Sortant && !f.Marche));
    }

    private void Placer(long maintenant)
    {
        var aPlacer = _scene.Where(f => f.AReplacer).ToList();
        if (aPlacer.Count == 0)
        {
            return;
        }

        // Les deja places gardent leur place : un changement de capacite ou de largeur ne fait pas
        // recommencer une entree.
        foreach (var f in aPlacer.Where(f => f.Place).ToList())
        {
            f.Cible = CibleDe(f);
            f.ProchainPas = maintenant;
            f.AReplacer = false;
            f.CentreVise = null;
        }

        // Les entrants, dans l'ordre d'arrivee, un a la fois : ceux qui attendent encore leur
        // planche patientent hors scene, et on les voit arriver avec leur avatar.
        foreach (var f in aPlacer.Where(f => !f.Place).OrderBy(f => f.Arrivee).ThenBy(f => f.Acteur, StringComparer.Ordinal))
        {
            if (_derniereEntree is { } derniere && maintenant - derniere < EcartEntrees)
            {
                break;
            }
            if (!PretAEntrer(f, maintenant))
            {
                continue;
            }

            var cible = CibleDe(f);
            // Par le bord le plus proche de sa place : la moitie gauche entre par la gauche.
            f.X = cible + TailleSprite / 2 < _largeur / 2 ? -TailleSprite : _largeur;
            f.Place = true;
            f.Cible = cible;
            f.ProchainPas = maintenant;
            f.AReplacer = false;
            f.CentreVise = null;
            _derniereEntree = maintenant;
        }
    }

    /// <summary>La place visee d'un figurant : celle qu'on lui a donnee, sinon le plus grand trou.</summary>
    private int CibleDe(Figurant f)
    {
        var centre = f.CentreVise ?? CentreLibre(f);
        return Math.Clamp(centre - TailleSprite / 2, 0, Math.Max(0, _largeur - TailleSprite));
    }

    /// <summary>
    /// Sa planche est-elle la, ou a-t-on assez attendu ?
    ///
    /// Tant qu'elle n'est pas la, il reste hors scene : c'est ce qui evite de dessiner une
    /// silhouette qui se transformerait sous les yeux du spectateur.
    /// </summary>
    private bool PretAEntrer(Figurant f, long maintenant)
        => (_avatars.TryGetValue(f.Acteur, out var a) && a.Planche is { Length: 64 })
           || maintenant - f.Arrivee >= AttenteAvatar;

    /// <summary>Le milieu du plus grand trou de la scene, decale de l'ecart propre a l'acteur.</summary>
    private int CentreLibre(Figurant nouveau)
    {
        var bornes = new List<int> { 0 };
        bornes.AddRange(_scene
            .Where(x => x != nouveau && !x.Sortant && x.Place && !x.AReplacer)
            .Select(x => x.Cible + TailleSprite / 2)
            .OrderBy(c => c));
        bornes.Add(_largeur);

        int a = 0, b = _largeur, ecart = -1;
        for (var i = 0; i + 1 < bornes.Count; i++)
        {
            var e = bornes[i + 1] - bornes[i];
            if (e > ecart)
            {
                ecart = e;
                a = bornes[i];
                b = bornes[i + 1];
            }
        }
        var jeu = Math.Max(0, ecart / 6);
        var decalage = jeu == 0 ? 0 : (int) (nouveau.Graine % (uint) (2 * jeu + 1)) - jeu;
        return (a + b) / 2 + decalage;
    }

    /// <summary>
    /// Une flanerie de temps en temps, et UNE SEULE a la fois. Si quelqu'un marche deja (une entree,
    /// une sortie), on attend : c'est ce qui garde la scene immobile la plupart du temps.
    /// </summary>
    private void Flaner(long maintenant)
    {
        if (_prochaineFlanerie < 0)
        {
            _prochaineFlanerie = maintenant + Tirer(FlanerieMin, FlanerieMax);
            return;
        }
        if (maintenant < _prochaineFlanerie)
        {
            return;
        }
        if (_scene.Any(f => f.Marche))
        {
            _prochaineFlanerie = maintenant + 1000;
            return;
        }

        _prochaineFlanerie = maintenant + Tirer(FlanerieMin, FlanerieMax);
        var candidats = _scene.Where(f => f.Place && !f.Sortant).ToList();
        if (candidats.Count == 0)
        {
            return;
        }

        var f = candidats[(int) (Suivant() % (uint) candidats.Count)];
        var allure = AllureDe(FamilleDe(f.Acteur));
        var ecart = Tirer(allure.Amplitude / 2, allure.Amplitude) * ((Suivant() & 1) == 0 ? -1 : 1);
        var cible = Math.Clamp(f.X + ecart, 0, Math.Max(0, _largeur - TailleSprite));
        if (cible == f.X)
        {
            cible = Math.Clamp(f.X - ecart, 0, Math.Max(0, _largeur - TailleSprite));
        }
        f.Cible = cible;
        f.ProchainPas = maintenant;
        f.RegardJusqua = 0;
        f.FinDeFlanerie = true;
    }

    private Vue VueDe(Figurant f, long maintenant)
    {
        if (f.Marche)
        {
            // Les pas chasses restent de face, sauf pour entrer ou sortir : on ne traverse pas un
            // ecran en crabe.
            if (AllureDe(FamilleDe(f.Acteur)).DeFace && !f.Sortant && !f.Nouveau)
            {
                return Vue.Face;
            }
            return f.Cible < f.X ? Vue.Gauche : Vue.Droite;
        }
        return maintenant < f.RegardJusqua ? Vue.Dos : Vue.Face;
    }

    private int HauteurDe(Figurant f, long maintenant)
    {
        var h = 0;
        if (_sauts.TryGetValue(f.Acteur, out var s) && s.Fin > maintenant)
        {
            var avancement = 1.0 - (s.Fin - maintenant) / (double) DureeSaut;
            // ARRONDI au pixel : une hauteur fractionnaire ferait vibrer les bords d'une image a l'autre.
            h = (int) Math.Round(Math.Sin(Math.Clamp(avancement, 0, 1) * Math.PI) * s.Hauteur);
        }
        if (f.Marche && f.Pas % 2 == 1)
        {
            h += AllureDe(FamilleDe(f.Acteur)).Rebond;
        }
        return h;
    }

    private uint Suivant()
    {
        var x = _alea;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _alea = x;
        return x;
    }

    private int Tirer(int min, int max) => max <= min ? min : min + (int) (Suivant() % (uint) (max - min + 1));
}
