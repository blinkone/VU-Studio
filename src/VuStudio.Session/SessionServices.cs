using Microsoft.Extensions.Logging;

namespace VuStudio.Session;

/// <summary>
/// The small service container handed to <see cref="Core.Providers.IProviderFactory.Create"/> in the tray:
/// <see cref="ILoggerFactory"/>, <see cref="ILogger{T}"/>, <see cref="TimeProvider"/> and any extra instances.
/// The tray has no DI container and needs none for the user-session providers.
/// </summary>
public sealed class SessionServices : IServiceProvider
{
    private readonly Dictionary<Type, object> _services = new();
    private readonly ILoggerFactory _loggerFactory;

    public SessionServices(ILoggerFactory loggerFactory, TimeProvider? time = null, IEnumerable<KeyValuePair<Type, object>>? extra = null)
    {
        _loggerFactory = loggerFactory;
        _services[typeof(ILoggerFactory)] = loggerFactory;
        _services[typeof(TimeProvider)] = time ?? TimeProvider.System;
        foreach (var (type, instance) in extra ?? [])
        {
            _services[type] = instance;
        }
    }

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(IServiceProvider))
        {
            return this;
        }

        if (_services.TryGetValue(serviceType, out var service))
        {
            return service;
        }

        if (serviceType.IsGenericType && serviceType.GetGenericTypeDefinition() == typeof(ILogger<>))
        {
            return Activator.CreateInstance(typeof(Logger<>).MakeGenericType(serviceType.GenericTypeArguments[0]), _loggerFactory);
        }

        return null;
    }
}
