using Microsoft.Extensions.Options;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Infrastructure;

public sealed class RuntimeConfigSnapshotProvider<TOptions> : IRuntimeConfigSnapshotProvider<TOptions>
    where TOptions : class
{
    private readonly IOptionsMonitor<TOptions> _options;

    public RuntimeConfigSnapshotProvider(IOptionsMonitor<TOptions> options)
    {
        _options = options;
    }

    public TOptions Current => _options.CurrentValue;
}
