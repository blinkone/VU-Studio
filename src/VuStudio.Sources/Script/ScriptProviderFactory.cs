using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;
using static VuStudio.Sources.Common.Localized;

namespace VuStudio.Sources.Script;

/// <summary>
/// Creates <c>script</c> providers. They run in the user's session (the tray app), never in the service,
/// and can only be configured from the local machine.
/// </summary>
public sealed class ScriptProviderFactory : IProviderFactory
{
    public ProviderDescriptor Descriptor { get; } = new()
    {
        Type = "script",
        Name = L("Script", "Script"),
        Description = L(
            "Runs a command on an interval and turns its output (a number, key=value lines or JSON) into sensors.",
            "Esegue un comando a intervalli regolari e trasforma il suo output (un numero, righe chiave=valore o JSON) in sensori."),
        Icon = "console",
        Group = ProviderGroup.Utility,
        Singleton = false,
        AutoCreate = false,
        Platforms = PlatformSupport.Any,
        LocalConfigOnly = true,
        RunsInUserSession = true,
        Settings =
        [
            Setting.Select(
                "shell",
                L("Shell", "Shell"),
                "auto",
                [
                    Setting.Option("auto", "Automatic (PowerShell on Windows, bash elsewhere)", "Automatica (PowerShell su Windows, bash altrove)"),
                    Setting.Option("powershell", "Windows PowerShell", "Windows PowerShell"),
                    Setting.Option("pwsh", "PowerShell 7 (pwsh)", "PowerShell 7 (pwsh)"),
                    Setting.Option("cmd", "Command Prompt (cmd)", "Prompt dei comandi (cmd)"),
                    Setting.Option("bash", "bash", "bash"),
                    Setting.Option("sh", "sh", "sh"),
                    Setting.Option("none", "None (run the program directly)", "Nessuna (esegui direttamente il programma)"),
                ],
                L(
                    "Which program interprets the command. With \"None\" the first word is the program and the rest are its arguments.",
                    "Il programma che interpreta il comando. Con \"Nessuna\" la prima parola è il programma e il resto sono i suoi argomenti.")),
            Setting.Multiline(
                "command",
                L("Command", "Comando"),
                help: L(
                    "What to run. It runs as you, in your session, and never as the system service.",
                    "Cosa eseguire. Viene eseguito con il tuo utente, nella tua sessione, mai come servizio di sistema."),
                placeholder: "Get-Date -Format HH",
                required: true),
            Setting.Interval(
                ScriptOptions.DefaultIntervalSeconds,
                min: 1,
                help: L("Seconds between runs.", "Secondi tra un'esecuzione e l'altra.")),
            Setting.Seconds(
                "timeout",
                L("Timeout", "Timeout"),
                ScriptOptions.DefaultTimeoutSeconds,
                min: 0.5,
                max: 3600,
                help: L(
                    "The command and everything it started are stopped after this many seconds.",
                    "Il comando e tutto ciò che ha avviato vengono fermati dopo questi secondi.")),
            Setting.Select(
                "parse",
                L("Output format", "Formato dell'output"),
                "auto",
                [
                    Setting.Option("auto", "Automatic", "Automatico"),
                    Setting.Option("number", "A number", "Un numero"),
                    Setting.Option("keyvalue", "key=value lines", "Righe chiave=valore"),
                    Setting.Option("json", "JSON", "JSON"),
                ],
                L(
                    "Automatic recognises a single number (sensor \"value\"), a JSON document (one sensor per number or text, named like a/b) and key=value or key: value lines.",
                    "Automatico riconosce un singolo numero (sensore \"value\"), un documento JSON (un sensore per ogni numero o testo, con nome tipo a/b) e righe chiave=valore o chiave: valore.")),
            FieldSpecs.Table(
                L("Fields", "Campi"),
                L("Key or path", "Chiave o percorso"),
                L(
                    "A key of key=value output (\"cpu temp\"), or a path into JSON output ($.main.temp, items[0].value).",
                    "Una chiave dell'output chiave=valore (\"cpu temp\"), oppure un percorso nell'output JSON ($.main.temp, items[0].value)."),
                L(
                    "Optional. Without rows, every value in the output becomes a sensor; with rows, only the listed ones do, with the unit, range and category you give.",
                    "Facoltativo. Senza righe ogni valore dell'output diventa un sensore; con le righe solo quelli elencati, con unità, intervallo e categoria indicati.")),
            Setting.Text(
                "workingDirectory",
                L("Working directory", "Cartella di lavoro"),
                help: L("Where the command starts. Empty means the app's folder.", "Da dove parte il comando. Vuoto significa la cartella dell'app.")),
        ],
    };

    public ISensorProvider Create(ProviderInstanceConfig config, IServiceProvider services) =>
        new ScriptProvider(
            config.Id,
            ScriptOptions.From(new ProviderSettings(config.Settings, Descriptor)),
            string.IsNullOrWhiteSpace(config.Name) ? null : config.Name.Trim(),
            ProviderServices.Logger<ScriptProvider>(services));
}
