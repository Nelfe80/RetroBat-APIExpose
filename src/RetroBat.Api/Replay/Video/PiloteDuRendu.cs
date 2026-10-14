namespace RetroBat.Api.Replay.Video;

/// <summary>
/// LE PILOTE DU RENDU VIDEO D'UN REPLAY (CDC video V2, §4.2). A chaque releve de la lecture, il dit ce que le
/// rendu fait : lancer l'avance rapide, commencer l'enregistrement au debut de la performance, l'arreter a sa
/// fin, quitter. Il ne touche a rien lui-meme : il est pur, donc teste sans RetroArch.
///
/// Ce que le lot 0 a appris et qu'il applique :
/// - `--eof-exit` ne ferme pas RetroArch a la fin du replay (pause, ou plus de reponse) : on arrete avant ;
/// - un replay peut s'arreter avant ce qu'annonce son manifeste (Bubble Bobble, 2026-10-14) : une lecture qui
///   n'avance plus pendant quelques secondes est une fin, et le film est alors incomplet.
/// </summary>
public sealed class PiloteDuRendu
{
    public enum Action { Rien, AvanceRapide, DebutEnregistrement, FinEnregistrement, Quitter }

    /// <param name="Secondes">Temps ecoule depuis le lancement.</param>
    /// <param name="Drapeaux">Drapeaux de <c>active_replay</c> (4 : lecture), ou -1 sans reponse.</param>
    /// <param name="Frame">Frame du replay annoncee.</param>
    public sealed record Releve(double Secondes, int Drapeaux, long Frame);

    private readonly long _debut;
    private readonly long _fin;
    private readonly double _attenteDemarrage;
    private readonly double _immobiliteMax;

    private bool _joue, _avanceRapide, _enregistre, _arrete, _quitte;
    private long _derniereFrame = -1;
    private double _derniereProgression;

    /// <param name="debut">Premiere frame a filmer.</param>
    /// <param name="fin">Derniere frame a filmer : doit tomber avant la fin reelle du replay.</param>
    /// <param name="attenteDemarrage">Secondes accordees a RetroArch pour commencer la lecture.</param>
    /// <param name="immobiliteMax">Secondes sans progression au-dela desquelles la lecture est finie.</param>
    public PiloteDuRendu(long debut, long fin, double attenteDemarrage = 60, double immobiliteMax = 5)
    {
        _debut = Math.Max(0, debut);
        _fin = fin;
        _attenteDemarrage = attenteDemarrage;
        _immobiliteMax = immobiliteMax;
    }

    /// <summary>Null tant que tout va bien ; sinon pourquoi le film n'est pas complet.</summary>
    public string? Raison { get; private set; }

    /// <summary>La derniere frame vue en lecture.</summary>
    public long FrameAtteinte => _derniereFrame;

    /// <summary>Le pilote a demande de quitter.</summary>
    public bool Termine => _quitte;

    /// <summary>Le film couvre toute la performance.</summary>
    public bool Complet => _quitte && Raison is null && _enregistre && _derniereFrame >= _fin;

    /// <summary>La decision pour ce releve. Une action a la fois : on rappelle au releve suivant.</summary>
    public Action Decider(Releve r)
    {
        if (_quitte) return Action.Rien;

        if (r.Drapeaux > 0 && r.Frame > _derniereFrame)
        {
            _derniereFrame = r.Frame;
            _derniereProgression = r.Secondes;
            _joue = true;
        }

        if (!_joue)
        {
            if (r.Secondes <= _attenteDemarrage) return Action.Rien;
            Raison = "demarrage";
            _quitte = true;
            return Action.Quitter;
        }

        // L'enregistrement d'abord quand la performance commence deja : sinon l'avance rapide emporte la
        // premiere seconde avant le releve suivant. Avant le debut, l'avance rapide y mene vite.
        if (!_enregistre && _derniereFrame >= _debut)
        {
            _enregistre = true;
            return Action.DebutEnregistrement;
        }

        if (!_avanceRapide)
        {
            _avanceRapide = true;
            return Action.AvanceRapide;
        }

        if (_enregistre && !_arrete && _derniereFrame >= _fin)
        {
            _arrete = true;
            return Action.FinEnregistrement;
        }

        if (_arrete)
        {
            _quitte = true;
            return Action.Quitter;
        }

        // La lecture n'avance plus : le replay s'est arrete avant la fin voulue.
        if (r.Secondes - _derniereProgression > _immobiliteMax)
        {
            Raison = "fin_prematuree";
            if (_enregistre)
            {
                _arrete = true;
                return Action.FinEnregistrement;
            }
            _quitte = true;
            return Action.Quitter;
        }

        return Action.Rien;
    }

    /// <summary>A appeler quand le temps total accorde est depasse.</summary>
    public Action Abandonner()
    {
        if (_quitte) return Action.Rien;
        Raison ??= "plafond";
        if (_enregistre && !_arrete)
        {
            _arrete = true;
            return Action.FinEnregistrement;
        }
        _quitte = true;
        return Action.Quitter;
    }
}
