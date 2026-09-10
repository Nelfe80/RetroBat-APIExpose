namespace RetroBat.Api.Netplay;

/// <summary>
/// « Je regarde le direct de quelqu'un. »
///
/// Le pendant, pour un direct, de ce que l'etat de lecture est pour un replay : la reponse a
/// « a quoi suis-je en train de reagir ». Sans lui, le moteur de reactions n'a aucune cible
/// pendant un direct, et la facade reste muette alors que c'est precisement le moment ou l'on
/// veut s'exprimer.
///
/// Il porte le JETON DE SPECTATEUR recu en rejoignant. Ce jeton est opaque : la borne
/// n'apprend rien du compte, elle l'estampille sur ses reactions et c'est la plateforme qui le
/// resout. Une machine compromise ne peut donc pas parler au nom de n'importe qui.
///
/// Un seul direct a la fois, volontairement : une borne a un ecran et une facade, on n'y
/// regarde pas deux parties de front. Rejoindre un autre direct remplace le precedent.
/// </summary>
public sealed class LiveSpectateState
{
    private readonly object _gate = new();
    private string _session = "";
    private string _viewer = "";
    private bool _peutJouer;

    /// <summary>Ouvre la seance. Un appel sans session ni jeton la ferme : rien a viser.</summary>
    public void Ouvrir(string session, string viewer, bool peutJouer)
    {
        lock (_gate)
        {
            _session = (session ?? "").Trim();
            _viewer = (viewer ?? "").Trim();
            _peutJouer = peutJouer;
        }
    }

    public void Fermer()
    {
        lock (_gate)
        {
            _session = "";
            _viewer = "";
            _peutJouer = false;
        }
    }

    /// <summary>
    /// La seance en cours, ou des chaines vides.
    ///
    /// Sans jeton, on rend une seance INACTIVE meme si la session est connue : une reaction que
    /// personne ne peut attribuer n'est ni decomptable d'un budget, ni agregeable honnetement.
    /// C'est la meme regle que pour un replay, et elle ne doit pas s'assouplir ici.
    /// </summary>
    public (string Session, string Viewer, bool PeutJouer) Courant
    {
        get
        {
            lock (_gate)
            {
                return (_session, _viewer, _peutJouer);
            }
        }
    }

    public bool Actif
    {
        get
        {
            lock (_gate)
            {
                return _session.Length > 0 && _viewer.Length > 0;
            }
        }
    }
}
