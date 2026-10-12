using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace VuStudio.Server.Security;

/// <summary>In-memory login sessions for LAN clients (cookie <see cref="CookieName"/>).</summary>
public sealed class AuthSessions(TimeProvider time)
{
    public const string CookieName = "vustudio_session";

    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset Since)> _failures = new(StringComparer.Ordinal);

    public string Create()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _sessions[token] = time.GetUtcNow() + Lifetime;
        return token;
    }

    public bool IsValid(string? token)
    {
        if (string.IsNullOrEmpty(token) || !_sessions.TryGetValue(token, out var expires))
        {
            return false;
        }

        if (expires < time.GetUtcNow())
        {
            _sessions.TryRemove(token, out _);
            return false;
        }

        return true;
    }

    public void Revoke(string? token)
    {
        if (token is not null)
        {
            _sessions.TryRemove(token, out _);
        }
    }

    /// <summary>Drops every session (e.g. after a password change).</summary>
    public void RevokeAll() => _sessions.Clear();

    /// <summary>Simple brute-force brake: at most 10 failed logins per client per 15 minutes.</summary>
    public bool IsLockedOut(string client)
    {
        if (!_failures.TryGetValue(client, out var entry))
        {
            return false;
        }

        if (time.GetUtcNow() - entry.Since > TimeSpan.FromMinutes(15))
        {
            _failures.TryRemove(client, out _);
            return false;
        }

        return entry.Count >= 10;
    }

    public void RecordFailure(string client) =>
        _failures.AddOrUpdate(client, _ => (1, time.GetUtcNow()), (_, e) => (e.Count + 1, e.Since));

    public void RecordSuccess(string client) => _failures.TryRemove(client, out _);
}
