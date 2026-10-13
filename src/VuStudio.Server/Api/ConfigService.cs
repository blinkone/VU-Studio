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

        var secretErrors = new List<string>();
        foreach (var instance in incoming.Providers)
        {
            currentProviders.TryGetValue(instance.Id, out var previous);
            var sameType = previous is not null && previous.Type == instance.Type;
            if (providers.GetFactory(instance.Type)?.Descriptor is { } descriptor)
            {
                RestoreSecrets(instance, sameType ? previous : null, descriptor, secretErrors);
            }

            // Who changed the provider last is decided here, never by the client.
            instance.ChangedRemotely = sameType && JsonNode.DeepEquals(instance.Settings, previous!.Settings)
                ? previous.ChangedRemotely
                : !local;
        }

        if (secretErrors.Count > 0)
        {
            throw new ConfigValidationException(secretErrors);
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
        // LAN access, the port, the accepted host names, the password and the API token decide who can reach the
        // service at all: only someone sitting at this PC may change them.
        if (!SameWebSettings(current.Web, incoming.Web))
        {
            throw new ConfigForbiddenException(
                "Web access settings (LAN access, port, allowed host names, password and API token) can only be changed on this PC.");
        }

        var localOnlyTypes = providers.Descriptors.Where(d => d.LocalConfigOnly).Select(d => d.Type).ToHashSet(StringComparer.Ordinal);
        string Fingerprint(AppConfig c) => JsonSerializer.Serialize(
            c.Providers.Where(p => localOnlyTypes.Contains(p.Type)).OrderBy(p => p.Id, StringComparer.Ordinal),
            Json.Compact);

        if (Fingerprint(current) != Fingerprint(incoming))
        {
            throw new ConfigForbiddenException("Scripts and other local-only sources can only be changed on this PC.");
        }
    }

    /// <summary>Every <c>web.*</c> setting, compared as JSON so new properties are covered automatically.</summary>
    internal static bool SameWebSettings(WebSettings a, WebSettings b) =>
        JsonSerializer.Serialize(a, Json.Compact) == JsonSerializer.Serialize(b, Json.Compact);

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

    /// <summary>
    /// Puts the stored value back in place of every <see cref="SecretPlaceholder"/>, but only when the secret would go
    /// to the same place as before: same provider id and type, and the same values for every non-secret setting that
    /// could redirect it (see <see cref="IsCompared"/>). Table rows are matched by their non-secret columns, not by
    /// position. Anything else is reported in <paramref name="errors"/> so the user re-enters the secret.
    /// </summary>
    internal static void RestoreSecrets(ProviderInstanceConfig instance, ProviderInstanceConfig? previous, ProviderDescriptor descriptor, List<string> errors)
    {
        var label = instance.Name is { Length: > 0 } n ? n : instance.Id;
        var scopeUnchanged = previous is not null && SameScope(instance.Settings, previous.Settings, descriptor.Settings);
        foreach (var field in descriptor.Settings)
        {
            if (!instance.Settings.TryGetPropertyValue(field.Key, out var node) || node is null)
            {
                continue;
            }

            if (field.Kind == SettingKind.Secret && IsPlaceholder(node))
            {
                if (scopeUnchanged && previous!.Settings.TryGetPropertyValue(field.Key, out var old) && old is not null && !IsEmpty(old))
                {
                    instance.Settings[field.Key] = old.DeepClone();
                }
                else
                {
                    errors.Add($"Source '{label}': re-enter the secret '{field.Label.En}' (the address or another setting it depends on changed).");
                }
            }
            else if (field.Kind == SettingKind.Table && field.Columns is { } columns && node is JsonArray rows &&
                     columns.Any(c => c.Kind == SettingKind.Secret))
            {
                var available = new List<JsonObject>();
                if (scopeUnchanged && previous!.Settings.TryGetPropertyValue(field.Key, out var oldNode) && oldNode is JsonArray oldRows)
                {
                    available.AddRange(oldRows.OfType<JsonObject>());
                }

                var rowNumber = 0;
                foreach (var row in rows.OfType<JsonObject>())
                {
                    rowNumber++;
                    if (!columns.Any(c => c.Kind == SettingKind.Secret && row.TryGetPropertyValue(c.Key, out var v) && v is not null && IsPlaceholder(v)))
                    {
                        continue;
                    }

                    var match = available.FirstOrDefault(old => SameScope(row, old, columns));
                    if (match is not null)
                    {
                        available.Remove(match); // two identical rows each take their own stored secret
                    }

                    foreach (var column in columns.Where(c => c.Kind == SettingKind.Secret))
                    {
                        if (!row.TryGetPropertyValue(column.Key, out var value) || value is null || !IsPlaceholder(value))
                        {
                            continue;
                        }

                        if (match is not null && match.TryGetPropertyValue(column.Key, out var old) && old is not null && !IsEmpty(old))
                        {
                            row[column.Key] = old.DeepClone();
                        }
                        else
                        {
                            errors.Add($"Source '{label}', {field.Label.En} row {rowNumber}: re-enter the secret '{column.Label.En}'.");
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Settings compared before a stored secret is reused: every non-secret value (text, address, number, switch,
    /// choice, multi-line text). Timing fields, lists and tables (sensor selections; rows with secrets are matched on
    /// their own) cannot send a top-level secret anywhere else, so changing them does not force the user to re-enter it.
    /// </summary>
    internal static bool IsCompared(SettingField field) => field.Kind switch
    {
        SettingKind.Secret or SettingKind.Seconds or SettingKind.TextList or SettingKind.Table => false,
        _ => true,
    };

    private static bool SameScope(JsonObject a, JsonObject b, IReadOnlyList<SettingField> fields) =>
        fields.Where(IsCompared).All(f => SameValue(Effective(a, f), Effective(b, f)));

    private static JsonNode? Effective(JsonObject settings, SettingField field) =>
        settings.TryGetPropertyValue(field.Key, out var node) && node is not null ? node : field.Default;

    private static bool SameValue(JsonNode? a, JsonNode? b)
    {
        if (a is JsonValue va && b is JsonValue vb && va.TryGetValue<double>(out var da) && vb.TryGetValue<double>(out var db))
        {
            return da.Equals(db);
        }

        if (a is JsonValue sa && b is JsonValue sb && sa.TryGetValue<string>(out var ta) && sb.TryGetValue<string>(out var tb))
        {
            return string.Equals(ta.Trim(), tb.Trim(), StringComparison.Ordinal);
        }

        return JsonNode.DeepEquals(a, b) || (IsBlank(a) && IsBlank(b));
    }

    private static bool IsBlank(JsonNode? node) => node is null || IsEmpty(node);

    private static bool IsPlaceholder(JsonNode node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && s == SecretPlaceholder;

    private static bool IsEmpty(JsonNode node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && string.IsNullOrEmpty(s);
}
