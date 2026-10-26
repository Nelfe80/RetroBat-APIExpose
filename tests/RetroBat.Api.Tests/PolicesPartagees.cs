using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Les tests qui dessinent avec les polices des ecrans MULTIPLAYER ne tournent pas en meme temps : changer
/// <c>PolicesDesEcrans.Dossier</c> libere la collection de polices, et un dessin en cours dans une autre classe echouait
/// (une fois sur trois environ, 26/10).
/// </summary>
[CollectionDefinition(Nom)]
public sealed class PolicesPartagees
{
    public const string Nom = "Polices des ecrans";
}
