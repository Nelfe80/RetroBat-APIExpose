using RetroBat.Api.Replay.Social;

namespace RetroBat.Api.Replay.Overlay;

/// <summary>
/// Le CAMEO : un performer vient reagir en personne sur la timeline, pile a la frame.
///
/// La plateforme donne la frame exacte de chaque reaction retenue (voir SocialSummary). Ici on
/// decide, a chaque image du HUD, ce qu'il y a a dessiner :
///   - une seconde AVANT la frame, l'avatar monte depuis le bas de l'ecran, a l'endroit de la
///     timeline ou la reaction a eu lieu, son etiquette a cote (nom, rang, couronnes) ;
///   - A la frame, il saute en faisant une pirouette (face, droite, dos, gauche, face) et son
///     emoji part vers le haut, grossit et s'efface ;
///   - il reste un instant, puis redescend.
///
/// L'entree est reglee sur l'horloge (on connait la frame qui vient), le saut sur la frame ELLE-
/// MEME (c'est ce qui le met pile au moment) : une pause entre les deux le laisse debout, en
/// attente. Un seul cameo a la fois : la plateforme les espace deja de six secondes.
///
/// Pur : pas d'horloge ni d'ecran dedans, tout vient des arguments. C'est ce qui se teste.
/// </summary>
public sealed class ReplayCameoModel
{
    public const int EntreeMs = 1000;
    public const int SautMs = 520;
    public const int VolMs = 1400;
    public const int AttenteMs = 900;
    public const int SortieMs = 700;
    /// <summary>Une frame passee de plus que ca sans avoir saute (un saut dans la lecture) : on abandonne.</summary>
    public const int RetardMaxMs = 4000;
    public const int HauteurSaut = 28;
    public const int MonteeVol = 96;

    public enum Phase { Entree, Attente, Saut, Repos, Sortie }

    /// <summary>Ce qu'il y a a dessiner. `Montee` va de 0 (hors ecran, en bas) a 1 (debout).</summary>
    public sealed record Etat(
        SocialSummary.Cameo Cameo,
        Phase Phase,
        float Montee,
        int Vue,          // 0 face, 1 dos, 2 droite, 3 gauche (les lignes de la planche, gauche = miroir)
        float Hauteur,    // le saut, en pixels de sprite
        float Vol,        // -1 sans emoji en vol, sinon 0..1 d'avancement
        bool Bouge);

    private readonly List<SocialSummary.Cameo> _cameos = new();
    private readonly HashSet<long> _joues = new();
    private double _fps = 60;
    private int _prochain;
    private SocialSummary.Cameo? _courant;
    private long _debut;          // l'instant ou l'entree a commence
    private long _sautA = -1;     // l'instant ou le saut a commence, -1 tant qu'il n'a pas eu lieu
    private long _derniereFrame = -1;

    public void Charger(IReadOnlyList<SocialSummary.Cameo> cameos, double fps)
    {
        _cameos.Clear();
        _cameos.AddRange(cameos.OrderBy(c => c.Frame));
        _fps = fps > 0 ? fps : 60;
        _joues.Clear();
        _prochain = 0;
        _courant = null;
        _sautA = -1;
        _derniereFrame = -1;
    }

    public void Vider() => Charger(Array.Empty<SocialSummary.Cameo>(), _fps);

    public bool EnScene => _courant is not null;

    /// <summary>Un cameo est-il prevu a cette frame, a la tolerance pres ? La petite carte de
    /// reaction n'a rien a dire quand le personnage vient le dire lui-meme.</summary>
    public bool ADesCameoPres(long frame, long tolerance)
        => _cameos.Any(c => Math.Abs(c.Frame - frame) <= tolerance);

    /// <summary>
    /// L'etat a dessiner maintenant, ou null. `frame` est la frame affichee (interpolee par
    /// l'appelant), `paused` dit si elle avance.
    /// </summary>
    public Etat? Relever(long frame, bool paused, long now)
    {
        // Un retour en arriere dans la lecture : ce qui est apres la frame redevient a jouer.
        if (_derniereFrame >= 0 && frame < _derniereFrame - (long) (2 * _fps))
        {
            _joues.RemoveWhere(f => f >= frame);
            _prochain = 0;
            while (_prochain < _cameos.Count && _cameos[_prochain].Frame < frame) _prochain++;
            if (_courant is not null && _courant.Frame > frame + (long) (_fps * EntreeMs / 1000.0))
            {
                _courant = null;
                _sautA = -1;
            }
        }
        _derniereFrame = frame;

        if (_courant is null)
        {
            // Les cameos deja passes sans avoir ete joues (lecture reprise plus loin) sont sautes.
            while (_prochain < _cameos.Count
                   && (_joues.Contains(_cameos[_prochain].Frame) || _cameos[_prochain].Frame < frame - (long) (_fps * 0.5)))
            {
                _prochain++;
            }
            if (_prochain >= _cameos.Count) return null;
            var candidat = _cameos[_prochain];
            var msAvant = (candidat.Frame - frame) * 1000.0 / _fps;
            if (paused || msAvant > EntreeMs) return null;
            _courant = candidat;
            _debut = now;
            _sautA = -1;
            _prochain++;
        }

        var c = _courant;
        var depuisEntree = now - _debut;

        if (_sautA < 0)
        {
            var monteeEntree = Math.Clamp(depuisEntree / (float) EntreeMs, 0f, 1f);
            // Le saut part a la FRAME, jamais avant : c'est ce qui le met pile au moment.
            if (frame >= c.Frame)
            {
                if (depuisEntree > EntreeMs + RetardMaxMs || frame > c.Frame + (long) (_fps * RetardMaxMs / 1000.0))
                {
                    // Trop tard (un saut dans la lecture) : on n'arrive pas en retard a sa propre reaction.
                    Terminer(c);
                    return null;
                }
                _sautA = now;
                return new Etat(c, Phase.Saut, 1f, 0, 0f, 0f, true);
            }
            return monteeEntree < 1f
                ? new Etat(c, Phase.Entree, monteeEntree, 0, 0f, -1f, true)
                : new Etat(c, Phase.Attente, 1f, 0, 0f, -1f, false);
        }

        var depuisSaut = now - _sautA;
        if (depuisSaut < SautMs)
        {
            var t = depuisSaut / (float) SautMs;
            // La pirouette : face, droite, dos, gauche, et on retombe de face.
            var vue = (int) Math.Floor(t * 4) switch { 0 => 2, 1 => 1, 2 => 3, _ => 0 };
            var hauteur = (float) (Math.Sin(t * Math.PI) * HauteurSaut);
            return new Etat(c, Phase.Saut, 1f, vue, hauteur, Math.Clamp(depuisSaut / (float) VolMs, 0f, 1f), true);
        }
        if (depuisSaut < SautMs + AttenteMs)
        {
            var vol = depuisSaut < VolMs ? depuisSaut / (float) VolMs : -1f;
            return new Etat(c, Phase.Repos, 1f, 0, 0f, vol, vol >= 0f);
        }
        var depuisSortie = depuisSaut - SautMs - AttenteMs;
        if (depuisSortie < SortieMs)
        {
            var vol = depuisSaut < VolMs ? depuisSaut / (float) VolMs : -1f;
            return new Etat(c, Phase.Sortie, 1f - depuisSortie / (float) SortieMs, 0, 0f, vol, true);
        }
        Terminer(c);
        return null;
    }

    private void Terminer(SocialSummary.Cameo c)
    {
        _joues.Add(c.Frame);
        _courant = null;
        _sautA = -1;
    }
}
