using RetroBat.Api.Multiplayer;

namespace RetroBat.Api.Adventure;

/// <summary>Un joueur de la seance, pour le choix de la langue : sa place (si elle est connue) et les langues qu'il lit, la sienne d'abord.</summary>
public sealed record AdventureJoueurLangue(int? Place, bool Hote, IReadOnlyList<string> Langues)
{
    /// <summary>Ce joueur lit-il cette langue ? Une langue inconnue ne gene personne : on ne devine pas, on ne bloque pas.</summary>
    public bool Lit(string langue) => Langues.Count == 0 || Langues.Contains(langue, StringComparer.Ordinal);
}

/// <summary>Le choix fait pour une seance : le depart (ou null : a froid) et la langue qu'il porte.</summary>
public sealed record AdventureChoixDeDepart(MultiplayerStart? Depart, string? Langue, string Raison);

/// <summary>
/// LA LANGUE DU DEPART (CDC Adventure v1.0 §0, « LANGUES MELEES », 1.9.223). Pur : c'est ce qui se teste.
///
/// La langue d'un joueur est celle de son ES et de son API (<c>CabinetLocale.Langue</c> : « fr_FR » devient « fr ») ; celle d'un autre
/// joueur vient du site (<c>language</c> de son entree dans la gameroom). Une machine emulee partagee (SNES, Mega Drive, et la GBA tant
/// que les consoles de langues differentes ne sont pas prouvees) est UN ecran, UNE langue : on choisit, parmi les langues dont la version
/// a un depart pret,
///   1. celle de l'hote si tous les autres la lisent ;
///   2. sinon la premiere langue (dans l'ordre : hote, puis les autres) que tous lisent ;
///   3. sinon l'anglais ;
///   4. sinon la langue du depart par defaut de la version.
/// Une aventure garde la langue de sa creation : <paramref name="langueDeLAventure"/> passe avant tout. Un depart par COMBINAISON de
/// langues (une langue par console GBA) ne sert que si le catalogue en porte un pour la suite exacte des langues des places.
/// </summary>
public static class AdventureLangues
{
    /// <summary>« fr_FR », « fr-FR », « FR », « French » (deux premieres lettres) vers « fr » ; vide ou illisible : null.</summary>
    public static string? Normaliser(string? langue)
    {
        if (string.IsNullOrWhiteSpace(langue))
        {
            return null;
        }

        var texte = langue.Trim().ToLowerInvariant();
        if (NomEnClair(texte) is { } connu)
        {
            return connu;
        }

        var coupe = texte.IndexOfAny(['_', '-', '.']);
        var code = coupe > 0 ? texte[..coupe] : texte;
        return code.Length == 2 && code.All(char.IsAsciiLetter) ? code : null;
    }

    private static string? NomEnClair(string texte) => texte switch
    {
        "english" or "eng" => "en",
        "fre" or "fra" => "fr",
        "ger" or "deu" => "de",
        "spa" => "es",
        "ita" => "it",
        "jpn" or "jp" => "ja",
        "por" => "pt",
        "korean" or "kor" or "kr" => "ko",
        "chinese" or "chi" or "zho" or "cn" => "zh",
        "russian" or "rus" => "ru",
        "dutch" or "dut" or "nld" => "nl",
        "swedish" or "swe" => "sv",
        "polish" or "pol" => "pl",
        "french" or "francais" or "français" => "fr",
        "german" or "deutsch" => "de",
        "spanish" or "espanol" or "español" => "es",
        "italian" or "italiano" => "it",
        "japanese" or "japonais" => "ja",
        "portuguese" or "portugues" or "português" => "pt",
        _ => null,
    };

    /// <summary>Les langues d'un joueur (sa langue d'abord, puis les autres qu'il declare), normalisees, sans doublon.</summary>
    public static IReadOnlyList<string> LanguesDe(string? langue, IEnumerable<string>? autres = null)
        => new[] { langue }.Concat(autres ?? []).Select(Normaliser).OfType<string>().Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Les langues a essayer, dans l'ordre, parmi <paramref name="offertes"/> (celles dont la version a des departs prets) : d'abord celles que
    /// TOUS lisent (l'hote d'abord, puis les autres dans l'ordre de leurs places) ; si aucune, l'anglais, puis la langue du depart par defaut.
    /// </summary>
    public static IReadOnlyList<string> Candidates(IReadOnlyList<string> offertes, IReadOnlyList<AdventureJoueurLangue> joueurs, string? langueParDefaut)
    {
        if (offertes.Count == 0)
        {
            return [];
        }

        var preferees = joueurs.OrderByDescending(j => j.Hote).ThenBy(j => j.Place ?? int.MaxValue)
            .SelectMany(j => j.Langues).Distinct(StringComparer.Ordinal);
        var communes = preferees.Where(l => offertes.Contains(l, StringComparer.Ordinal) && joueurs.All(j => j.Lit(l))).ToList();
        if (communes.Count > 0)
        {
            return communes;
        }

        return new[] { "en", langueParDefaut }.OfType<string>().Where(l => offertes.Contains(l, StringComparer.Ordinal)).Concat(offertes).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>La langue de la partie : la premiere des <see cref="Candidates"/>, ou null.</summary>
    public static string? LangueCommune(IReadOnlyList<string> offertes, IReadOnlyList<AdventureJoueurLangue> joueurs, string? langueParDefaut)
        => Candidates(offertes, joueurs, langueParDefaut).FirstOrDefault();

    /// <summary>
    /// Le depart pret de cette version pour ce nombre de joueurs, ce contenu et ce coeur, dans la langue qui convient.
    /// <paramref name="partieNeuveParDefaut"/> : le depart par defaut de la version (<see cref="MultiplayerGame.DepartPour"/>), de la
    /// <see cref="AdventureVersionDuJeu.LangueParDefaut"/>.
    /// </summary>
    public static AdventureChoixDeDepart ChoisirLeDepart(
        AdventureVersionDuJeu? version, MultiplayerGame jeu, int joueurs, string? contenu, string? empreinteDuCoeur,
        IReadOnlyList<AdventureJoueurLangue> lesJoueurs, string? langueDeLAventure)
    {
        var parDefaut = jeu.DepartPour(joueurs, contenu, empreinteDuCoeur);
        var langueParDefaut = version?.LangueParDefaut;
        if (version is null)
        {
            return new(parDefaut, null, "version unique : depart du catalogue");
        }

        var verrou = Normaliser(langueDeLAventure);

        // 1. Une langue par console, seulement avec un depart fait pour cette suite exacte (GBA, apres preuve au banc).
        //    Sans langue d'aventure : toutes les places doivent avoir une langue connue. AVEC une langue d'aventure (reprise a un autre nombre
        //    de joueurs : le .srm de la console 1 porte SA langue, lot 4 : Four Swords et Kirby) : la place 1 est la langue de l'aventure, les
        //    autres places celle de leur joueur (la langue de l'aventure quand elle est inconnue) ; sans depart pour cette suite, la meme langue
        //    partout ; sans depart non plus, la regle de la langue de l'aventure plus bas (a froid au besoin).
        if (version.CombinationStarts is { Count: > 0 } combinaisons && joueurs >= 2)
        {
            var langues = Enumerable.Range(1, joueurs)
                .Select(place => lesJoueurs.FirstOrDefault(j => j.Place == place)?.Langues.FirstOrDefault()).ToList();
            var suites = new List<IReadOnlyList<string?>>();
            if (verrou is null)
            {
                suites.Add(langues);
            }
            else
            {
                suites.Add(langues.Select((l, i) => i == 0 ? verrou : l ?? verrou).ToList());
                suites.Add(Enumerable.Repeat<string?>(verrou, joueurs).ToList());
            }

            foreach (var suite in suites.Where(su => su.All(l => l is not null)))
            {
                foreach (var combinaison in combinaisons)
                {
                    if (combinaison.Pour(suite, contenu, empreinteDuCoeur) is { } depart)
                    {
                        return new(depart, suite[0], "combinaison de langues " + string.Join("+", suite));
                    }
                }
            }
        }

        var offertes = version.LanguesAvecDepart();

        // 2. La langue de l'aventure, sinon les langues communes des joueurs. Une aventure garde la sienne : sans depart dans cette langue a
        //    ce nombre de joueurs, la partie part a froid (on choisit la langue dans le jeu) plutot que dans une autre langue.
        var candidates = verrou is not null ? [verrou] : Candidates(offertes, lesJoueurs, langueParDefaut);
        if (candidates.Count == 0)
        {
            return new(parDefaut, langueParDefaut, "version sans langue nommee : depart par defaut");
        }

        foreach (var langue in candidates)
        {
            var depart = langue == langueParDefaut
                ? parDefaut
                : version.LanguageStarts?.FirstOrDefault(l => Normaliser(l.Key) == langue).Value?.Pour(joueurs, contenu, empreinteDuCoeur);
            if (depart is not null)
            {
                return new(depart, langue, (langue == langueParDefaut ? "depart par defaut (" : "depart en (") + langue + ")");
            }
        }

        return new(null, null, "aucun depart en " + string.Join("/", candidates) + " a " + joueurs + " joueurs : a froid");
    }
}
