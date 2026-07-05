namespace RetroBat.Domain.Interfaces;

public interface IStartupOverlayService
{
    void MarkStartupBootstrapCompleted(bool awaitingFirstReload);
    void NotifyReloadSucceeded();
    bool IsStartupActiveOrRecentlyCompleted(TimeSpan recentWindow);
    void UpdateStartupProgress(string messageKey, int current, int total, string? detail = null);
}
