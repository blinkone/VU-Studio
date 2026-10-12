using System.Reflection;

namespace VuStudio.Core.Providers;

/// <summary>
/// Discovers provider factories by reflection so that adding a provider never requires editing a
/// shared registration file: any public, non-abstract <see cref="IProviderFactory"/> with a public
/// parameterless constructor in the scanned assemblies is picked up.
/// </summary>
public static class ProviderCatalog
{
    public static IReadOnlyList<IProviderFactory> Discover(params Assembly[] assemblies)
    {
        var factories = new List<IProviderFactory>();
        foreach (var assembly in assemblies.Distinct())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).ToArray()!;
            }

            foreach (var type in types)
            {
                if (type is { IsAbstract: false, IsInterface: false, IsPublic: true } &&
                    typeof(IProviderFactory).IsAssignableFrom(type) &&
                    type.GetConstructor(Type.EmptyTypes) is not null)
                {
                    factories.Add((IProviderFactory)Activator.CreateInstance(type)!);
                }
            }
        }

        return factories
            .GroupBy(f => f.Descriptor.Type, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(f => f.Descriptor.Type, StringComparer.Ordinal)
            .ToList();
    }
}
