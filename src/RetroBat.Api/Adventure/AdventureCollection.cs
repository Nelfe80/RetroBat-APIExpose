using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Media;
using RetroBat.Api.Multiplayer;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;

namespace RetroBat.Api.Adventure;

/// <summary>
/// LA COLLECTION « NELFEPLAY ADVENTURE » D'ES (CDC v1.0 §2, point 1) : le meme calcul que la collection MULTIPLAYER
/// (<see cref="MultiplayerCollectionSyncService"/>), sur le catalogue Adventure et sous un autre nom. Les jeux entrent a la
/// volee comme dans World Scoring et MULTIPLAYER : le contenu reconnu a son md5, jamais une ROM renommee ni deplacee, et le
/// pseudo-jeu d'ES n'est jamais ecrit dans une gamelist (<c>DeclarerJeuxAbsents</c> ne declare que des jeux reels).
/// </summary>
public sealed class AdventureCollection
{
    /// <summary>Le nom de la collection dans ES (le dossier de ses fichiers, son logo et son fond).</summary>
    public const string Nom = "nelfeplay-adventure";

    /// <summary>Le logo livre par le Data Pack (<c>theme/images</c>) ; le fond est celui des autres collections NelfePlay.</summary>
    public const string Logo = "nelfeplay-adventure.png";

    public static readonly MultiplayerCollectionSyncService.ProfilDeCollection Profil
        = new(Nom, Logo, np => np.ShowAdventureCollection, "adventure-jeux.json", ToutesLesVersions: true);

    /// <summary>
    /// LE PROFIL DE LA BORNE (1.9.223, decision du user du 26/11) : la collection d'ES ne montre, pour chaque jeu, que les versions de LA
    /// LANGUE DE LA BORNE (<paramref name="langue"/> : ES puis config de l'API). Voir <see cref="VersionsAffichees"/>.
    /// </summary>
    public static MultiplayerCollectionSyncService.ProfilDeCollection ProfilDeLaBorne(AdventureCatalogueStore catalogue, Func<string?> langue)
        => Profil with { Affichees = tous => VersionsAffichees(tous, catalogue, langue()) };

    /// <summary>
    /// Pour chaque jeu dont la borne possede au moins une version : (1) les versions possedees dont les langues du catalogue contiennent la
    /// langue de la borne ; sinon (2) celles en anglais ; sinon (3) la <c>default_version</c> si elle est possedee ; sinon (4) TOUTES les versions
    /// possedees (une seule ROM, meme japonaise : on la montre). Un jeu possede n'est donc jamais cache. Langues d'une version : celles du catalogue
    /// (<c>languages</c>, <c>start_language</c>, cles de <c>language_starts</c>), jamais deduites d'un nom de fichier. Ne change que ce que la collection
    /// montre : une invitation, un join ou une reprise vers une version cachee passent par <see cref="Games"/>, qui garde tout.
    /// </summary>
    public static IReadOnlyList<MultiplayerInstalledGame> VersionsAffichees(IReadOnlyList<MultiplayerInstalledGame> tous, AdventureCatalogueStore catalogue, string? langueDeLaBorne)
    {
        var specs = catalogue.Entrees(out _).ToDictionary(e => e.Jeu.Id, e => e.Spec, StringComparer.Ordinal);
        var langue = AdventureLangues.Normaliser(langueDeLaBorne);
        var affichees = new List<MultiplayerInstalledGame>();
        foreach (var groupe in tous.GroupBy(j => j.Game.Id, StringComparer.Ordinal))
        {
            var possedees = groupe.ToList();
            if (!specs.TryGetValue(groupe.Key, out var spec) || possedees.Count == 1)
            {
                affichees.AddRange(possedees);
                continue;
            }

            bool Parle(MultiplayerInstalledGame j, string code) => spec.VersionDuContenu(j.ContentKey)?.Langues().Contains(code, StringComparer.Ordinal) == true;
            var choix = langue is null ? [] : possedees.Where(j => Parle(j, langue)).ToList();
            if (choix.Count == 0)
            {
                choix = possedees.Where(j => Parle(j, "en")).ToList();
            }

            if (choix.Count == 0 && spec.DefaultVersion is { } parDefaut)
            {
                choix = possedees.Where(j => string.Equals(spec.VersionDuContenu(j.ContentKey)?.Key, parDefaut, StringComparison.Ordinal)).ToList();
            }

            // UN SEUL EXEMPLAIRE DE CHAQUE JEU (user, 27/11) : parmi les versions retenues, la default_version, sinon la revision la plus haute,
            // sinon une version multilingue qui contient la langue, sinon la premiere par nom. Les autres restent jouables par invitation et reprise.
            var retenues = choix.Count > 0 ? choix : possedees;
            affichees.Add(retenues.Count == 1 ? retenues[0] : UnSeul(retenues, spec, langue));
        }

        return affichees;
    }

    private static readonly System.Text.RegularExpressions.Regex Revision = new(@"rev[\s_-]*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>La revision d'une version (« france-rev1 » : 1 ; sans « rev » : 0), lue de sa cle puis de son nom.</summary>
    internal static int RevisionDe(AdventureVersionDuJeu? version)
        => version is null ? 0 : new[] { version.Key, version.Label, version.FileHint }.Select(t => Revision.Match(t ?? string.Empty)).FirstOrDefault(m => m.Success) is { } trouvee
            && int.TryParse(trouvee.Groups[1].Value, out var n) ? n : 0;

    internal static MultiplayerInstalledGame UnSeul(IReadOnlyList<MultiplayerInstalledGame> retenues, AdventureSpec spec, string? langue)
    {
        if (spec.DefaultVersion is { } parDefaut && retenues.FirstOrDefault(j => string.Equals(spec.VersionDuContenu(j.ContentKey)?.Key, parDefaut, StringComparison.Ordinal)) is { } defaut)
        {
            return defaut;
        }

        var revisions = retenues.Select(j => RevisionDe(spec.VersionDuContenu(j.ContentKey))).ToList();
        var plusHaute = revisions.Max();
        var candidates = retenues.Where((j, i) => revisions[i] == plusHaute).ToList();
        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        if (langue is not null && candidates.Where(j => spec.VersionDuContenu(j.ContentKey) is { Languages.Count: > 1 } v && v.Langues().Contains(langue, StringComparer.Ordinal)).ToList() is { Count: > 0 } multilingues)
        {
            candidates = multilingues;
        }

        return candidates.OrderBy(j => spec.VersionDuContenu(j.ContentKey)?.Key ?? j.ContentKey, StringComparer.Ordinal).First();
    }

    public AdventureCollection(MultiplayerCollectionSyncService service)
    {
        Service = service;
    }

    public MultiplayerCollectionSyncService Service { get; }

    /// <summary>Les aventures jouables sur cette borne : le catalogue croise avec ce qu'elle possede.</summary>
    public IReadOnlyList<MultiplayerInstalledGame> Games => Service.JeuxConnus();

    /// <summary>Construit la collection avec ce que le conteneur sait (les memes services que MULTIPLAYER, un autre catalogue).</summary>
    public static AdventureCollection Creer(IServiceProvider sp)
    {
        var service = new MultiplayerCollectionSyncService(
            sp.GetRequiredService<IOptionsMonitor<ApiExposeOptions>>(),
            sp.GetRequiredService<AdventureCatalogueStore>().Interne,
            sp.GetRequiredService<InstalledGameCatalog>(),
            sp.GetRequiredService<EsCustomCollectionWriter>(),
            sp.GetRequiredService<EsCollectionThemeAssets>(),
            sp.GetRequiredService<MediaRuntimeState>(),
            sp.GetService<IEsSettingsChangeBus>(),
            sp.GetService<IEventBus>(),
            sp.GetService<ILogger<MultiplayerCollectionSyncService>>(),
            gamelists: sp.GetService<GamelistUpdateService>(),
            dataPack: sp.GetService<DataPackSyncService>(),
            paquets: sp.GetService<MultiplayerContenusDesPaquets>(),
            profil: ProfilDeLaBorne(sp.GetRequiredService<AdventureCatalogueStore>(), () => sp.GetService<CabinetLocale>()?.LangueDesJeux));
        return new AdventureCollection(service);
    }
}
