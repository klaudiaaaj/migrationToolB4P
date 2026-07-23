using System.Reflection;

namespace MigrationTool.Core.Runtime;

public sealed record RuntimeMigration(long Version, string Name, Type MigrationType);

public static class AssemblyMigrationDiscovery
{
    public static IReadOnlyList<RuntimeMigration> Discover(Assembly assembly)
    {
        var migrations = new List<RuntimeMigration>();

        foreach (var type in GetLoadableTypes(assembly))
        {
            if (!type.IsClass || type.IsAbstract)
            {
                continue;
            }

            var attribute = type.CustomAttributes.SingleOrDefault(IsMigrationAttribute);
            if (attribute is null || attribute.ConstructorArguments.Count == 0)
            {
                continue;
            }

            var rawVersion = attribute.ConstructorArguments[0].Value;
            if (rawVersion is null)
            {
                continue;
            }

            var version = Convert.ToInt64(rawVersion, System.Globalization.CultureInfo.InvariantCulture);
            migrations.Add(new RuntimeMigration(version, type.FullName ?? type.Name, type));
        }

        var duplicate = migrations.GroupBy(x => x.Version).FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Assembly zawiera więcej niż jedną migrację o wersji {duplicate.Key}: " +
                string.Join(", ", duplicate.Select(x => x.Name)));
        }

        return migrations.OrderBy(x => x.Version).ToArray();
    }

    private static bool IsMigrationAttribute(CustomAttributeData attribute)
        => string.Equals(attribute.AttributeType.Name, "MigrationAttribute", StringComparison.Ordinal) &&
           string.Equals(attribute.AttributeType.Namespace, "FluentMigrator", StringComparison.Ordinal);

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }
}
