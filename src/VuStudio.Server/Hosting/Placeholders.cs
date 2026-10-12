using VuStudio.Core.Config;
using VuStudio.Core.Engine;
using VuStudio.Core.Faces;
using VuStudio.Core.Lighting;
using VuStudio.Core.Profiles;

namespace VuStudio.Server.Hosting;

// Temporary stand-ins used until the hardware and rendering modules are merged.

internal sealed class OfflineHardwareHost : IHardwareHost, IDialOutput
{
    public IDialOutput Output => this;

    public HubStatus Status { get; } = new(HubConnectionState.Disconnected, null, "Hardware module not available", false);

    public IReadOnlyList<DialInfo> Dials { get; } = [];

    public event Action? Changed;

    public Task StartAsync(HardwareSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;

    public void Apply(HardwareSettings settings) => Changed?.Invoke();

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void SetNeedle(string uid, double percent)
    {
    }

    public void SetBacklight(string uid, LedLevels levels)
    {
    }

    public void SetBacklightTransition(string uid, int milliseconds)
    {
    }

    public void SetNeedleEasing(string uid, Easing easing)
    {
    }

    public void SetFace(string uid, MonoBitmap face)
    {
    }

    public void Invalidate()
    {
    }

    public Task IdentifyAsync(string uid, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RescanAsync(bool provision, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<bool> SetRawAsync(string uid, int raw, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> CalibrateAsync(string uid, CalibrationPoint point, int raw, CancellationToken cancellationToken = default) => Task.FromResult(false);
}

internal sealed class PlaceholderRenderer : IFaceRenderer
{
    public IReadOnlyList<ThemeInfo> Themes { get; } = [];

    public IReadOnlyList<FontInfo> Fonts { get; } = [];

    public IReadOnlyList<IconInfo> Icons { get; } = [];

    public FaceDocument BuildDocument(FaceConfig config, FaceRenderContext context) => config.Custom ?? new FaceDocument();

    public MonoBitmap Render(FaceDocument document, FaceRenderContext context) => new();

    public byte[] EncodePng(MonoBitmap bitmap, int scale = 1, RgbwColor? tint = null) => [];
}
