using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;
using static VuStudio.Sources.Common.Localized;

namespace VuStudio.Sources.Clock;

/// <summary>Creates the <c>clock</c> provider: time, date, calendar progress and countdowns.</summary>
public sealed class ClockProviderFactory : IProviderFactory
{
    public ProviderDescriptor Descriptor { get; } = new()
    {
        Type = "clock",
        Name = L("Clock", "Orologio"),
        Description = L(
            "Time and date, progress of the day, week, month and year, and countdowns to dates or times of day.",
            "Ora e data, avanzamento di giorno, settimana, mese e anno, e conti alla rovescia verso date o orari."),
        Icon = "clock-outline",
        Group = ProviderGroup.Utility,
        Singleton = true,
        AutoCreate = true,
        Platforms = PlatformSupport.Any,
        Settings =
        [
            Setting.Select(
                "weekStart",
                L("Week starts on", "La settimana inizia di"),
                "monday",
                [Setting.Option("monday", "Monday", "Lunedì"), Setting.Option("sunday", "Sunday", "Domenica")],
                L("Decides where the week progress sensor begins.", "Stabilisce da dove parte il sensore di avanzamento della settimana.")),
            Setting.Table(
                "countdowns",
                L("Countdowns", "Conti alla rovescia"),
                [
                    Setting.Text(
                        "id",
                        L("ID", "ID"),
                        help: L("Part of the sensor id: letters, digits and dashes.", "Parte dell'ID del sensore: lettere, cifre e trattini."),
                        required: true),
                    Setting.Text("name", L("Name", "Nome")),
                    Setting.Text(
                        "target",
                        L("Target", "Scadenza"),
                        help: L(
                            "A date and time such as 2026-12-25T18:00 (an offset like +01:00 or Z is optional), or a time of day such as 18:30 that repeats every day.",
                            "Una data e ora come 2026-12-25T18:00 (il fuso, ad esempio +01:00 o Z, è facoltativo), oppure un orario come 18:30 che si ripete ogni giorno."),
                        placeholder: "2026-12-25T18:00",
                        required: true),
                    Setting.Number(
                        "span",
                        L("Span (hours)", "Durata (ore)"),
                        Countdown.DefaultSpanHours,
                        min: 0.001,
                        help: L(
                            "The percent sensor falls from 100 % this many hours before the target to 0 % at the target.",
                            "Il sensore percentuale scende dal 100 %, tante ore prima della scadenza, allo 0 % alla scadenza.")),
                ],
                help: L(
                    "Each row publishes the seconds remaining and the remaining time as a percentage of its span.",
                    "Ogni riga pubblica i secondi rimanenti e il tempo rimanente come percentuale della sua durata.")),
            Setting.Interval(1, min: 0.1, max: 60),
        ],
    };

    public ISensorProvider Create(ProviderInstanceConfig config, IServiceProvider services) =>
        new ClockProvider(
            config.Id,
            new ProviderSettings(config.Settings, Descriptor),
            ProviderServices.Time(services),
            ProviderServices.Logger<ClockProvider>(services));
}
