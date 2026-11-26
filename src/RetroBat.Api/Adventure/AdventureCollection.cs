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
            profil: Profil);
        return new AdventureCollection(service);
    }
}
