using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VuStudio.Sources.Common;

/// <summary>
/// Resolves the shared services a provider needs from the <see cref="IServiceProvider"/> handed to
/// <c>IProviderFactory.Create</c>, with safe fallbacks so that providers also work in tests and in
/// hosts that registered nothing (tray app, agent).
/// </summary>
internal static class ProviderServices
{
    /// <summary>Name of the <see cref="IHttpClientFactory"/> client providers ask for.</summary>
    public const string HttpClientName = "vustudio";

    private static readonly Lazy<HttpClient> FallbackHttpClient = new(CreateFallbackClient);

    public static ILogger Logger<T>(IServiceProvider services) =>
        services.GetService<ILoggerFactory>()?.CreateLogger(typeof(T).FullName ?? typeof(T).Name) ?? NullLogger.Instance;

    public static TimeProvider Time(IServiceProvider services) =>
        services.GetService<TimeProvider>() ?? TimeProvider.System;

    /// <summary>
    /// An <see cref="HttpClient"/> from the factory, else a registered client, else a process-wide shared one.
    /// Callers must not dispose it and must not rely on <see cref="HttpClient.Timeout"/>; enforce timeouts per request.
    /// </summary>
    public static HttpClient Http(IServiceProvider services) =>
        services.GetService<IHttpClientFactory>()?.CreateClient(HttpClientName)
        ?? services.GetService<HttpClient>()
        ?? FallbackHttpClient.Value;

    private static HttpClient CreateFallbackClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
