using System.Globalization;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// La langue de la borne, pour tout ce qu'APIExpose DESSINE a l'ecran : textes du dictionnaire
/// d'interface, separateur de milliers, format des dates.
///
/// Une seule source, celle qu'APIExpose applique deja a ses notifications et au panneau de
/// classement (<see cref="EmulationStationSettingsService.GetScrapingSettings"/>). Sans elle, chaque
/// ecran choisit sa langue : la barre du replay etait en francais sur une borne en anglais.
///
/// Gardee en memoire trente secondes : lire la langue analyse es_settings.cfg, et un overlay la
/// demande a chaque image.
/// </summary>
public sealed class CabinetLocale
{
    private static readonly TimeSpan Fraicheur = TimeSpan.FromSeconds(30);

    private readonly EmulationStationSettingsService _reglages;
    private readonly InterfaceTextService _textes;
    private readonly object _verrou = new();
    private string _langue = "en";
    private CultureInfo _culture = CultureInfo.GetCultureInfo("en-US");
    private string _langueDesJeux = "en";
    private DateTime _lue = DateTime.MinValue;

    public CabinetLocale(EmulationStationSettingsService reglages, InterfaceTextService textes)
    {
        _reglages = reglages;
        _textes = textes;
    }

    /// <summary>Le code de langue de la borne, tel que le dictionnaire l'attend (« en_US », « fr_FR »…).</summary>
    public string Langue
    {
        get { Rafraichir(); lock (_verrou) return _langue; }
    }

    /// <summary>
    /// LA LANGUE DES JEUX de la borne, en deux lettres (« fr ») : le profil de langue des ROMs que le joueur regle dans les options
    /// d'APIExpose (<c>global.apiexpose.romset.language_profile</c>, ou <c>api.language_profile</c> : french, german, japanese...), sinon la
    /// langue d'ES, sinon « en ». « show_all » ou un profil vide n'est pas un profil. C'est celle que NelfePlay Adventure suit
    /// (collection, depart, langue declaree au site). Lue de <c>ContentLanguageProfile</c> : le calcul des gamelists localisees, rien de refait.
    /// </summary>
    public string LangueDesJeux
    {
        get { Rafraichir(); lock (_verrou) return _langueDesJeux; }
    }

    /// <summary>Le profil de langue des ROMs (ou la langue d'ES, ou « en ») en deux lettres. Pur.</summary>
    public static string LangueDesJeuxDe(string? profil, string? langueDEs)
        => RetroBat.Api.Adventure.AdventureLangues.Normaliser(profil) ?? RetroBat.Api.Adventure.AdventureLangues.Normaliser(langueDEs) ?? "en";

    /// <summary>La culture de la borne, pour les nombres et les dates.</summary>
    public CultureInfo Culture
    {
        get { Rafraichir(); lock (_verrou) return _culture; }
    }

    /// <summary>Un texte du dictionnaire d'interface, dans la langue de la borne.</summary>
    public string Text(string cle) => _textes.Text(cle, Langue);

    private void Rafraichir()
    {
        lock (_verrou)
        {
            if (DateTime.UtcNow - _lue < Fraicheur) return;
            _lue = DateTime.UtcNow;
            try
            {
                var reglages = _reglages.GetScrapingSettings();
                _langueDesJeux = LangueDesJeuxDe(reglages.ContentLanguageProfile, reglages.Language);
                var langue = reglages.Language;
                if (string.IsNullOrWhiteSpace(langue)) return;
                _langue = langue.Trim();
                _culture = CultureDe(_langue);
            }
            catch (Exception)
            {
                // Reglages illisibles : on garde la derniere langue connue.
            }
        }
    }

    /// <summary>« fr_FR » ou « fr » vers une culture .NET ; l'anglais si la langue est inconnue.</summary>
    public static CultureInfo CultureDe(string langue)
    {
        var nom = (langue ?? "").Trim().Replace('_', '-');
        foreach (var essai in new[] { nom, nom.Split('-')[0] })
        {
            if (essai.Length == 0) continue;
            try { return CultureInfo.GetCultureInfo(essai); }
            catch (CultureNotFoundException) { }
        }
        return CultureInfo.GetCultureInfo("en-US");
    }
}
