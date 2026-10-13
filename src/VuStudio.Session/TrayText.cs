using System.Globalization;

namespace VuStudio.Session;

/// <summary>Strings of the desktop app (tray menu, window, balloons) in English and Italian.</summary>
public sealed class TrayText
{
    private static readonly Dictionary<string, (string En, string It)> Strings = new(StringComparer.Ordinal)
    {
        ["App"] = ("VU Studio", "VU Studio"),
        ["Open"] = ("Open VU Studio", "Apri VU Studio"),
        ["OpenInBrowser"] = ("Open in browser", "Apri nel browser"),
        ["Profiles"] = ("Profiles", "Profili"),
        ["Automatic"] = ("Automatic", "Automatico"),
        ["AutomaticActive"] = ("Automatic ({0})", "Automatico ({0})"),
        ["NoProfiles"] = ("No profiles", "Nessun profilo"),
        ["Pause"] = ("Pause", "Pausa"),
        ["Resume"] = ("Resume", "Riprendi"),
        ["RefreshDials"] = ("Refresh dials", "Aggiorna quadranti"),
        ["StartWithWindows"] = ("Start with Windows", "Avvia con Windows"),
        ["About"] = ("About", "Informazioni"),
        ["Exit"] = ("Exit", "Esci"),
        ["StartService"] = ("Start service", "Avvia servizio"),
        ["Retry"] = ("Retry", "Riprova"),
        ["TooltipConnected"] = ("VU Studio", "VU Studio"),
        ["TooltipPaused"] = ("VU Studio (paused)", "VU Studio (in pausa)"),
        ["TooltipOffline"] = ("VU Studio: service not running", "VU Studio: servizio non attivo"),
        ["ServiceOffline"] = ("The VU Studio service is not running.", "Il servizio VU Studio non è attivo."),
        ["ServiceOfflineHint"] = (
            "Start the service to control the dials. The window reconnects automatically.",
            "Avvia il servizio per controllare i quadranti. La finestra si ricollega da sola."),
        ["ServiceStarting"] = ("Starting the service…", "Avvio del servizio…"),
        ["ServiceStartCancelled"] = ("The service was not started (permission denied).", "Il servizio non è stato avviato (autorizzazione negata)."),
        ["ServiceNotInstalled"] = ("The VU Studio service is not installed. Reinstall VU Studio.", "Il servizio VU Studio non è installato. Reinstalla VU Studio."),
        ["ServiceStartFailed"] = ("The service could not be started (code {0}).", "Impossibile avviare il servizio (codice {0})."),
        ["ActionFailed"] = ("The service did not accept the request: {0}", "Il servizio non ha accettato la richiesta: {0}"),
        ["StartupManagedByInstaller"] = (
            "VU Studio starts for all users (set by the installer). Change it in Task Manager > Startup apps.",
            "VU Studio si avvia per tutti gli utenti (impostato dall'installazione). Modificalo in Gestione attività > App di avvio."),
        ["StartupFailed"] = ("Could not change the startup setting: {0}", "Impossibile modificare l'avvio automatico: {0}"),
        ["WebView2Missing"] = ("The Microsoft Edge WebView2 Runtime is not installed.", "Microsoft Edge WebView2 Runtime non è installato."),
        ["WebView2MissingHint"] = (
            "VU Studio needs it to show its window. Install it, then reopen this window, or use VU Studio in your browser.",
            "VU Studio ne ha bisogno per mostrare la finestra. Installalo e riapri questa finestra, oppure usa VU Studio nel browser."),
        ["WebView2Download"] = ("Download WebView2 Runtime", "Scarica WebView2 Runtime"),
        ["WebView2Failed"] = ("The embedded browser could not start: {0}", "Il browser integrato non si è avviato: {0}"),
        ["AboutText"] = (
            "VU Studio {0}\nDrives Streacom VU1 dials with data from this PC, the LAN and online sources.\n\nService: {1}\n\nFree software under the GNU GPL v3 or later.\n{2}",
            "VU Studio {0}\nControlla i quadranti Streacom VU1 con i dati di questo PC, della rete locale e di fonti online.\n\nServizio: {1}\n\nSoftware libero con licenza GNU GPL v3 o successiva.\n{2}"),
        ["NotConnected"] = ("not connected", "non connesso"),
    };

    public TrayText(CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;
        Italian = culture.TwoLetterISOLanguageName.Equals("it", StringComparison.OrdinalIgnoreCase);
    }

    public bool Italian { get; }

    public static IReadOnlyCollection<string> Keys => Strings.Keys;

    public string this[string key] => Strings.TryGetValue(key, out var s) ? (Italian ? s.It : s.En) : key;

    public string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, this[key], args);

    internal static (string En, string It) Raw(string key) => Strings[key];
}
