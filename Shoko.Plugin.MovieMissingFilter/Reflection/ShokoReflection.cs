using System.Collections;
using System.Reflection;

namespace Shoko.Plugin.MovieMissingFilter.Reflection;

internal static class ShokoReflection
{
    private const BindingFlags InstancePublic = BindingFlags.Instance | BindingFlags.Public;

    internal static object? Get(object? instance, string propertyName)
        => instance?.GetType().GetProperty(propertyName, InstancePublic)?.GetValue(instance);

    internal static int? GetInt(object? instance, string propertyName)
    {
        var value = Get(instance, propertyName);
        if (value is null)
            return null;

        try
        {
            return Convert.ToInt32(value);
        }
        catch
        {
            return null;
        }
    }

    internal static string? GetString(object? instance, string propertyName)
        => Get(instance, propertyName)?.ToString();

    internal static bool HasAny(object? value)
    {
        if (value is null)
            return false;

        if (value is ICollection collection)
            return collection.Count > 0;

        if (value is not IEnumerable enumerable)
            return false;

        var enumerator = enumerable.GetEnumerator();
        try
        {
            return enumerator.MoveNext();
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }
    }

    internal static IEnumerable<object> Enumerate(object? value)
    {
        if (value is not IEnumerable enumerable)
            yield break;

        foreach (var item in enumerable)
        {
            if (item is not null)
                yield return item;
        }
    }
}
