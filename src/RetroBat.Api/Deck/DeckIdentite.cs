using RetroBat.Api.Infrastructure;
using RetroBat.Api.Media;

namespace RetroBat.Api.Deck;

/// <summary>
/// L'IDENTITE DU JEU D'UN LANCEMENT AVEC DECK : la cle « systeme/rom_group » des classements (rang en direct, H-D21) et du
/// choix retenu (<c>choix.json</c>). Essai reel du 08/11 : en arcade, la fiche trouvee par le NOM du fichier
/// (<see cref="RomCanonicalResolver.ResolveByRomName"/>) peut etre celle d'un autre jeu (mslug3 donnait « 2020 Super
/// Baseball », sf2ce « Adventure Quiz Capcom World 2 »). Comme la route <c>/api/v1/gamelists/resolve</c>, la cle vient
/// d'abord du .MEM qui connait ce dump (<see cref="RomCanonicalResolver.ResolveScoreSlug"/>, = rom_group certifie, la cle
/// des classements), puis d'un .MEM sans score (jeu ouvert aux seules epreuves), puis du rom_group donne par l'appelant
/// (reglement d'une epreuve) ; le nom du fichier ne sert qu'en repli, et la fiche par nom jamais en arcade.
/// </summary>
public static class DeckIdentite
{
    public sealed record Cle(string Jeu, string Groupe, string Source, string? SystemeCanonique = null);

    /// <summary>Avec le referentiel de l'API (ou sans : repli sur le nom du fichier).</summary>
    public static Cle Resoudre(RomCanonicalResolver? canonique, string systeme, string chemin, string? romGroup)
        => Resoudre(systeme, chemin, romGroup,
            canonique is null ? null : (s, f) => canonique.ResolveScoreSlug(s, f, null, null),
            canonique is null ? null : (s, f) => canonique.ResolveDefinitionSlug(s, f, null, null),
            canonique is null ? null : (s, f) => canonique.ResolveByRomName(s, f) is { GameKey: { Length: > 0 } k } c ? (k, c.CanonicalSystem) : null);

    /// <summary>La cascade, pure : les trois lectures du referentiel sont passees en parametre (essais sans fichiers).</summary>
    public static Cle Resoudre(string systeme, string chemin, string? romGroup,
        Func<string, string, string?>? slugDuScore, Func<string, string, string?>? slugDeDefinition,
        Func<string, string, (string Cle, string? Systeme)?>? ficheParNom)
    {
        var dossier = RomCanonicalResolver.CanonicalScoringSystem(systeme);
        string? Essayer(Func<string, string, string?>? f)
        {
            try { return f?.Invoke(systeme, chemin) is { Length: > 0 } s ? s : null; }
            catch (Exception) { return null; }
        }

        if (Essayer(slugDuScore) is { } score) return Faire(dossier, score, "mem-score");
        if (Essayer(slugDeDefinition) is { } definition) return Faire(dossier, definition, "mem-definition");
        if (romGroup is { Length: > 0 } donne) return Faire(dossier, donne, "caller");

        // La fiche par nom de fichier se trompe en arcade (ensembles de ROM aux noms courts) : jamais la
        if (!dossier.Equals("arcade", StringComparison.OrdinalIgnoreCase) && ficheParNom is not null)
        {
            try
            {
                if (ficheParNom(systeme, chemin) is { } fiche)
                {
                    var barre = fiche.Cle.IndexOf('/');
                    var groupe = barre >= 0 ? fiche.Cle[(barre + 1)..] : fiche.Cle;
                    return new Cle(fiche.Cle.ToLowerInvariant(), groupe, "catalog", fiche.Systeme);
                }
            }
            catch (Exception)
            {
            }
        }

        return Faire(dossier, GamelistIdentity.Slugifier(Path.GetFileNameWithoutExtension(chemin)), "file");
    }

    private static Cle Faire(string dossier, string groupe, string source)
        => new((dossier + "/" + groupe).ToLowerInvariant(), groupe, source, dossier);
}
