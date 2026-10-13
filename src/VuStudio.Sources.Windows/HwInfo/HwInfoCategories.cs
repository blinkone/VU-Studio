using System.Text.RegularExpressions;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;

namespace VuStudio.Sources.Windows.HwInfo;

/// <summary>Category guess from HWiNFO sensor group names ("CPU [#0]: AMD Ryzen 9 7900X: Enhanced").</summary>
internal static partial class HwInfoCategories
{
    [GeneratedRegex(@"\((?:Nuvoton|ITE|Fintek|Winbond|ASPEED|SMSC|Microchip|Embedded Controller)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SuperIo();

    [GeneratedRegex(@"\b(?:ssd|nvme|hdd|disk|drive|s\.m\.a\.r\.t\.?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StorageWords();

    [GeneratedRegex(@"\b(?:ethernet|wi-?fi|wireless|network|lan)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NetworkWords();

    public static SensorCategory FromSensorName(string? sensorName)
    {
        var name = (sensorName ?? "").Trim();
        if (name.Length == 0)
        {
            return SensorCategory.System;
        }

        // HWiNFO names groups "<Group>: <details>". The group says what the sensor is; the details may contain
        // misleading words such as "WIFI" in a motherboard model.
        var colon = name.IndexOf(':');
        if (colon > 0 && ByGroup(name[..colon].Trim()) is { } byGroup)
        {
            return byGroup;
        }

        if (SuperIo().IsMatch(name) || Has(name, "motherboard") || Has(name, "mainboard") || Has(name, "super i/o"))
        {
            return SensorCategory.Motherboard;
        }

        if (Has(name, "battery"))
        {
            return SensorCategory.Battery;
        }

        if (Has(name, "memory") || Has(name, "dimm"))
        {
            return SensorCategory.Memory;
        }

        if (StorageWords().IsMatch(name))
        {
            return SensorCategory.Storage;
        }

        if (NetworkWords().IsMatch(name))
        {
            return SensorCategory.Network;
        }

        return SensorCategory.System;
    }

    private static SensorCategory? ByGroup(string group)
    {
        if (group.StartsWith("CPU", StringComparison.OrdinalIgnoreCase))
        {
            return SensorCategory.Cpu;
        }

        if (group.StartsWith("GPU", StringComparison.OrdinalIgnoreCase))
        {
            return SensorCategory.Gpu;
        }

        if (group.Equals("System", StringComparison.OrdinalIgnoreCase))
        {
            return SensorCategory.System;
        }

        if (group.StartsWith("Network", StringComparison.OrdinalIgnoreCase))
        {
            return SensorCategory.Network;
        }

        if (group.StartsWith("Battery", StringComparison.OrdinalIgnoreCase))
        {
            return SensorCategory.Battery;
        }

        if (group.StartsWith("Drive", StringComparison.OrdinalIgnoreCase) || group.StartsWith("S.M.A.R.T", StringComparison.OrdinalIgnoreCase))
        {
            return SensorCategory.Storage;
        }

        if (group.StartsWith("Memory", StringComparison.OrdinalIgnoreCase) || group.StartsWith("DIMM", StringComparison.OrdinalIgnoreCase))
        {
            return SensorCategory.Memory;
        }

        return null;
    }

    private static bool Has(string text, string word) => text.Contains(word, StringComparison.OrdinalIgnoreCase);
}

/// <summary>User-visible messages. The status API carries English text; Italian is kept alongside for the UI.</summary>
internal static class HwInfoMessages
{
    public static readonly LocalizedText NotRunning = new(
        "HWiNFO is not running, or its Shared Memory Support is turned off. Start HWiNFO with the Sensors window running and enable Shared Memory Support in its settings. The free edition switches Shared Memory off after 12 hours.",
        "HWiNFO non è in esecuzione oppure il supporto alla memoria condivisa è disattivato. Avvia HWiNFO con la finestra Sensori attiva e abilita il supporto alla memoria condivisa nelle impostazioni. L'edizione gratuita la disattiva dopo 12 ore.");

    public static readonly LocalizedText AccessDenied = new(
        "Windows denied access to HWiNFO's shared memory. Run the VU Studio service as SYSTEM or as an administrator.",
        "Windows ha negato l'accesso alla memoria condivisa di HWiNFO. Esegui il servizio VU Studio come SYSTEM o come amministratore.");

    public static readonly LocalizedText Stale = new(
        "HWiNFO stopped updating its shared memory. The free edition turns Shared Memory Support off after 12 hours: enable it again in HWiNFO's settings.",
        "HWiNFO ha smesso di aggiornare la memoria condivisa. L'edizione gratuita disattiva il supporto dopo 12 ore: riattivalo nelle impostazioni di HWiNFO.");

    public static readonly LocalizedText ShuttingDown = new(
        "HWiNFO has shut down its shared memory.",
        "HWiNFO ha chiuso la memoria condivisa.");

    public static readonly LocalizedText Empty = new(
        "HWiNFO's shared memory has no sensors yet. Make sure the Sensors window is running.",
        "La memoria condivisa di HWiNFO non contiene ancora sensori. Verifica che la finestra Sensori sia attiva.");

    public static readonly LocalizedText BadData = new(
        "HWiNFO's shared memory has an unexpected layout.",
        "La memoria condivisa di HWiNFO ha un formato inatteso.");

    public static readonly LocalizedText Unstable = new(
        "HWiNFO's shared memory kept changing while it was read; retrying.",
        "La memoria condivisa di HWiNFO continuava a cambiare durante la lettura; nuovo tentativo.");
}
