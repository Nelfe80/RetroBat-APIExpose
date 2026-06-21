using System.Reflection;

namespace RetroBat.Api.Infrastructure;

public static class ApiExposeVersion
{
    public static string Current { get; } =
        typeof(ApiExposeVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? typeof(ApiExposeVersion).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
