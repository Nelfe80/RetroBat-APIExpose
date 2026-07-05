namespace RetroBat.Domain.Interfaces;

public interface ITaskProgressService
{
    void Report(string taskId, string title, int current, int total, string? detail = null);
    void Complete(string taskId);
}
