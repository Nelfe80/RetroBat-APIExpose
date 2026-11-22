using System.Runtime.CompilerServices;
using RetroBat.Api.Deck.Canal;

namespace RetroBat.Api.Tests;

/// <summary>
/// Les profils d'affichage du canal (ram\&lt;systeme&gt;\&lt;jeu&gt;.VIEW.json) se lisent dans le dossier <c>ram</c> de la racine de l'API, qui
/// n'existe pas sous les tests : tous les essais lisent la copie du dossier <c>Profils</c> (celle que porte le Data Pack).
/// </summary>
internal static class CanalProfilsInit
{
    [ModuleInitializer]
    internal static void Init() => CanalProfils.DossierRam = Path.Combine(AppContext.BaseDirectory, "Profils", "ram");
}
