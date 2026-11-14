using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace GarRevival
{
    // protobuf-net drops empty collections, so they arrive as null. The
    // original servers always had something in them and the client iterates
    // many of them without null checks. Replace null lists, arrays and
    // dictionaries with empty ones throughout a JunkWars value object graph.
    internal static class VoNormalizer
    {
        public static void FillNullCollections(object root)
        {
            Fill(root, new HashSet<object>());
        }

        private static bool IsGameType(Type type)
        {
            return type.Namespace != null && type.Namespace.StartsWith("JunkWars", StringComparison.Ordinal);
        }

        private static void Fill(object obj, HashSet<object> visited)
        {
            if (obj == null || !IsGameType(obj.GetType()) || !visited.Add(obj))
            {
                return;
            }
            foreach (FieldInfo field in obj.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                Type type = field.FieldType;
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
                    else if (value is IDictionary)
                    {
                        FillItems(((IDictionary)value).Values, visited);
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
}
