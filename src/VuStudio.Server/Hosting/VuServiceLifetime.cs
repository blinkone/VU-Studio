using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using VuStudio.Core.Engine;
using VuStudio.Server.Live;

namespace VuStudio.Server.Hosting;

/// <summary>
/// Windows service lifetime that also forwards power (suspend/resume) and session (lock/unlock)
/// events to the engine, so dials go dark when the PC sleeps or the user locks the screen even
/// before the tray app reports it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VuServiceLifetime : WindowsServiceLifetime
{
    private readonly IServiceProvider _services;

    public VuServiceLifetime(
        IHostEnvironment environment,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor,
        IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor,
        IServiceProvider services)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor)
    {
        _services = services;
        CanHandlePowerEvent = true;
        CanHandleSessionChangeEvent = true;
        CanShutdown = true;
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        var engine = _services.GetService<DialEngine>();
        switch (powerStatus)
        {
            case PowerBroadcastStatus.Suspend:
                engine?.SetSuspended(true);
                break;
            case PowerBroadcastStatus.ResumeSuspend:
            case PowerBroadcastStatus.ResumeAutomatic:
                engine?.SetSuspended(false);
                break;
        }

        return true;
    }

    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        var session = _services.GetService<SessionState>();
        switch (changeDescription.Reason)
        {
            case SessionChangeReason.SessionLock:
                session?.SetLocked(true);
                break;
            case SessionChangeReason.SessionUnlock:
            case SessionChangeReason.SessionLogon:
                session?.SetLocked(false);
                break;
        }

        base.OnSessionChange(changeDescription);
    }

    protected override void OnShutdown()
    {
        _services.GetService<DialEngine>()?.SetSuspended(true);
        base.OnShutdown();
    }
}
