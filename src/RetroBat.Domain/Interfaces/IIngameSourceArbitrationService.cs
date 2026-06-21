namespace RetroBat.Domain.Interfaces;

public interface IIngameSourceArbitrationService
{
    void MarkMameLuaSessionStarted(string systemId, string rom, string definitionFile);

    void MarkMameLuaSessionStopped(string systemId, string rom, string definitionFile);

    bool ShouldSuppressRetroArchWrapper(string systemId, string rom, string definitionFile);
}
