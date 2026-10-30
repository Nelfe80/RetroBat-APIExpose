using System.Globalization;

namespace RetroBat.Api.Speedrun;

public enum EtatTentative { Preparee, Armee, EnCourse, ArriveeProvisoire, Finie, Ratee, Annulee, Abandonnee }

/// <summary>
/// Un evenement du .MEM emis par le wrapper (ou le pont Lua de MAME) : l'action de la ligne, son adresse,
/// la valeur lue et la frame de la borne. <c>HorlogeMs</c> est l'horloge monotone de la borne a la reception,
/// pour reconnaitre une pause de RetroArch ou du PC (les frames s'arretent, pas l'horloge).
/// </summary>
public sealed record Signal(long Frame, string Action, long Adresse, int Valeur, long? HorlogeMs = null)
{
    /// <summary>Depuis la ligne du wrapper, qui ecrit l'adresse sur six chiffres (0x00F00A pour 0XF00A du .MEM).</summary>
    public static Signal Depuis(long frame, string action, string adresse, int valeur, long? horlogeMs = null)
        => new(frame, action, Hex.Lire(adresse), valeur, horlogeMs);
}

/// <summary>Un chrono mesure : frames de depart et d'arrivee, duree une fois les plages exclues retirees.</summary>
public sealed record MesureChrono(long? Depart, long? Arrivee, long? Frames, long FramesExclues);

/// <summary>Un evenement qui a compte dans la tentative, pour le journal et le passeport.</summary>
public sealed record EvenementCite(string Role, string Action, long Adresse, int Valeur, long Frame);

public sealed record ResultatTentative(
    EtatTentative Etat,
    string? Cause,
    long? FrameDeFin,
    string ChronoOfficiel,
    MesureChrono? Cale,
    MesureChrono? Independant,
    long? FramesOfficielles,
    int? TempsDuJeuSecondes,
    string ControleTempsDuJeu,
    IReadOnlyDictionary<string, int> Compteurs,
    IReadOnlyDictionary<string, int?> ValeursFinales,
    IReadOnlyList<(long De, long A)> PlagesExclues,
    IReadOnlyList<EvenementCite> Journal,
    double Cadence)
{
    /// <summary>Les valeurs du rang, dans l'ordre des criteres du reglement ; null si une valeur manque.</summary>
    public IReadOnlyList<(long? Valeur, bool PlusPetitMeilleur)> ValeursDeRang(Reglement reglement)
        => reglement.Rang.Select(c => (c.Mesure switch
        {
            "frames" => FramesOfficielles,
            var id when Compteurs.TryGetValue(id, out var n) => n,
            var id when ValeursFinales.TryGetValue(id, out var v) => (long?)v,
            _ => null,
        }, c.PlusPetitMeilleur)).ToList();
}

/// <summary>
/// L'EVALUATEUR D'UNE TENTATIVE (prototype Sonic, §9 ; CDC Speedruns v1.4, §6.2). Une machine d'etat
/// alimentee par les evenements du .MEM, sans reseau ni fichier :
///
/// preparee -> armee (conditions de depart vraies) -> en course (depart du chrono officiel) -> arrivee
/// provisoire (« finish ») -> finie (confirmation), ou ratee (echec), annulee (controle de la borne,
/// evenement interdit, pause, capteur coupe) ou abandonnee.
///
/// Les regles du pilote tiennent : un evenement ne compte qu'une fois ; une fin vue avant le depart ne
/// compte jamais ; une valeur jamais lue reste INCONNUE, jamais 0, et un depart qui depend d'elle n'arme
/// pas (<see cref="AdressesInconnues"/> dit quoi relever) ; aucune pause pendant le run (D12). Les deux
/// chronos (cale sur le timer du jeu, independant) se mesurent ensemble (D22), en frames de la borne.
/// </summary>
public sealed class Evaluateur
{
    private readonly Reglement _r;
    private readonly Dictionary<long, int> _valeurs = new();
    private readonly Dictionary<string, int> _compteurs = new(StringComparer.Ordinal);
    private readonly List<EvenementCite> _journal = new();
    private readonly List<(long De, long A)> _plages = new();
    private readonly int[] _progressionEtapes;
    private long? _departCale, _departIndependant;
    private long? _arriveeEvenementCale, _arriveeEvenementIndependant;
    private long? _arriveeProvisoire, _confirmation, _confirmationSansArrivee;
    private long? _plageOuverte;
    private long? _derniereFrame, _derniereHorloge, _frameDeLHorloge;
    private long? _frameDeFin;

    public Evaluateur(Reglement reglement)
    {
        _r = reglement;
        _progressionEtapes = new int[reglement.Etapes.Count];
        foreach (var c in reglement.Compteurs) _compteurs[c.Id] = 0;
    }

    public EtatTentative Etat { get; private set; } = EtatTentative.Preparee;

    public string? Cause { get; private set; }

    public bool EstTerminee => Etat is EtatTentative.Finie or EtatTentative.Ratee or EtatTentative.Annulee or EtatTentative.Abandonnee;

    private bool EnCourse => Etat is EtatTentative.EnCourse or EtatTentative.ArriveeProvisoire;

    /// <summary>
    /// LES ADRESSES A RELEVER DIRECTEMENT (READ_CORE_RAM), parce qu'une ligne « change » n'emet rien tant que
    /// la valeur ne bouge pas. Avant la course : celles du depart (l'acte de Green Hill 1). En course : celles
    /// du temps du jeu et des valeurs d'arrivee (les minutes restent a 0 sur un acte de moins d'une minute) ;
    /// a relever des l'arrivee provisoire, quand le timer du jeu est fige.
    /// </summary>
    public IReadOnlyList<long> AdressesInconnues
        => (EnCourse
                ? _r.TempsDuJeu.Select(l => l.Adresse).Concat(_r.ValeursFinales.Select(v => v.Adresse))
                : _r.AdressesDeDepart())
            .Distinct().Where(a => !_valeurs.ContainsKey(a)).ToList();

    /// <summary>
    /// Une valeur relevee directement dans la memoire du coeur, a une adresse lue par le .MEM. Une ligne
    /// « change » n'emet rien a sa premiere lecture : sans releve, l'acte de Green Hill 1 resterait inconnu.
    /// Un releve ne remplace jamais une valeur deja connue : sur la borne, la lecture part avant que l'evenement
    /// du wrapper n'arrive et revient parfois apres lui, avec une valeur perimee.
    /// </summary>
    public void Relever(long adresse, int valeur)
    {
        if (EstTerminee || _valeurs.ContainsKey(adresse)) return;
        _valeurs[adresse] = valeur;
        if (!EnCourse) MettreAJourArmement();
    }

    public void Recevoir(Signal s) => RecevoirImage(new[] { s });

    /// <summary>
    /// LES EVENEMENTS D'UNE MEME IMAGE, d'un bloc : le wrapper les emet ensemble, dans l'ordre de ses lectures,
    /// qui n'a pas de sens pour le jeu. Les valeurs de l'image sont posees d'abord, puis chaque evenement est
    /// juge avec la valeur d'avant de son adresse. Sonic remet ses minutes a zero dans l'image ou son timer
    /// repart : la condition « minutes a 0 » du depart doit voir la valeur de cette image-la.
    /// </summary>
    public void RecevoirImage(IReadOnlyList<Signal> image)
    {
        if (EstTerminee || image.Count == 0) return;
        var premier = image[0];
        if (EnCourse && PauseRevelee(premier)) { Annuler("pause", premier.Frame); return; }
        _derniereFrame = premier.Frame;
        if (premier.HorlogeMs is { } h) (_derniereHorloge, _frameDeLHorloge) = (h, premier.Frame);

        var avant = new Dictionary<long, int?>();
        foreach (var s in image)
        {
            if (!avant.ContainsKey(s.Adresse)) avant[s.Adresse] = _valeurs.TryGetValue(s.Adresse, out var p) ? p : null;
            _valeurs[s.Adresse] = s.Valeur;
        }

        foreach (var s in image)
        {
            if (EstTerminee) return;
            var precedente = avant[s.Adresse];
            if (!EnCourse)
            {
                MettreAJourArmement();
                if (Etat == EtatTentative.Armee) TenterLeDepart(s, precedente);
                continue;
            }

            Courir(s, precedente);
        }
    }

    /// <summary>Un controle de la borne : save_state_load, rewind, runahead, fast_forward, cheats, netplay, pause_clock_gap.</summary>
    public void Controle(string type, long? frame = null)
    {
        if (EstTerminee) return;
        if (_r.AnnulationsBorne.Contains(type, StringComparer.OrdinalIgnoreCase)) Annuler(type, frame ?? _derniereFrame ?? 0);
    }

    /// <summary>Le wrapper a coupe une ligne trop bavarde (wrapper.watch.muted) : si le reglement la cite, plus rien n'est mesurable.</summary>
    public void CapteurCoupe(long adresse)
    {
        if (EstTerminee) return;
        if (_r.AdressesCitees().Contains(adresse)) Annuler("sensor_muted", _derniereFrame ?? 0);
    }

    public void Abandonner(string cause = "abandoned")
    {
        if (EstTerminee) return;
        Etat = EtatTentative.Abandonnee;
        Cause = cause;
        _frameDeFin = _derniereFrame;
    }

    /// <summary>Le jeu se ferme : une tentative pas encore finie est abandonnee, sans envoi.</summary>
    public void Fermer() => Abandonner("game_closed");

    private void MettreAJourArmement()
    {
        var arme = _r.Depart.All(c => _valeurs.TryGetValue(c.Adresse, out var v) && c.Vraie(v));
        if (arme) { Etat = EtatTentative.Armee; return; }
        Etat = EtatTentative.Preparee;
        // Desarmee avant le depart officiel : un chrono secondaire deja parti ne vaut plus rien.
        _departCale = _departIndependant = null;
    }

    private void TenterLeDepart(Signal s, int? precedente)
    {
        // Avant le depart officiel, un depart secondaire garde sa derniere occurrence : la reprise au poteau
        // apres une mort donne aussi la main au joueur, et ne vaut rien pour la tentative suivante.
        if (_r.Cale is { } cale && Borne(cale.Depart, s, precedente)) _departCale = s.Frame;
        if (_r.Independant is { } ind && Borne(ind.Depart, s, precedente)) _departIndependant = s.Frame;

        var departOfficiel = _r.ChronoOfficiel == Reglement.ChronoIndependant ? _departIndependant : _departCale;
        if (departOfficiel is not { } frame) return;

        Etat = EtatTentative.EnCourse;
        Citer("start", s);
        for (var i = 0; i < _r.Etapes.Count; i++)
        {
            var etape = _r.Etapes[i];
            if (_valeurs.TryGetValue(etape.Adresse, out var v) && etape.Suite.Count > 0 && v == etape.Suite[0])
                _progressionEtapes[i] = 1;
        }
    }

    private void Courir(Signal s, int? precedente)
    {
        // L'arrivee provisoire attend sa confirmation : elle passe avant tout le reste. Quitter l'acte par le
        // Big Ring confirme la fin de Green Hill 1 ; le meme changement d'acte, sans panneau, l'annulerait.
        if (Etat == EtatTentative.ArriveeProvisoire && Confirme(s, precedente)) return;

        if (_r.AnnulationsJeu.Any(c => Correspond(c, s, precedente)))
        {
            Citer("void", s);
            Annuler(s.Action, s.Frame);
            return;
        }

        if (_r.Echecs.Any(c => Correspond(c, s, precedente)))
        {
            Citer("fail", s);
            Rater(s.Action, s.Frame);
            return;
        }

        foreach (var c in _r.Compteurs.Where(c => Correspond(c.Evenement, s, precedente)))
        {
            _compteurs[c.Id]++;
            if (c.Maximum is { } max && _compteurs[c.Id] > max)
            {
                Citer("fail", s);
                Rater(c.Id, s.Frame);
                return;
            }
        }

        if (!AvancerLesEtapes(s)) { Citer("fail", s); Rater("milestone_out_of_order", s.Frame); return; }

        if (_plageOuverte is null && _r.Exclusions.Any(x => Correspond(x.De, s, precedente))) _plageOuverte = s.Frame;
        else if (_plageOuverte is { } debut && _r.Exclusions.Any(x => Correspond(x.A, s, precedente)))
        {
            _plages.Add((debut, s.Frame));
            _plageOuverte = null;
        }

        // Le chrono secondaire peut partir apres l'officiel (Green Hill 1 : main au joueur 25 images apres le timer).
        if (_r.Cale is { } cale && _departCale is null && Borne(cale.Depart, s, precedente)) _departCale = s.Frame;
        if (_r.Independant is { } ind && _departIndependant is null && Borne(ind.Depart, s, precedente)) _departIndependant = s.Frame;
        if (_r.Cale is { } c2 && _departCale is not null && c2.Arrivee.Evenements.Any(f => Correspond(f, s, precedente))) _arriveeEvenementCale = s.Frame;
        if (_r.Independant is { } i2 && _departIndependant is not null && i2.Arrivee.Evenements.Any(f => Correspond(f, s, precedente))) _arriveeEvenementIndependant = s.Frame;

        if (Etat == EtatTentative.ArriveeProvisoire && _r.EffacePar.Any(c => Correspond(c, s, precedente)))
        {
            _arriveeProvisoire = null;
            Etat = EtatTentative.EnCourse;
        }

        if (_r.Arrivee.Any(c => Correspond(c, s, precedente)))
        {
            _arriveeProvisoire = s.Frame;
            Citer("finish", s);
            if (_r.Confirmations.Count == 0) { Finir(s.Frame); return; }
            Etat = EtatTentative.ArriveeProvisoire;
            // La capsule de Green Hill 3 fige le timer et bloque les commandes dans la meme image : l'ordre des
            // deux evenements dans l'image ne doit pas compter.
            if (_confirmationSansArrivee == s.Frame) { _confirmation = s.Frame; Finir(s.Frame); }
            return;
        }

        // Une confirmation sans arrivee provisoire ne finit rien : dans un monde, la fin de l'acte 1 arrive
        // bien avant la capsule de l'acte 3. On la retient seulement pour l'image en cours.
        if (Etat == EtatTentative.EnCourse && _r.Confirmations.Any(c => Correspond(c, s, precedente))) _confirmationSansArrivee = s.Frame;
    }

    private bool Confirme(Signal s, int? precedente)
    {
        if (!_r.Confirmations.Any(c => Correspond(c, s, precedente))) return false;
        if (_r.Independant is { } ind && _departIndependant is not null && ind.Arrivee.Evenements.Any(f => Correspond(f, s, precedente)))
            _arriveeEvenementIndependant = s.Frame;
        if (_r.Cale is { } cale && _departCale is not null && cale.Arrivee.Evenements.Any(f => Correspond(f, s, precedente)))
            _arriveeEvenementCale = s.Frame;
        Citer("finish_confirm", s);
        _confirmation = s.Frame;
        Finir(s.Frame);
        return true;
    }

    /// <summary>Faux si l'adresse d'une etape prend une valeur hors de la suite (acte saute, retour en arriere).</summary>
    private bool AvancerLesEtapes(Signal s)
    {
        for (var i = 0; i < _r.Etapes.Count; i++)
        {
            var etape = _r.Etapes[i];
            if (etape.Adresse != s.Adresse) continue;
            var pos = _progressionEtapes[i];
            if (pos < etape.Suite.Count && s.Valeur == etape.Suite[pos]) _progressionEtapes[i] = pos + 1;
            else if (pos > 0 && s.Valeur == etape.Suite[pos - 1]) continue;
            else return false;
        }

        return true;
    }

    private void Finir(long frame)
    {
        _frameDeFin = frame;
        if (_plageOuverte is { } debut) { _plages.Add((debut, frame)); _plageOuverte = null; }

        for (var i = 0; i < _r.Etapes.Count; i++)
        {
            if (_progressionEtapes[i] < _r.Etapes[i].Suite.Count) { Rater("milestones_incomplete", frame); return; }
        }

        foreach (var v in _r.ValeursFinales)
        {
            if (v.Egal is null && v.AuMoins is null) continue;
            if (!_valeurs.TryGetValue(v.Adresse, out var valeur)) { Annuler(v.Id + "_unknown", frame); return; }
            if (!new ConditionValeur(v.Adresse, v.Egal, v.AuMoins).Vraie(valeur)) { Rater(v.Id, frame); return; }
        }

        Etat = EtatTentative.Finie;
        Cause = null;
    }

    private void Rater(string cause, long frame) { Etat = EtatTentative.Ratee; Cause = cause; _frameDeFin = frame; }

    private void Annuler(string cause, long frame) { Etat = EtatTentative.Annulee; Cause = cause; _frameDeFin = frame; }

    /// <summary>
    /// L'HORLOGE A AVANCE SANS LES FRAMES : pause de RetroArch, menu, perte du focus, PC suspendu (§5.5 du CDC
    /// v1.4). Les evenements sont espaces ; on compare, entre deux, le temps de la borne et celui des frames.
    /// </summary>
    private bool PauseRevelee(Signal s)
    {
        if (s.HorlogeMs is not { } h || _derniereHorloge is not { } h0 || _frameDeLHorloge is not { } f0) return false;
        var horloge = h - h0;
        var frames = (s.Frame - f0) * 1000.0 / _r.Cadence;
        return horloge - frames > _r.TolerancePauseMs;
    }

    private bool Borne(BorneDeChrono borne, Signal s, int? precedente)
        => borne.Evenements.Any(ev => Correspond(ev, s, precedente))
            && borne.Quand.All(q => _valeurs.TryGetValue(q.Adresse, out var v) && q.Vraie(v));

    private static bool Correspond(CibleEvenement c, Signal s, int? precedente)
        => (c.Action is not { } action || action.Equals(s.Action, StringComparison.OrdinalIgnoreCase))
            && (c.Adresse is not { } adresse || adresse == s.Adresse)
            && (c.Valeur is not { } valeur || valeur == s.Valeur)
            && (c.Quitte is not { } quitte || (precedente == quitte && s.Valeur != quitte));

    private void Citer(string role, Signal s) => _journal.Add(new EvenementCite(role, s.Action, s.Adresse, s.Valeur, s.Frame));

    public ResultatTentative Resultat()
    {
        var cale = Mesurer(_r.Cale, _departCale, _arriveeEvenementCale);
        var independant = Mesurer(_r.Independant, _departIndependant, _arriveeEvenementIndependant);
        var officiel = _r.ChronoOfficiel == Reglement.ChronoIndependant ? independant : cale;
        var tempsDuJeu = TempsDuJeu();
        return new ResultatTentative(
            Etat, Cause, _frameDeFin, _r.ChronoOfficiel, cale, independant,
            Etat == EtatTentative.Finie ? officiel?.Frames : null,
            tempsDuJeu,
            Controler(cale, tempsDuJeu),
            new Dictionary<string, int>(_compteurs),
            _r.ValeursFinales.ToDictionary(v => v.Id, v => _valeurs.TryGetValue(v.Adresse, out var x) ? x : (int?)null),
            _plages.ToList(),
            _journal.ToList(),
            _r.Cadence);
    }

    private MesureChrono? Mesurer(DefinitionChrono? chrono, long? depart, long? arriveeEvenement)
    {
        if (chrono is null) return null;
        long? arrivee = Etat != EtatTentative.Finie ? null : chrono.Arrivee.Renvoi switch
        {
            "finish" => _arriveeProvisoire,
            "finish_confirm" => _confirmation ?? _arriveeProvisoire,
            _ => arriveeEvenement,
        };
        if (depart is not { } d || arrivee is not { } a || a < d) return new MesureChrono(depart, arrivee, null, 0);
        var exclues = _plages.Sum(p => Math.Max(0, Math.Min(p.A, a) - Math.Max(p.De, d)));
        return new MesureChrono(d, a, a - d - exclues, exclues);
    }

    /// <summary>Le temps affiche par le jeu a l'arrivee, en secondes entieres ; null si une de ses lignes n'a jamais ete lue.</summary>
    private int? TempsDuJeu()
    {
        if (_r.TempsDuJeu.Count == 0) return null;
        double secondes = 0;
        foreach (var ligne in _r.TempsDuJeu)
        {
            if (!_valeurs.TryGetValue(ligne.Adresse, out var v)) return null;
            secondes += ligne.Unite switch
            {
                "minute" => v * 60.0,
                "second" => v,
                "centisecond" => v / 100.0,
                "frame" => v / (double)_r.PeriodeDuTimer,
                _ => 0,
            };
        }

        return (int)Math.Floor(secondes);
    }

    /// <summary>
    /// LE CHRONO CALE CONCORDE-T-IL AVEC LE TEMPS DU JEU ? Le timer compte une seconde par periode : les
    /// secondes affichees valent la partie entiere des frames divisees par la periode, a une seconde pres.
    /// </summary>
    /// <summary>
    /// Le chrono cale contre le temps affiche par le jeu. « ok » : a une seconde pres. « jeu_en_retard » : le
    /// timer du jeu avance au rythme de sa logique, pas de la console, et perd les images ou le jeu rame
    /// (Sonic : anneaux eparpilles, beaucoup d'objets) ; un retard jusqu'a 5 % (2 s au moins) s'explique ainsi.
    /// « ecart » : le jeu en avance, ou un retard que le ralenti n'explique pas (depart ou arrivee mal places).
    /// </summary>
    private string Controler(MesureChrono? cale, int? tempsDuJeu)
    {
        if (cale?.Frames is not { } frames || tempsDuJeu is not { } secondes) return "non_mesure";
        var attendu = frames / _r.PeriodeDuTimer;
        if (Math.Abs(attendu - secondes) <= 1) return "ok";
        var retard = attendu - secondes;
        return retard > 0 && retard <= Math.Max(2, attendu * 0.05) ? "jeu_en_retard" : "ecart";
    }
}

/// <summary>L'affichage d'un temps en frames, a la cadence reelle du coeur (D30) : m:ss.cc.</summary>
public static class Chronometrage
{
    public static string Afficher(long frames, double cadence)
    {
        var centiemes = (long)Math.Round(frames * 100.0 / cadence, MidpointRounding.AwayFromZero);
        var minutes = centiemes / 6000;
        var reste = centiemes % 6000;
        return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}.{2:00}", minutes, reste / 100, reste % 100);
    }
}
