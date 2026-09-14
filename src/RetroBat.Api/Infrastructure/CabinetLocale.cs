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
                var langue = _reglages.GetScrapingSettings().Language;
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
