using System.Text.Json;
using System.Text.Json.Nodes;
using VuStudio.Core.Config;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;

namespace VuStudio.Server.Api;

public sealed class ConfigValidationException(IReadOnlyList<string> errors) : Exception(string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public sealed class ConfigForbiddenException(string message) : Exception(message);

/// <summary>Masks secrets for the API, restores them on save, validates and enforces local-only settings.</summary>
public sealed class ConfigService(ConfigStore store, ProviderManager providers)
{
    public const string SecretPlaceholder = "__secret__";
    public const string PasswordPlaceholder = "__set__";

    public AppConfig GetMasked()
    {
        var copy = store.GetCopy();
        foreach (var instance in copy.Providers)
        {
            if (providers.GetFactory(instance.Type)?.Descriptor is { } descriptor)
            {
                Mask(instance.Settings, descriptor.Settings);
            }
        }

        copy.Web.PasswordHash = copy.Web.PasswordHash is null ? null : PasswordPlaceholder;
        return copy;
    }

    public AppConfig Save(AppConfig incoming, long? baseRevision, bool local)
    {
        var current = store.Current;
        var currentProviders = current.Providers.ToDictionary(p => p.Id, StringComparer.Ordinal);

        foreach (var instance in incoming.Providers)
        {
            if (currentProviders.TryGetValue(instance.Id, out var previous) && previous.Type == instance.Type &&
                providers.GetFactory(instance.Type)?.Descriptor is { } descriptor)
            {
                Restore(instance.Settings, previous.Settings, descriptor.Settings);
            }
        }

        // The password is changed only through /api/auth/password.
        incoming.Web.PasswordHash = current.Web.PasswordHash;

        if (!local)
        {
            EnforceLocalOnly(current, incoming);
        }

        var errors = Validate(incoming);
        if (errors.Count > 0)
        {
            throw new ConfigValidationException(errors);
        }

        store.Replace(incoming, baseRevision);
        return GetMasked();
    }

    public List<string> Validate(AppConfig config)
    {
        var errors = new List<string>();

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in config.Providers)
        {
            if (!ProviderManager.ValidId().IsMatch(p.Id))
            {
                errors.Add($"Provider id '{p.Id}' is invalid (use lowercase letters, digits and '-').");
            }
            else if (p.Id == WellKnownSensors.SessionProvider)
            {
                errors.Add("Provider id 'session' is reserved.");
            }
            else if (!ids.Add(p.Id))
            {
                errors.Add($"Duplicate provider id '{p.Id}'.");
            }

            if (providers.GetFactory(p.Type) is { Descriptor: { } d })
            {
                if (d.Singleton && config.Providers.Count(x => x.Type == p.Type) > 1)
                {
                    errors.Add($"Only one '{p.Type}' source is allowed.");
                }

                foreach (var field in d.Settings.Where(f => f.Required))
                {
                    if (!p.Settings.TryGetPropertyValue(field.Key, out var v) || v is null || (v is JsonValue jv && jv.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s)))
                    {
                        errors.Add($"Source '{p.Name ?? p.Id}': '{field.Label.En}' is required.");
                    }
                }
            }
        }

        if (config.Profiles.Count == 0)
        {
            errors.Add("At least one profile is required.");
        }

        var profileIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in config.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id) || !profileIds.Add(profile.Id))
            {
                errors.Add($"Profile '{profile.Name}' has a missing or duplicate id.");
            }

            foreach (var group in profile.Dials.GroupBy(d => d.Slot).Where(g => g.Count() > 1 || g.Key < 1))
            {
                errors.Add($"Profile '{profile.Name}': slot {group.Key} is invalid or assigned twice.");
            }

            foreach (var assignment in profile.Dials)
            {
                if (assignment.Pages.GroupBy(p => p.Id).Any(g => g.Count() > 1))
                {
                    errors.Add($"Profile '{profile.Name}', slot {assignment.Slot}: page ids must be unique.");
                }
            }
        }

        foreach (var group in config.Hardware.Dials.GroupBy(d => d.Uid, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            errors.Add($"Dial {group.Key} is listed twice.");
        }

        foreach (var group in config.Hardware.Dials.GroupBy(d => d.Slot).Where(g => g.Count() > 1 || g.Key < 1))
        {
            errors.Add($"Slot {group.Key} is invalid or used by more than one dial.");
        }

        if (config.Web.Port is < 1024 or > 65535)
        {
            errors.Add("The web port must be between 1024 and 65535.");
        }

        if (config.Web.AllowLan && config.Web.PasswordHash is null)
        {
            errors.Add("Set a password before enabling LAN access.");
        }

        if (string.IsNullOrWhiteSpace(config.Web.ApiToken) || config.Web.ApiToken.Length < 16)
        {
            errors.Add("The API token must be at least 16 characters.");
        }

        return errors;
    }

    private void EnforceLocalOnly(AppConfig current, AppConfig incoming)
    {
        var localOnlyTypes = providers.Descriptors.Where(d => d.LocalConfigOnly).Select(d => d.Type).ToHashSet(StringComparer.Ordinal);
        string Fingerprint(AppConfig c) => JsonSerializer.Serialize(
            c.Providers.Where(p => localOnlyTypes.Contains(p.Type)).OrderBy(p => p.Id, StringComparer.Ordinal),
            Json.Compact);

        if (Fingerprint(current) != Fingerprint(incoming))
        {
            throw new ConfigForbiddenException("Scripts and other local-only sources can only be changed on this PC.");
        }
    }

    private static void Mask(JsonObject settings, IReadOnlyList<SettingField> fields)
    {
        foreach (var field in fields)
        {
            if (!settings.TryGetPropertyValue(field.Key, out var node) || node is null)
            {
                continue;
            }

            if (field.Kind == SettingKind.Secret && !IsEmpty(node))
            {
                settings[field.Key] = SecretPlaceholder;
            }
            else if (field.Kind == SettingKind.Table && field.Columns is { } columns && node is JsonArray rows)
            {
                foreach (var row in rows.OfType<JsonObject>())
                {
                    Mask(row, columns);
                }
            }
        }
    }

    private static void Restore(JsonObject incoming, JsonObject previous, IReadOnlyList<SettingField> fields)
    {
        foreach (var field in fields)
        {
            if (!incoming.TryGetPropertyValue(field.Key, out var node) || node is null)
            {
                continue;
            }

            if (field.Kind == SettingKind.Secret && node is JsonValue v && v.TryGetValue<string>(out var s) && s == SecretPlaceholder)
            {
                incoming[field.Key] = previous.TryGetPropertyValue(field.Key, out var old) ? old?.DeepClone() : null;
            }
            else if (field.Kind == SettingKind.Table && field.Columns is { } columns && node is JsonArray rows &&
                     previous.TryGetPropertyValue(field.Key, out var oldNode) && oldNode is JsonArray oldRows)
            {
                for (var i = 0; i < rows.Count; i++)
                {
                    if (rows[i] is JsonObject row && i < oldRows.Count && oldRows[i] is JsonObject oldRow)
                    {
                        Restore(row, oldRow, columns);
                    }
                }
            }
        }
    }

    private static bool IsEmpty(JsonNode node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && string.IsNullOrEmpty(s);
}
