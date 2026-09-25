namespace RetroBat.Domain.Interfaces;

public interface IIngameSourceArbitrationService
{
    void MarkMameLuaSessionStarted(string systemId, string rom, string definitionFile);

    void MarkMameLuaSessionStopped(string systemId, string rom, string definitionFile);

    bool ShouldSuppressRetroArchWrapper(string systemId, string rom, string definitionFile);

    /// <summary>
    /// La session de FIN du wrapper doit-elle être écartée ? Oui quand le pont Lua de MAME mesure
    /// ce jeu, et encore une minute après la fin de sa session : les deux sessions de fin partent
    /// au même moment, dans un ordre qui n'est pas garanti. Sans cela, sous le cœur libretro MAME,
    /// une partie produisait deux sessions et pouvait être soumise deux fois.
    /// </summary>
    bool ShouldSuppressRetroArchWrapperSession(string systemId, string rom, string definitionFile);
}
