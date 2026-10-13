using VuStudio.Core.Config;
using VuStudio.Core.Engine;
using VuStudio.Core.Providers;

namespace VuStudio.Server.Api;

public sealed record StateDto(
    long Revision,
    EngineSnapshot Engine,
    HubStatus Hub,
    IReadOnlyList<DialInfo> Hardware,
    IReadOnlyList<ProviderStatusInfo> Providers);

/// <summary>Builds the state document shared by <c>GET /api/state</c> and the live WebSocket.</summary>
public sealed class StateBuilder(DialEngine engine, IDialOutput output, ProviderManager providers, ConfigStore config)
{
    public StateDto Build() => new(
        config.Current.Revision,
        engine.Snapshot,
        output.Status,
        output.Dials,
        providers.GetStatus());
}
