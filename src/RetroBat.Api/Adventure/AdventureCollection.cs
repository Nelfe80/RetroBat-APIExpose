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

            affichees.AddRange(choix.Count > 0 ? choix : possedees);
        }

        return affichees;
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
