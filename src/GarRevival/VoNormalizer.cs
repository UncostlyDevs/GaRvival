using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace GarRevival;

// protobuf-net drops empty collections, so they arrive as null, and the client iterates many of
// them without null checks. Replaces null lists, arrays and dictionaries in a JunkWars VO graph with empty ones.
internal static class VoNormalizer
{
    public static void FillNullCollections(object root) => Fill(root, new HashSet<object>());

    private static bool IsGameType(Type type) => type.Namespace != null && type.Namespace.StartsWith("JunkWars", StringComparison.Ordinal);

    private static void Fill(object obj, HashSet<object> visited)
    {
        if (obj == null || !IsGameType(obj.GetType()) || !visited.Add(obj)) return;
        foreach (var field in obj.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var type = field.FieldType;
            object value = field.GetValue(obj);
            if (type.IsArray)
            {
                if (value == null)
                {
                    field.SetValue(obj, Array.CreateInstance(type.GetElementType(), 0));
                }
                else
                {
                    FillItems((IEnumerable)value, visited);
                }
            }
            else if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(List<>) || type.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
            {
                if (value == null)
                {
                    field.SetValue(obj, Activator.CreateInstance(type));
                }
                else if (value is IDictionary dictionary)
                {
                    FillItems(dictionary.Values, visited);
                }
                else
                {
                    FillItems((IEnumerable)value, visited);
                }
            }
            else if (!type.IsValueType)
            {
                Fill(value, visited);
            }
        }
    }

    private static void FillItems(IEnumerable items, HashSet<object> visited)
    {
        foreach (object item in items)
        {
            Fill(item, visited);
        }
    }
}
